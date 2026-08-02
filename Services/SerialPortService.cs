using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Ports;
using System.Text;
using System.Threading;

// 本文件位于 namespace SerialPort.Services 内，根命名空间 SerialPort 会遮蔽
// System.IO.Ports.SerialPort 类型（命名空间成员优先于 using 导入），故用别名引用。
using SerialPortType = System.IO.Ports.SerialPort;

namespace SerialPort.Services
{
    /// <summary>
    /// 串口服务：封装 System.IO.Ports.SerialPort 的连接、收发与生命周期管理。
    /// 所有接收数据通过事件 <see cref="DataReceived"/> 推送给 UI 层。
    /// </summary>
    public sealed class SerialPortService : IDisposable
    {
        private readonly SerialPortType _port;
        private readonly SynchronizationContext _syncContext;
        private bool _disposed;

        /// <summary>当接收到数据时触发（已在 UI 线程上回调）。</summary>
        public event EventHandler<DataReceivedEventArgs> DataReceived;

        /// <summary>当串口打开/关闭状态变化时触发（已在 UI 线程上回调）。</summary>
        public event EventHandler<bool> ConnectionChanged;

        public bool IsOpen => _port?.IsOpen ?? false;

        /// <summary>当前端口名称（未打开时为空字符串）。</summary>
        public string PortName => _port?.PortName ?? string.Empty;

        /// <summary>后台读取发现设备已不可用（拔出等）时触发（已在 UI 线程上回调）。</summary>
        public event EventHandler PortGone;

        public SerialPortService()
        {
            _port = new SerialPortType
            {
                // 默认参数，实际由 UI 通过 Open 传入
                ReadTimeout = 3000,
                WriteTimeout = 3000,
                // 启用基础流，便于 DataReceived 工作
                DtrEnable = true,
                RtsEnable = false
            };
            _port.DataReceived += OnSerialDataReceived;

            // 捕获当前同步上下文，用于把后台线程事件切回 UI 线程
            _syncContext = SynchronizationContext.Current ?? new SynchronizationContext();
        }

        /// <summary>获取系统中所有可用串口名称。</summary>
        public static string[] GetAvailablePorts() => SerialPortType.GetPortNames();

        /// <summary>已连接端口是否仍存在于给定端口集合中（设备拔出检测）。</summary>
        public bool IsOpenPortPresent(string[] availablePorts) =>
            !IsOpen || Array.IndexOf(availablePorts, PortName) >= 0;

        /// <summary>
        /// 按指定参数打开串口。
        /// </summary>
        public void Open(SerialPortConfig config)
        {
            if (_port.IsOpen) return;

            _port.PortName = config.PortName;
            _port.BaudRate = (int)config.BaudRate;
            _port.DataBits = (int)config.DataBits;
            _port.StopBits = config.StopBits;
            _port.Parity = config.Parity;
            _port.Handshake = config.Handshake;

            _port.Open();
            ConnectionChanged?.Invoke(this, true);
        }

        /// <summary>关闭串口。设备已拔出时 Close 可能抛异常，try/finally 保证断开事件总是发出。</summary>
        public void Close()
        {
            if (!_port.IsOpen) return;
            try
            {
                _port.Close();
            }
            catch
            {
                // 设备已拔出等场景下关闭可能失败：忽略，继续发出断开事件
            }
            finally
            {
                ConnectionChanged?.Invoke(this, false);
            }
        }

        /// <summary>以文本方式发送数据。</summary>
        public void SendText(string text, Encoding encoding)
        {
            if (!_port.IsOpen) throw new InvalidOperationException("串口未打开。");
            byte[] buffer = encoding.GetBytes(text);
            try
            {
                _port.BaseStream.Write(buffer, 0, buffer.Length);
            }
            catch (Exception ex)
            {
                // 设备已拔出（COM 号复用等）时写入抛 IO 类异常：广播 PortGone 后静默，由 UI 提示
                if (HandleDeviceError(ex)) return;
                throw;
            }
        }

