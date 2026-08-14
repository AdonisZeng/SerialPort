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
        private SerialPortType _port;              // 物理端口实例（可空：未打开 / 重建中）
        private SerialPortConfig _config;          // 逻辑连接配置（非空 = 逻辑已连接）
        private readonly SynchronizationContext _syncContext;
        private bool _disposed;

        // 保护 _port 的跨线程读写（后台接收回调 vs UI 线程重建/关闭）
        private readonly object _portLock = new object();

        // 接收攒批：后台线程累积数据合并成块，一次 Post 到 UI，降低高频数据下的 UI 积压
        private readonly object _pendingLock = new object();
        private MemoryStream _pendingData;         // 未提交的接收数据（非空 = 已有在途 Post 将取走）
        private SerialPortType _pendingPort;       // 数据所属端口实例（Post 后用于实例守卫）

        /// <summary>攒批提交上限：超过后强制提交，防止 UI 长时间不投递时后台内存无界增长。</summary>
        private const int PendingBatchMaxBytes = 64 * 1024;

        /// <summary>当接收到数据时触发（已在 UI 线程上回调）。</summary>
        public event EventHandler<DataReceivedEventArgs> DataReceived;

        /// <summary>当串口打开/关闭状态变化时触发（已在 UI 线程上回调）。</summary>
        public event EventHandler<bool> ConnectionChanged;

        /// <summary>物理连接状态：底层串口是否真正打开（设备拔出 / IO 错误后可能为 false）。</summary>
        public bool IsOpen => _port?.IsOpen ?? false;

        /// <summary>逻辑连接状态：打开后直到手动关闭（设备拔出等待重插期间仍为 true，UI 以此为据）。</summary>
        public bool IsConnected => _config != null;

        /// <summary>当前端口名称（未连接时为空字符串；逻辑连接期间读取配置）。</summary>
        public string PortName => _config?.PortName ?? string.Empty;

        public SerialPortService()
        {
            // 端口实例不在构造时创建：每次 Open / TryReconnect 都通过 CreatePort 重建。
            // 设备拔出后旧实例的内部流会被自动关闭，复用同一实例打开不可靠，必须全新实例。

            // 捕获当前同步上下文，用于把后台线程事件切回 UI 线程
            _syncContext = SynchronizationContext.Current ?? new SynchronizationContext();
        }

        /// <summary>获取系统中所有可用串口名称。</summary>
        public static string[] GetAvailablePorts() => SerialPortType.GetPortNames();

        /// <summary>标准波特率列表（升序，供 UI 下拉框使用）。</summary>
        public static readonly int[] StandardBaudRates = new[]
        {
            110, 300, 600, 1200, 2400, 4800, 9600,
            14400, 19200, 38400, 56000, 57600, 115200,
            128000, 230400, 256000, 460800, 500000, 512000,
            600000, 750000, 921600, 1000000, 1500000, 2000000
        };

        /// <summary>销毁当前端口实例（摘事件、关闭、释放）并清空字段；任何异常都吞掉。</summary>
        private void DisposePort()
        {
            SerialPortType old;
            lock (_portLock)
            {
                old = _port;
                _port = null;   // 先清字段：在途 DataReceived 回调的实例守卫立即生效
            }
            if (old == null) return;
            old.DataReceived -= OnSerialDataReceived;
            try { if (old.IsOpen) old.Close(); } catch { }
            try { old.Dispose(); } catch { }
        }

        /// <summary>清理旧实例并创建新实例（默认超时 / DTR，挂接接收事件）。</summary>
        private SerialPortType CreatePort()
        {
            DisposePort();
            var port = new SerialPortType
            {
                // 默认参数，实际由 UI 通过 Open 传入
                ReadTimeout = 3000,
                WriteTimeout = 3000,
                // 启用基础流，便于 DataReceived 工作
                DtrEnable = true,
                RtsEnable = false
            };
            port.DataReceived += OnSerialDataReceived;
            lock (_portLock) _port = port;
            return port;
        }

        /// <summary>把配置参数套用到端口实例上。</summary>
        private static void ApplyConfig(SerialPortType port, SerialPortConfig config)
        {
            port.PortName = config.PortName;
            port.BaudRate = config.BaudRate;
            port.DataBits = (int)config.DataBits;
            port.StopBits = config.StopBits;
            port.Parity = config.Parity;
            port.Handshake = config.Handshake;
        }

        /// <summary>
        /// 按指定参数打开串口。打开失败时保持未连接状态并把异常抛给调用方（UI 弹窗提示）。
        /// </summary>
        public void Open(SerialPortConfig config)
        {
            if (_config != null) return;   // 已逻辑连接：忽略重复打开

            SerialPortType port = CreatePort();
            ApplyConfig(port, config);
            try
            {
                port.Open();
            }
            catch
            {
                // 打开失败：清理新建实例，保持未连接，异常抛给调用方
                DisposePort();
                throw;
            }
            _config = config;
            ConnectionChanged?.Invoke(this, true);
        }

        /// <summary>
        /// 设备重插后的自动重连：按打开时的配置重建实例并打开。
        /// 成功返回 true（不触发事件，UI 保持"已连接"外观）；失败清理实例并返回 false，由下轮重试。
        /// </summary>
        public bool TryReconnect()
        {
            if (_config == null) return false;

            SerialPortType port = CreatePort();
            ApplyConfig(port, _config);
            try
            {
                port.Open();
                return true;
            }
            catch
            {
                DisposePort();
                return false;
            }
        }

        /// <summary>
        /// 关闭串口。逻辑连接期间总是发出断开事件（设备已拔出时底层关闭可能失败，忽略）；
        /// 未连接时仅防御性清理，不发事件。
        /// </summary>
        public void Close()
        {
            if (_config == null)
            {
                DisposePort();   // 防御性清理可能残留的物理端口
                return;
            }
            DisposePort();
            _config = null;
            ConnectionChanged?.Invoke(this, false);
        }

        /// <summary>以文本方式发送数据。逻辑未连接时抛异常；等待重插（物理断开）时静默丢弃。</summary>
        public void SendText(string text, Encoding encoding)
        {
            if (_config == null) throw new InvalidOperationException("串口未连接。");

            byte[] buffer = encoding.GetBytes(text);
            try
            {
                // 物理未打开（设备拔出等待重插）时跳过写入，静默丢弃
                if (_port != null && _port.IsOpen)
                    _port.BaseStream.Write(buffer, 0, buffer.Length);
            }
            catch (Exception ex)
            {
                // 设备拔出（COM 号复用等）时写入抛 IO 类异常：静默丢弃，由 UI 定时器负责重连
                if (HandleDeviceError(ex)) return;
                throw;
            }
        }

        /// <summary>以十六进制字节方式发送数据。逻辑未连接时抛异常；等待重插（物理断开）时静默丢弃。</summary>
        public void SendBytes(byte[] data)
        {
            if (_config == null) throw new InvalidOperationException("串口未连接。");
            try
            {
                // 物理未打开（设备拔出等待重插）时跳过写入，静默丢弃
                if (_port != null && _port.IsOpen)
                    _port.BaseStream.Write(data, 0, data.Length);
            }
            catch (Exception ex)
            {
                // 设备拔出（COM 号复用等）时写入抛 IO 类异常：静默丢弃，由 UI 定时器负责重连
                if (HandleDeviceError(ex)) return;
                throw;
            }
        }

        /// <summary>真正丢弃输入缓冲区中尚未读取的数据（仅物理打开时有效）。</summary>
        public void DiscardInBuffer()
        {
            if (_port != null && _port.IsOpen) _port.DiscardInBuffer();
        }

        /// <summary>设置 DTR 引脚状态（仅物理打开时生效；未打开静默忽略，重连后由端口默认值接管）。</summary>
        public void SetDtr(bool enable)
        {
            if (_port != null && _port.IsOpen) _port.DtrEnable = enable;
        }

        /// <summary>设置 RTS 引脚状态（仅物理打开时生效；未打开静默忽略，重连后由端口默认值接管）。</summary>
        public void SetRts(bool enable)
        {
            if (_port != null && _port.IsOpen) _port.RtsEnable = enable;
        }

        private void OnSerialDataReceived(object sender, SerialDataReceivedEventArgs e)
        {
            try
            {
                // 只处理当前实例的事件：端口重建期间，旧实例的在途回调直接丢弃
                SerialPortType port;
                lock (_portLock) port = _port;
                if (port == null || !ReferenceEquals(sender, port) || !port.IsOpen) return;
                int bytes = port.BytesToRead;
                if (bytes <= 0) return;

                byte[] buffer = new byte[bytes];
                port.BaseStream.Read(buffer, 0, bytes);

                // 攒批：已有同实例的在途批则只追加数据（由该批的 Flush 统一提交），否则开新批并 Post
                bool postFlush = false;
                lock (_pendingLock)
                {
                    if (_pendingData == null || !ReferenceEquals(_pendingPort, port))
                    {
                        // 开始新批（不同实例的旧批由 Flush 时的实例守卫丢弃）
                        _pendingData = new MemoryStream();
                        _pendingPort = port;
                        postFlush = true;
                    }
                    _pendingData.Write(buffer, 0, buffer.Length);
                    // 超过攒批上限：强制提交（在途 Flush 会取走整批，本次新 Post 的 Flush 为空返回）
                    if (_pendingData.Length > PendingBatchMaxBytes) postFlush = true;
                }
                if (postFlush) Post(FlushPendingData);
            }
            catch (Exception ex)
            {
                // 设备拔出（COM 号被复用等）时读取抛 IO 类异常：静默丢弃，由 UI 定时器自动重连
                HandleDeviceError(ex);
                // 其余异常静默处理，避免后台线程崩溃
            }
        }

        /// <summary>UI 线程：提交攒批的接收数据；期间端口若已重建，丢弃这批旧数据。</summary>
        private void FlushPendingData()
        {
            byte[] data;
            SerialPortType port;
            lock (_pendingLock)
            {
                if (_pendingData == null) return;
                data = _pendingData.ToArray();
                port = _pendingPort;
                _pendingData = null;    // 移交后置空：后续新块开新批
                _pendingPort = null;
            }
            bool isCurrent;
            lock (_portLock) isCurrent = ReferenceEquals(_port, port);
            if (!isCurrent) return;
            DataReceived?.Invoke(this, new DataReceivedEventArgs(data));
        }

        /// <summary>
        /// 统一判定"设备已不可用"（拔出但端口名未消失等）：逻辑连接期间遇 IO 类异常、
        /// 端口关闭/重建竞态异常或读写超时时，视为设备已不可用，静默丢弃本次收发，
        /// 由 UI 定时器检测拔出并自动重连。返回是否已按此处理。
        /// 注意：不能用 _port.IsOpen 判定——致命 IO 错误后 SerialStream 已被自动关闭，
        /// 以 IsOpen 判定会漏报（这正是旧版拔出后界面状态不更新的根源）。
        /// </summary>
        private bool HandleDeviceError(Exception ex) =>
            _config != null && (ex is IOException
                || ex is UnauthorizedAccessException
                || ex is InvalidOperationException    // 端口正在被关闭/重建（流已释放）时的写竞态
                || ex is TimeoutException);           // 设备无响应：读/写超时

        /// <summary>把动作切回 UI 线程执行（后台线程事件回调统一走此入口）。</summary>
        private void Post(Action action) => _syncContext.Post(_ => action(), null);

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _config = null;   // 逻辑连接复位（不触发事件：窗口已关闭）
            DisposePort();
        }
    }

    /// <summary>串口配置参数。</summary>
    public sealed class SerialPortConfig
    {
        public string PortName { get; set; } = "COM1";
        public int BaudRate { get; set; } = 9600;
        public DataBits DataBits { get; set; } = DataBits.Eight;
        public StopBits StopBits { get; set; } = System.IO.Ports.StopBits.One;
        public Parity Parity { get; set; } = Parity.None;
        public Handshake Handshake { get; set; } = Handshake.None;
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
