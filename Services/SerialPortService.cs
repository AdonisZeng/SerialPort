using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Ports;
using System.Threading;
using System.Windows.Threading;

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

        // 发送队列：写操作可能被硬件流控阻塞到 WriteTimeout，必须在后台线程串行执行，
        // 否则高频发送 / 设备不读时 UI 线程会整段冻结
        private readonly object _sendLock = new object();
        private readonly Queue<SendItem> _sendQueue = new Queue<SendItem>();
        private int _sendQueueBytes;   // 队列中尚未写出的字节数（_sendLock 内维护）
        private bool _writeInProgress; // 已出队、正在写出（_sendLock 内维护；排空判断需算上它）
        private readonly AutoResetEvent _sendSignal = new AutoResetEvent(false);
        private readonly Thread _sendThread;
        private volatile bool _sendStopped;
        private long _sentBytes;

        /// <summary>攒批硬上限：超过后丢弃新到的数据块并上报，避免 UI 长时间停顿时后台内存无界增长。</summary>
        private const int PendingBatchMaxBytes = 1024 * 1024;

        /// <summary>
        /// 发送队列字节上限。接收方向有攒批硬上限，发送方向没有天然的背压来源：
        /// 改造前同步写在 UI 线程，写阻塞时界面卡住顺便"挡住"后续提交；改到后台线程后
        /// 定时发送（间隔可低至 1 ms）叠加设备不读 / 硬件流控会让队列无界增长，
        /// 故与接收同量级设上限，超限拒绝入队并上报。
        /// </summary>
        private const int SendQueueMaxBytes = 1024 * 1024;

        /// <summary>
        /// 队列元素：数据 + 写出成功后交给 UI 回显的文本。
        /// 回显必须绑定到"真正写出"这一刻：入队即回显会让设备拔出 / 写超时的丢弃
        /// 也显示成"已发送"，而写线程又无法在失败后纠正已经上屏的行。
        /// </summary>
        private sealed class SendItem
        {
            public byte[] Data;
            public string Echo;
        }

        /// <summary>当接收到数据时触发（已在 UI 线程上回调）。</summary>
        public event EventHandler<DataReceivedEventArgs> DataReceived;

        /// <summary>当串口打开/关闭状态变化时触发（在调用线程同步回调，非 Post 编组）。</summary>
        public event EventHandler<bool> ConnectionChanged;

        /// <summary>
        /// 数据真正写出后触发（已在 UI 线程回调），参数为提交时携带的回显文本（非空）。
        /// 本地回显据此产生，保证"看到 [TX]"与"数据已离开本机"是同一件事。
        /// </summary>
        public event EventHandler<string> SendCompleted;

        /// <summary>
        /// 设备异常（拔出 / IO 错误 / 读写超时）时触发（已在 UI 线程回调），携带中文提示文本。
        /// 仅用于状态栏告知：同一消息 2 秒内最多上报一次，避免高频错误淹没界面。
        /// </summary>
        public event EventHandler<string> DeviceError;

        // 设备错误上报节流（后台线程产生，UI 线程消费）
        private readonly object _errorLock = new object();
        private DateTime _lastErrorAt;         // 上次上报时刻（UTC）；按时间全局节流，不区分错误文本
        private static readonly TimeSpan ErrorReportInterval = TimeSpan.FromSeconds(2);

        /// <summary>物理连接状态：底层串口是否真正打开（设备拔出 / IO 错误后可能为 false）。</summary>
        public bool IsOpen => TryGetOpenPort() != null;   // 同样走锁内取快照，不直读 _port

        /// <summary>逻辑连接状态：打开后直到手动关闭（设备拔出等待重插期间仍为 true，UI 以此为据）。</summary>
        public bool IsConnected => _config != null;

        /// <summary>当前端口名称（未连接时为空字符串；逻辑连接期间读取配置）。</summary>
        public string PortName => _config?.PortName ?? string.Empty;

        /// <summary>累计真正写出的发送字节数（线程安全，供统计区读取）。</summary>
        public long SentBytes => Interlocked.Read(ref _sentBytes);

        /// <summary>复位发送字节统计。</summary>
        public void ResetSentBytes() => Interlocked.Exchange(ref _sentBytes, 0);

        public SerialPortService()
        {
            // 端口实例不在构造时创建：每次 Open / TryReconnect 都通过 CreatePort 重建。
            // 设备拔出后旧实例的内部流会被自动关闭，复用同一实例打开不可靠，必须全新实例。

            // 回退到创建线程的 Dispatcher 上下文：服务必须在 UI 线程构造，
            // 直接用裸 SynchronizationContext 会把事件丢到线程池（跨线程访问控件）
            _syncContext = SynchronizationContext.Current
                ?? new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher);

            // 写线程常驻：随进程退出（IsBackground），Dispose 时置停止标志并唤醒
            _sendThread = new Thread(SendLoop) { IsBackground = true, Name = "SerialPortSend" };
            _sendThread.Start();
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
        /// 关闭前给写线程一个短暂的排空窗口：用户"点发送后立刻关串口"时，队列里的数据
        /// 通常能在几毫秒内写出；排不空的剩余部分丢弃并上报条数，避免静默丢失。
        /// </summary>
        public void Close()
        {
            if (_config == null)
            {
                ClearSendQueue();
                DisposePort();   // 防御性清理可能残留的物理端口
                return;
            }

            WaitSendQueueDrained(TimeSpan.FromMilliseconds(300));
            int discarded = ClearSendQueue();

            // 先复位逻辑连接再释放端口：写线程此刻若因端口释放而失败，
            // HandleDeviceError 会因 _config == null 静默忽略，
            // 状态栏不会被"端口正在重建"之类的提示覆盖掉"已断开"
            _config = null;
            DisposePort();

            if (discarded > 0)
                ReportDeviceError($"关闭串口：{discarded} 条待发数据未写出，已丢弃");
            ConnectionChanged?.Invoke(this, false);
        }

        /// <summary>等待发送队列排空（含正在写出的那一项），超时返回 false。</summary>
        private bool WaitSendQueueDrained(TimeSpan timeout)
        {
            DateTime deadline = DateTime.UtcNow + timeout;
            while (true)
            {
                lock (_sendLock)
                {
                    // 仅队列为空不够：最后一项可能已出队但仍阻塞在 Write 里
                    if (_sendQueue.Count == 0 && !_writeInProgress) return true;
                }
                if (DateTime.UtcNow >= deadline) return false;
                Thread.Sleep(5);
            }
        }

        /// <summary>
        /// 清空发送队列中尚未写出的数据并返回丢弃条数：关闭 / 释放时丢弃，否则下次打开
        /// 会把上一次会话遗留的指令写到新端口（旧实现同步写，不存在跨会话残留）。
        /// </summary>
        private int ClearSendQueue()
        {
            lock (_sendLock)
            {
                int count = _sendQueue.Count;
                _sendQueue.Clear();
                _sendQueueBytes = 0;
                return count;
            }
        }

        /// <summary>
        /// 在 _portLock 内取一次端口快照：仅当端口存在且物理打开时返回该实例，否则 null。
        /// 供所有读写引脚 / 收发的方法统一使用，避免与 DisposePort 竞态下重复读 _port 拿到 null。
        /// 返回的实例在锁外使用时可能已被释放，调用方须自行捕获 IO 类异常（见 HandleDeviceError）。
        /// </summary>
        private SerialPortType TryGetOpenPort()
        {
            lock (_portLock)
            {
                SerialPortType port = _port;
                return port != null && port.IsOpen ? port : null;
            }
        }

        /// <summary>
        /// 提交数据到后台发送队列。返回是否被接受：
        /// 逻辑未连接时抛异常；等待重插（物理断开）或队列已满时丢弃并返回 false（经 DeviceError 上报）；
        /// 接受后由后台写线程按序写出，统计计入 <see cref="SentBytes"/>。
        /// 文本内容由调用方（MainWindow.SendText）先按当前编码转为字节后经此发送。
        /// echoText：非空时在数据真正写出后经 <see cref="SendCompleted"/> 交回 UI 追加本地回显。
        /// </summary>
        public bool SendBytes(byte[] data, string echoText = null)
        {
            if (_config == null) throw new InvalidOperationException("串口未连接。");
            if (data == null || data.Length == 0) return false;
            if (TryGetOpenPort() == null)
            {
                // 物理未打开但逻辑仍连接（设备拔出等待重插）：丢弃并提示，
                // 否则定时发送会长时间空转而状态栏毫无反馈（服务层已按时间节流）
                ReportDeviceError("设备未打开，本次数据已丢弃，等待重新连接");
                return false;
            }

            bool rejected = false;
            lock (_sendLock)
            {
                // 队列已满（设备写入受阻）：拒绝新数据而不是丢弃最旧的——
                // 已入队的数据是用户先发的，丢掉会让设备收到乱序/不完整的指令流
                if (_sendQueueBytes + data.Length > SendQueueMaxBytes)
                    rejected = true;
                else
                {
                    _sendQueue.Enqueue(new SendItem { Data = data, Echo = echoText });
                    _sendQueueBytes += data.Length;
                }
            }
            if (rejected)
            {
                // 由写线程阻塞导致，提示里要说明是"设备写入受阻"而不是格式或连接问题
                ReportDeviceError("发送缓冲已满（设备写入受阻），本次数据已丢弃");
                return false;
            }
            _sendSignal.Set();
            return true;
        }

        /// <summary>
        /// 后台写线程：按入队顺序写出数据。写入失败不得抛出（后台线程崩溃会带走进程），
        /// 统一走 HandleDeviceError 上报并经 DeviceError 提示状态栏。
        /// 只有真正写出才累计字节并触发回显：写失败 / 关闭竞态下的丢弃不会留下"已发送"痕迹。
        /// </summary>
        private void SendLoop()
        {
            while (!_sendStopped)
            {
                _sendSignal.WaitOne();
                while (!_sendStopped)
                {
                    SendItem item;
                    lock (_sendLock)
                    {
                        if (_sendQueue.Count == 0) break;
                        item = _sendQueue.Dequeue();
                        _sendQueueBytes -= item.Data.Length;
                        _writeInProgress = true;
                    }

                    try
                    {
                        SerialPortType port = TryGetOpenPort();
                        if (port == null)
                        {
                            if (_config != null)
                            {
                                // 物理已断开（拔出等待重插）：丢弃并提示，重连后用户再次发送即可。
                                // 手动关闭时 _config 已复位，剩余数据由 Close 统一上报条数，这里不重复
                                ReportDeviceError("设备未打开，本次数据已丢弃，等待重新连接");
                            }
                            continue;
                        }
                        port.BaseStream.Write(item.Data, 0, item.Data.Length);
                        Interlocked.Add(ref _sentBytes, item.Data.Length);
                        if (!string.IsNullOrEmpty(item.Echo))
                        {
                            string echo = item.Echo;
                            Post(() => SendCompleted?.Invoke(this, echo));
                        }
                    }
                    catch (Exception ex)
                    {
                        // 关闭竞态（_config 已复位）下的写失败属预期：静默，避免覆盖"已断开"
                        if (_config == null) continue;
                        if (!HandleDeviceError(ex))
                            ReportDeviceError("发送失败：" + ex.Message);
                    }
                    finally
                    {
                        lock (_sendLock) _writeInProgress = false;
                    }
                }
            }
        }

        /// <summary>真正丢弃输入缓冲区中尚未读取的数据（仅物理打开时有效）。</summary>
        public void DiscardInBuffer()
        {
            SerialPortType port = TryGetOpenPort();
            if (port == null) return;
            try { port.DiscardInBuffer(); }
            catch (Exception ex) { HandleDeviceError(ex); }   // 端口刚被释放：忽略
        }

        /// <summary>设置 DTR 引脚状态（仅物理打开时生效；未打开静默忽略，重连后由端口默认值接管）。</summary>
        public void SetDtr(bool enable)
        {
            SerialPortType port = TryGetOpenPort();
            if (port == null) return;
            try { port.DtrEnable = enable; }
            catch (Exception ex) { HandleDeviceError(ex); }
        }

        /// <summary>设置 RTS 引脚状态（仅物理打开时生效；未打开静默忽略，重连后由端口默认值接管）。</summary>
        public void SetRts(bool enable)
        {
            SerialPortType port = TryGetOpenPort();
            if (port == null) return;
            try { port.RtsEnable = enable; }
            catch (Exception ex) { HandleDeviceError(ex); }
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
                int read = port.BaseStream.Read(buffer, 0, bytes);
                if (read <= 0) return;
                if (read < bytes) Array.Resize(ref buffer, read);   // 短读防御：截掉尾部未填充的 0x00

                // 攒批：已有同实例的在途批则只追加数据（由该批的 Flush 统一提交），否则开新批并 Post
                bool postFlush = false;
                bool overflowDropped = false;
                lock (_pendingLock)
                {
                    if (_pendingData == null || !ReferenceEquals(_pendingPort, port))
                    {
                        // 开始新批（不同实例的旧批由 Flush 时的实例守卫丢弃）
                        _pendingData = new MemoryStream();
                        _pendingPort = port;
                        postFlush = true;   // 同一批只需一次在途 Post，后续追加不必重复投递
                    }
                    if (_pendingData.Length + buffer.Length <= PendingBatchMaxBytes)
                        _pendingData.Write(buffer, 0, buffer.Length);
                    else
                        overflowDropped = true;   // 硬上限：丢弃本块，保证后台内存有界
                }
                if (postFlush) Post(FlushPendingData);
                if (overflowDropped) ReportDeviceError("接收缓冲溢出，部分数据已丢弃");
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
        /// 端口关闭/重建竞态异常或读写超时时，视为设备已不可用，丢弃本次收发，
        /// 由 UI 定时器检测拔出并自动重连；同时经 DeviceError 事件上报一次提示（状态栏可见）。
        /// 返回是否已按此处理。
        /// 注意：不能用 _port.IsOpen 判定——致命 IO 错误后 SerialStream 已被自动关闭，
        /// 以 IsOpen 判定会漏报（这正是旧版拔出后界面状态不更新的根源）。
        /// </summary>
        private bool HandleDeviceError(Exception ex)
        {
            if (_config == null) return false;
            if (!(ex is IOException
                || ex is UnauthorizedAccessException
                || ex is InvalidOperationException    // 端口正在被关闭/重建（流已释放）时的写竞态
                || ex is TimeoutException))           // 设备无响应：读/写超时
                return false;
            ReportDeviceError(DescribeDeviceError(ex));
            return true;
        }

        /// <summary>把设备异常翻译成中文提示（状态栏展示用）。</summary>
        private static string DescribeDeviceError(Exception ex)
        {
            if (ex is TimeoutException) return "设备无响应（读/写超时），数据已丢弃，等待重新连接";
            if (ex is UnauthorizedAccessException) return "串口被其他程序占用，等待重新连接";
            if (ex is InvalidOperationException) return "端口正在重建，本次操作已丢弃";
            return "设备已断开或读写失败，等待重新连接";   // IOException
        }

        /// <summary>
        /// 上报设备错误（节流后 Post 回 UI 线程）：2 秒内最多上报一次（不论文本）。
        /// 串口错误在高频收发时可能每秒成百上千次，不去重会把 UI 消息队列打满。
        /// 只比较"上一条文本"是不够的：设备拔出时 IOException（写失败）与
        /// InvalidOperationException（端口重建竞态）交替出现，两次判定的文本都不同，
        /// 节流会被逐条绕过，因此这里按时间做全局节流。
        /// </summary>
        private void ReportDeviceError(string text)
        {
            DateTime now = DateTime.UtcNow;
            lock (_errorLock)
            {
                if (now - _lastErrorAt < ErrorReportInterval) return;
                _lastErrorAt = now;
            }
            Post(() => DeviceError?.Invoke(this, text));
        }

        /// <summary>
        /// 把动作切回 UI 线程执行（后台线程事件回调统一走此入口）。
        /// 关窗后调度器可能已关闭（BeginInvoke 抛 InvalidOperationException），
        /// 此时事件已无人消费，静默丢弃；只收窄捕获该异常，其余异常照常暴露。
        /// </summary>
        private void Post(Action action)
        {
            try { _syncContext.Post(_ => action(), null); }
            catch (InvalidOperationException) { }   // 调度器已关闭：事件已无人消费
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            // 先置停止标志再释放端口：否则写线程可能在端口释放后仍去写。
            // 不 Join：写可能正被硬件流控阻塞在 WriteTimeout 上，Join 会拖住关窗退出；
            // 写线程为 IsBackground，进程退出时由运行时回收，_sendSignal 同样无需释放。
            _sendStopped = true;
            _sendSignal.Set();

            _config = null;   // 逻辑连接复位（不触发事件：窗口已关闭）
            DisposePort();
        }
    }

    /// <summary>串口配置参数。</summary>
    public sealed class SerialPortConfig
    {
        public string PortName { get; set; } = "COM1";
        public int BaudRate { get; set; } = 115200;
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