        /// <summary>以十六进制字节方式发送数据。</summary>
        public void SendBytes(byte[] data)
        {
            if (!_port.IsOpen) throw new InvalidOperationException("串口未打开。");
            try
            {
                _port.BaseStream.Write(data, 0, data.Length);
            }
            catch (Exception ex)
            {
                // 设备已拔出（COM 号复用等）时写入抛 IO 类异常：广播 PortGone 后静默，由 UI 提示
                if (HandleDeviceError(ex)) return;
                throw;
            }
        }

        /// <summary>清空输入缓冲区。</summary>
        public void DiscardInBuffer()
        {
            if (_port.IsOpen) _port.BaseStream.Flush();
        }

        private void OnSerialDataReceived(object sender, SerialDataReceivedEventArgs e)
        {
            try
            {
                if (_port == null || !_port.IsOpen) return;
                int bytes = _port.BytesToRead;
                if (bytes <= 0) return;

                byte[] buffer = new byte[bytes];
                _port.BaseStream.Read(buffer, 0, bytes);

                // 切回 UI 线程
                Post(() => DataReceived?.Invoke(this, new DataReceivedEventArgs(buffer)));
            }
            catch (Exception ex)
            {
                // 设备拔出但端口名未消失（COM 号被复用）时，读取会抛 IO 类异常；
                // 通过 HandleDeviceError 通知 UI 层自动断开，避免状态停留在"已连接"
                HandleDeviceError(ex);
                // 其余异常静默处理，避免后台线程崩溃
            }
        }

        /// <summary>
        /// 统一判定"设备已不可用"（拔出但端口名未消失等）：端口仍打开且异常为 IO 类时，
        /// 通过 <see cref="PortGone"/> 事件通知 UI 层（已在 UI 线程上回调）；返回是否已按此处理。
        /// 收发两条路径的设备失效判定都走这里，判定标准只需维护一处。
        /// </summary>
        private bool HandleDeviceError(Exception ex)
        {
            if (_port == null || !_port.IsOpen || !(ex is IOException || ex is UnauthorizedAccessException))
                return false;
            Post(() => PortGone?.Invoke(this, EventArgs.Empty));
            return true;
        }

        /// <summary>把动作切回 UI 线程执行（后台线程事件回调统一走此入口）。</summary>
        private void Post(Action action) => _syncContext.Post(_ => action(), null);

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try
            {
                if (_port != null)
                {
                    _port.DataReceived -= OnSerialDataReceived;
                    if (_port.IsOpen) _port.Close();
                    _port.Dispose();
                }
            }
            catch
            {
            }
        }
    }

    /// <summary>串口配置参数。</summary>
    public sealed class SerialPortConfig
    {
        public string PortName { get; set; } = "COM1";
        public BaudRate BaudRate { get; set; } = BaudRate.Baud9600;
        public DataBits DataBits { get; set; } = DataBits.Eight;
        public StopBits StopBits { get; set; } = System.IO.Ports.StopBits.One;
        public Parity Parity { get; set; } = Parity.None;
        public Handshake Handshake { get; set; } = Handshake.None;
    }

    /// <summary>常用波特率枚举（便于 UI 绑定）。</summary>
    public enum BaudRate
    {
        Baud1200 = 1200,
        Baud2400 = 2400,
        Baud4800 = 4800,
        Baud9600 = 9600,
        Baud19200 = 19200,
        Baud38400 = 38400,
        Baud57600 = 57600,
        Baud115200 = 115200
    }

    /// <summary>数据位枚举。</summary>
    public enum DataBits
    {
        Five = 5,
        Six = 6,
        Seven = 7,
        Eight = 8
    }

    /// <summary>数据接收事件参数。</summary>
    public sealed class DataReceivedEventArgs : EventArgs
    {
        public byte[] Data { get; }

        public DataReceivedEventArgs(byte[] data)
        {
            Data = data;
        }
    }
}
