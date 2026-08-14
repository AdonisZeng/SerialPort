using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Ports;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using SerialPort.Services;
using SerialPort.UI;

namespace SerialPort
{
    /// <summary>
    /// 主窗口：上下分栏布局。
    /// 上部 = 接收显示区；下部 = 串口配置 + 发送区。
    /// </summary>
    public partial class MainWindow : Window
    {
        private readonly SerialPortService _service = new SerialPortService();
        private readonly UpdateService _updateService = new UpdateService();
        private readonly DispatcherTimer _timerPortCheck = new DispatcherTimer();
        private readonly DispatcherTimer _timerStats = new DispatcherTimer();   // 1 秒刷新统计区
        private bool _manualCheck;   // 手动检查标志：手动触发时结果/失败需要弹窗提示
        private bool _awaitingDevice;    // 已检测到设备拔出、正在等待重新插入（期间冻结端口列表）
        private int _reconnectFailures;  // 连续重连失败次数（达到阈值后状态栏提示一次）
        private FrameParserWindow _frameWindow;   // 帧解析窗口（非空 = 打开中，接收数据喂入）
        private long _receivedBytesTotal;
        private long _sentBytesTotal;   // 累计发送字节数
        private long _lastStatsRx;      // 上次统计快照的接收字节（计算速率）
        private long _lastStatsTx;      // 上次统计快照的发送字节（计算速率）
        private DateTime _connectStartedAt;   // 当前连接开始时间（断开后统计区显示 --:--:--）
        private volatile bool _timerSending;   // 定时发送进行中（后台线程循环检查，volatile 保证可见性）
        private int _timerGen;                 // 定时发送代次：每次启动递增，线程捕获自己的代次，代次不匹配立即退出
        private StreamWriter _saveFileWriter;   // 文件保存流（勾选“文件保存”时创建）
        private string _saveFilePath;           // 当前保存文件的完整路径

        // 后台文件写入队列：写盘移出 UI 线程，避免高频数据时阻塞界面（锁内访问）
        private readonly object _saveLock = new object();
        private readonly Queue<string> _saveQueue = new Queue<string>();
        private bool _saveDraining;

        // 接收/发送文本编码与状态保持解码器（跨接收块解码；仅 UI 线程使用）
        private Encoding _textEncoding = Encoding.UTF8;
        private Decoder _textDecoder = Encoding.UTF8.GetDecoder();

        // 暂停显示缓冲：暂停期间累积显示文本（上限保护），恢复时一次性补齐
        private readonly StringBuilder _pausedBuffer = new StringBuilder();

        // 搜索状态（接收区查找）
        private string _searchTerm;
        private bool _searchMatchCase;
        private bool _searchWholeWord;
        private int _searchIndex = -1;          // 最近一次匹配的起始下标；-1 = 无

        // 筛选状态（_filterState 非空表示筛选已启用）
        private FilterState _filterState;

        // 时间戳显示状态（状态机始终运行，与复选框勾选状态无关）
        private bool _atLineStart = true;   // 当前处于行首（下一个非换行字符应补时间戳）

        // 接收区显示文本字符数（与 txtReceive.Text 同步维护，避免每块数据 O(n) 读 Length）
        private int _receiveTextLength;

        public MainWindow()
        {
            InitializeComponent();

            // 文件保存：优先恢复上次路径，否则使用“我的文档”
            string lastSavePath = Properties.Settings.Default.LastSavePath;
            txtSavePath.Text = !string.IsNullOrWhiteSpace(lastSavePath)
                ? lastSavePath
                : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

            // 串口列表（初始填充不提示；运行期由定时器自动检测变化）
            RefreshPortList(SerialPortService.GetAvailablePorts(), false);

            // 波特率：固定速率 + Custom（可编辑输入任意值）
            cboBaudRate.Items.Clear();
            cboBaudRate.Items.Add("Custom");
            foreach (int rate in SerialPortService.StandardBaudRates)
                cboBaudRate.Items.Add(rate);
            cboBaudRate.SelectedItem = 9600;

            // 加载上次关闭前保存的波特率（Custom 数值也能恢复）
            int lastBaudRate = Properties.Settings.Default.LastBaudRate;
            if (lastBaudRate > 0)
                cboBaudRate.Text = lastBaudRate.ToString();

            // 定时发送间隔：恢复上次设置
            int timerInterval = Properties.Settings.Default.TimerSendInterval;
            if (timerInterval > 0)
                txtTimerInterval.Text = timerInterval.ToString();

            // 编码：固定三项（UTF-8 / GBK / ASCII），恢复上次选择
            cboEncoding.Items.Add("UTF-8");
            cboEncoding.Items.Add("GBK");
            cboEncoding.Items.Add("ASCII");
            string lastEncoding = Properties.Settings.Default.Encoding;
            if (string.IsNullOrEmpty(lastEncoding)) lastEncoding = "UTF-8";
            cboEncoding.SelectedItem = lastEncoding;

            // 数据位
            cboDataBits.Items.Clear();
            cboDataBits.Items.Add(5);
            cboDataBits.Items.Add(6);
            cboDataBits.Items.Add(7);
            cboDataBits.Items.Add(8);
            cboDataBits.SelectedItem = 8;

            // 停止位
            cboStopBits.Items.Clear();
            cboStopBits.Items.Add("1");
            cboStopBits.Items.Add("1.5");
            cboStopBits.Items.Add("2");
            cboStopBits.SelectedIndex = 0;

            // 校验位
            cboParity.Items.Clear();
            cboParity.Items.Add("None");
            cboParity.Items.Add("Even");
            cboParity.Items.Add("Odd");
            cboParity.Items.Add("Mark");
            cboParity.Items.Add("Space");
            cboParity.SelectedIndex = 0;

            // 流控
            cboHandshake.Items.Clear();
            cboHandshake.Items.Add("None");
            cboHandshake.Items.Add("RTS/CTS");
            cboHandshake.Items.Add("XOn/XOff");
            cboHandshake.SelectedIndex = 0;

            // 端口自动检测：0.5 秒轮询一次（缩短以便更快捕获设备重启后的启动打印）
            _timerPortCheck.Interval = TimeSpan.FromMilliseconds(500);
            _timerPortCheck.Tick += TimerPortCheck_Tick;
            _timerPortCheck.Start();

            // 数据统计：1 秒刷新一次（累计字节 / 速率 / 连接时长）
            _timerStats.Interval = TimeSpan.FromSeconds(1);
            _timerStats.Tick += TimerStats_Tick;
            _timerStats.Start();

            // 订阅服务事件（服务在 UI 线程构造，事件回调已在 UI 线程，可直接操作控件）
            _service.DataReceived += OnDataReceived;
            _service.ConnectionChanged += OnConnectionChanged;

            // 订阅更新服务事件；启动时后台检查一次新版本（不阻塞界面）
            _updateService.CheckCompleted += OnUpdateCheckCompleted;
            _updateService.UpdateError += OnUpdateError;
            _updateService.UpdateApplied += OnUpdateApplied;
            _updateService.CheckForUpdatesAsync();

            Closed += (s, e) =>
            {
                _timerPortCheck.Stop();       // 停止端口轮询
                _timerStats.Stop();           // 停止统计刷新
                _timerSending = false;        // 停止定时发送（后台线程随进程退出）
                DisposeSaveWriter();          // 关闭保存文件（排空队列后释放）
                _service.Dispose();           // 关闭串口并释放资源（Dispose 幂等）

                // 保存当前波特率与文件保存路径，下次启动自动恢复
                if (int.TryParse(cboBaudRate.Text, out int baudRate) && baudRate > 0)
                    Properties.Settings.Default.LastBaudRate = baudRate;

                if (int.TryParse(txtTimerInterval.Text.Trim(), out int savedTimerInterval) && savedTimerInterval > 0)
                    Properties.Settings.Default.TimerSendInterval = savedTimerInterval;

                Properties.Settings.Default.Encoding = cboEncoding.SelectedItem?.ToString() ?? "UTF-8";

                string savePath = txtSavePath.Text?.Trim();
                if (!string.IsNullOrWhiteSpace(savePath))
                    Properties.Settings.Default.LastSavePath = savePath;

                Properties.Settings.Default.Save();
            };

            UpdateStatus("就绪");
        }

        // ============================================================
        // 端口自动检测（0.5 秒轮询一次；拔出保持连接，重插自动重连）
        // ============================================================
        private void TimerPortCheck_Tick(object sender, EventArgs e)
        {
            string[] ports = SerialPortService.GetAvailablePorts();

            if (_service.IsConnected)
            {
                if (Array.IndexOf(ports, _service.PortName) >= 0)
                {
                    // 端口已出现：物理连接缺失（拔出 / IO 错误后）则尝试自动重连
                    if (!_service.IsOpen)
                    {
                        if (_service.TryReconnect())
                        {
                            _awaitingDevice = false;
                            _reconnectFailures = 0;
                            ApplyPinStates();   // 重连重建了端口实例：按当前勾选重写引脚
                            UpdateStatus($"检测到 {_service.PortName} 已重新连接");
                        }
                        else if (++_reconnectFailures == 10)
                        {
                            // 连续重连失败（端口被占用等）：提示一次，继续每轮静默重试
                            UpdateStatus("重连失败，请检查端口是否被其他程序占用");
                        }
                    }
                    else
                    {
                        _awaitingDevice = false;   // 物理连接已在：防御性复位
                    }
                }
                else if (!_awaitingDevice)
                {
                    // 首次检测到端口消失：置位冻结标志并提示一次（不刷屏）
                    _awaitingDevice = true;
                    UpdateStatus($"检测到 {_service.PortName} 已拔出，等待重新插入…");
                }
            }
            else
            {
                _awaitingDevice = false;   // 未连接：防御性复位
            }

            // 等待重插期间冻结端口列表（跳过刷新，避免重建列表丢失选中项）
            if (_awaitingDevice) return;
            // 下拉展开时不改 Items（避免重绘异常）；DropDownOpened 事件（每次打开下拉）会补刷
            if (cboPortName.IsDropDownOpen) return;
            RefreshPortList(ports);
        }

        /// <summary>打开下拉时刷新一次，保证用户看到的列表是最新的（PortListEquals 兜底去重）。</summary>
        private void cboPortName_DropDown(object sender, EventArgs e)
        {
            // 等待重插期间冻结列表：不刷新，避免重建列表丢失选中项
            if (_awaitingDevice) return;
            RefreshPortList(SerialPortService.GetAvailablePorts());
        }

        private void RefreshPortList(string[] ports, bool notify = true)
        {
            if (PortListEquals(ports)) return;   // 集合无变化：不动列表，也不触发描述查询

            // 先以纯端口号占位重建（列表立即反映真实端口，描述查询完成后回填）
            RebuildPortItems(ports, null);

            if (notify) UpdateStatus("检测到端口变化，已更新列表");

            ScheduleDescriptionBackfill(ports);
        }

        /// <summary>
        /// 异步查询设备描述；完成后切回 UI 线程回填。
        /// 集合再次变化时丢弃本次结果（不直接触发新查询），由下轮 tick 自然补查——收敛无循环。
        /// </summary>
        private void ScheduleDescriptionBackfill(string[] ports)
        {
            Task.Run(() => PortDeviceInfo.GetDescriptions()).ContinueWith(t =>
            {
                if (!PortListEquals(ports)) return;   // 集合已变化：丢弃本次结果
                RebuildPortItems(ports, t.Result);
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        /// <summary>
        /// 重建端口下拉框 Items（SerialPortItem 列表）。
        /// 选中恢复规则：优先保留当前选中（用户可能已手动改选）；
        /// 失效且未连接时选第一个 / 清空；已连接（端口仍在列表中）不强制改选。
        /// </summary>
        private void RebuildPortItems(string[] ports, Dictionary<string, string> descriptions)
        {
            string keep = (cboPortName.SelectedItem as SerialPortItem)?.PortName;

            cboPortName.Items.Clear();
            foreach (string port in ports)
            {
                string desc = null;
                if (descriptions != null) descriptions.TryGetValue(port, out desc);
                cboPortName.Items.Add(new SerialPortItem(port, desc));
            }

            int keepIndex = keep != null ? Array.IndexOf(ports, keep) : -1;
            if (keepIndex >= 0)
                cboPortName.SelectedIndex = keepIndex;   // 保留当前选中项
            else if (!_service.IsConnected)
                cboPortName.SelectedIndex = ports.Length > 0 ? 0 : -1;   // 选中项消失且未连接 → 选第一个 / 清空
            // 串口已打开：不强制改选、不动连接
        }

        /// <summary>集合比较（GetPortNames 顺序不稳，用存在性判断而非按下标；只比较端口名，不分配临时数组）。</summary>
        private bool PortListEquals(string[] ports)
        {
            if (cboPortName.Items.Count != ports.Length) return false;
            foreach (string port in ports)
            {
                bool found = false;
                for (int i = 0; i < cboPortName.Items.Count; i++)
                {
                    if (((SerialPortItem)cboPortName.Items[i]).PortName == port)
                    {
                        found = true;
                        break;
                    }
                }
                if (!found) return false;
            }
            return true;
        }

        // ============================================================
        // 打开 / 关闭串口（单个切换按钮）
        // ============================================================
        private void BtnToggleConnect_Click(object sender, RoutedEventArgs e)
        {
            if (_service.IsConnected)
            {
                _service.Close();
                return;
            }

            var item = cboPortName.SelectedItem as SerialPortItem;
            if (item == null)
            {
                MessageBox.Show("请先选择串口。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            try
            {
                int baudRate = ParseBaudRate(cboBaudRate.Text);
                var config = new SerialPortConfig
                {
                    PortName = item.PortName,
                    BaudRate = baudRate,
                    DataBits = (DataBits)Convert.ToInt32(cboDataBits.SelectedItem),
                    StopBits = ParseStopBits(cboStopBits.SelectedItem.ToString()),
                    Parity = ParseParity(cboParity.SelectedItem.ToString()),
                    Handshake = ParseHandshake(cboHandshake.SelectedItem.ToString())
                };
                _service.Open(config);
                UpdateStatus($"已连接 {config.PortName} @ {config.BaudRate}");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"打开串口失败：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void OnConnectionChanged(object sender, bool connected)
        {
            // 连接状态由按钮文字（打开/关闭串口）与颜色（绿/红）体现；
            // 已连接时的端口详情由 BtnToggleConnect_Click 写入状态栏，这里只处理断开侧，避免覆盖
            btnToggleConnect.Content = connected ? "关闭串口" : "打开串口";
            btnToggleConnect.Background = connected
                ? (Brush)FindResource("PortCloseBrush")
                : (Brush)FindResource("PortOpenBrush");
            UpdateFileEditState(connected);   // 串口打开时禁止修改文件名
            chkDtr.IsEnabled = connected;     // 引脚开关连接后可用
            chkRts.IsEnabled = connected;
            if (connected)
            {
                _connectStartedAt = DateTime.Now;   // 连接时长计时起点
                ApplyPinStates();                    // 按当前勾选写入引脚
            }
            else
            {
                _awaitingDevice = false;   // 手动关闭等：解除冻结，恢复列表刷新
                UpdateStatus("已断开");
            }
        }

        // ============================================================
        // 主题切换（右上角按钮）
        // ============================================================
        private void BtnThemeToggle_Click(object sender, RoutedEventArgs e)
        {
            ThemeManager.Toggle();
            btnThemeToggle.Content = ThemeManager.Current == ThemeMode.Dark ? "浅色模式" : "深色模式";
            // 主题持久化：保存选择，下次启动恢复
            Properties.Settings.Default.Theme = (int)ThemeManager.Current;
            Properties.Settings.Default.Save();
        }

        /// <summary>编码下拉切换：重建对应状态保持 Decoder（从干净状态开始），发送编码联动。</summary>
        private void CboEncoding_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            string name = cboEncoding.SelectedItem as string;
            if (string.IsNullOrEmpty(name)) return;
            _textEncoding = name == "GBK" ? Encoding.GetEncoding("GBK")
                : name == "ASCII" ? Encoding.ASCII
                : Encoding.UTF8;
            _textDecoder = _textEncoding.GetDecoder();   // 新编码从干净状态开始
        }

        // ============================================================
        // 数据接收
        // ============================================================
        private void OnDataReceived(object sender, DataReceivedEventArgs e)
        {
            if (e.Data == null || e.Data.Length == 0) return;

            // 帧解析窗口打开时：把原始字节副本喂给解析器（窗口关闭即停止，无常驻开销）
            _frameWindow?.PushData(e.Data);

            _receivedBytesTotal += e.Data.Length;

            string increment = GetTimestampedText(GetIncrementText(e.Data));
            AppendDisplayText(increment);
        }

        /// <summary>1 秒统计刷新：累计接收/发送字节、双向实时速率、连接时长（UI 线程定时器）。</summary>
        private void TimerStats_Tick(object sender, EventArgs e)
        {
            long rx = _receivedBytesTotal;
            long tx = _sentBytesTotal;
            double rxKb = (rx - _lastStatsRx) / 1024.0;
            double txKb = (tx - _lastStatsTx) / 1024.0;
            _lastStatsRx = rx;
            _lastStatsTx = tx;
            string duration = _service.IsConnected
                ? DateTime.Now.Subtract(_connectStartedAt).ToString(@"hh\:mm\:ss")
                : "--:--:--";
            txtStats.Text = $"累计收 {rx} B / 发 {tx} B　|　↓ {rxKb:F1} KB/s　↑ {txKb:F1} KB/s　|　连接 {duration}";
        }

        /// <summary>
        /// 统一追加显示文本：筛选缓冲 / 容量截断 / 文件保存 / 自动滚动（接收数据与本地回显共用）。
        /// 暂停显示时仅累积与写文件，不刷新接收区，恢复时一次性补齐。
        /// </summary>
        private void AppendDisplayText(string increment)
        {
            if (chkPauseReceive.IsChecked == true)
            {
                // 暂停：数据仍记入筛选缓冲与保存文件，仅不刷新接收区
                if (_filterState != null) _filterState.Buffer.Append(increment);
                _pausedBuffer.Append(increment);
                TrimPausedBuffer();
                TrimReceiveIfNeeded();   // 暂停期间筛选 Buffer 同样截头，防止长时间暂停内存无界
                EnqueueSaveText(increment);
                return;
            }

            if (_filterState != null)
            {
                // 筛选启用：完整文本记入缓冲，接收区只追加符合筛选条件的行
                _filterState.Buffer.Append(increment);
                AppendFiltered(increment);
            }
            else
            {
                // 增量追加（WPF 下全量重赋 Text 会重建整个文本，大数据量时卡顿）
                txtReceive.AppendText(increment);
                _receiveTextLength += increment.Length;
            }

            // 接收区与筛选缓冲容量上限：超限截头，防止长时运行内存无界增长
            TrimReceiveIfNeeded();

            // 勾选“文件保存”时把同样的内容写入文件（后台队列写盘，不阻塞 UI 线程）
            EnqueueSaveText(increment);

            if (chkAutoScroll.IsChecked == true)
                txtReceive.ScrollToEnd();
        }

        /// <summary>暂停缓冲上限截头（与接收区同量级，防止长时间暂停内存无界增长）。</summary>
        private void TrimPausedBuffer()
        {
            if (_pausedBuffer.Length > MaxReceiveChars + TrimKeepMargin)
                _pausedBuffer.Remove(0, _pausedBuffer.Length - MaxReceiveChars);
        }

        /// <summary>暂停显示开关：勾选暂停刷新，取消时一次性补齐暂停期间的数据。</summary>
        private void ChkPauseReceive_Changed(object sender, RoutedEventArgs e)
        {
            if (chkPauseReceive.IsChecked == true)
            {
                UpdateStatus("接收显示已暂停，数据继续接收中");
                return;
            }
            ResumeDisplay();
            UpdateStatus("已恢复接收显示");
        }

        /// <summary>恢复显示：把暂停期间累积的文本一次性追加（筛选模式按行过滤，Buffer 已记录不重复）。</summary>
        private void ResumeDisplay()
        {
            if (_pausedBuffer.Length == 0) return;
            string pending = _pausedBuffer.ToString();
            _pausedBuffer.Clear();

            if (_filterState != null)
                AppendFiltered(pending);   // 逐行过滤追加显示（Buffer 在暂停期间已同步记录）
            else
            {
                txtReceive.AppendText(pending);
                _receiveTextLength += pending.Length;
                TrimReceiveIfNeeded();
            }
            if (chkAutoScroll.IsChecked == true)
                txtReceive.ScrollToEnd();
        }

        /// <summary>字节数组转 "AA BB CC" 形式（本地回显 hex 模式用）。</summary>
        private static string FormatHex(byte[] bytes)
        {
            var sb = new StringBuilder(bytes.Length * 3);
            foreach (byte b in bytes)
                sb.Append(b.ToString("X2")).Append(' ');
            return sb.ToString().TrimEnd();
        }

        /// <summary>本地回显：勾选后把发送内容以 [TX] 前缀追加到接收区（与接收数据同管线，含时间戳）。</summary>
        private void AppendLocalEcho(string content)
        {
            if (chkLocalEcho.IsChecked != true) return;
            // 行尾去掉追加的换行再补 \r\n：回显内容自成一行；经 GetTimestampedText 保持行首状态机一致
            AppendDisplayText(GetTimestampedText("[TX] " + content.TrimEnd('\r', '\n') + "\r\n"));
        }

        /// <summary>
        /// 接收显示与筛选缓冲的容量上限（字符数）：超出后截掉头部等量内容。
        /// WPF TextBox 整段存储文本且 AppendText 随文本增长越来越慢，必须设上限防止内存无界增长。
        /// </summary>
        private const int MaxReceiveChars = 1_000_000;

        /// <summary>截断余量：计数超过“上限 + 余量”才截头，避免高频接收时每块都触发全量拷贝。</summary>
        private const int TrimKeepMargin = 200_000;

        /// <summary>接收区（及筛选 Buffer）超过容量上限时截头，保持内存有界。</summary>
        private void TrimReceiveIfNeeded()
        {
            // 筛选模式：Buffer 记录全量文本，增长快于显示（不匹配行不进显示），须独立检查截头
            if (_filterState != null)
            {
                int bufferLength = _filterState.Buffer.Length;
                if (bufferLength > MaxReceiveChars + TrimKeepMargin)
                    _filterState.Buffer.Remove(0, bufferLength - MaxReceiveChars);
            }

            if (_receiveTextLength <= MaxReceiveChars + TrimKeepMargin) return;

            // 只在截断时读一次实际长度（频率低），防御计数漂移
            int actualLength = txtReceive.Text.Length;
            if (actualLength <= MaxReceiveChars)
            {
                _receiveTextLength = actualLength;   // 计数漂移：校准
                return;
            }

            int remove = actualLength - MaxReceiveChars;
            txtReceive.Text = txtReceive.Text.Substring(remove);   // 截头：保留最新数据
            _receiveTextLength = actualLength - remove;
            _searchIndex = _searchIndex < 0 ? -1 : Math.Max(-1, _searchIndex - remove);   // 搜索下标随截断前移
        }

        /// <summary>计算本次接收数据对应的显示文本（hex 模式每字节 "XX " 加换行，文本模式为 UTF-8 解码结果）。</summary>
        private string GetIncrementText(byte[] data)
        {
            if (chkReceiveHex.IsChecked == true)
            {
                _textDecoder.Reset();   // 切到 hex 模式：丢弃文本模式残留的未完成多字节序列
                StringBuilder sb = new StringBuilder(data.Length * 3 + 2);
                foreach (byte b in data)
                    sb.Append(b.ToString("X2")).Append(' ');
                sb.AppendLine();
                return sb.ToString();
            }
            // 状态保持的 Decoder 跨块解码：多字节序列被拆到相邻块时不会产生 U+FFFD 乱码
            int charCount = _textDecoder.GetCharCount(data, 0, data.Length);
            char[] chars = new char[charCount];
            _textDecoder.GetChars(data, 0, data.Length, chars, 0);
            return new string(chars);
        }

        /// <summary>
        /// 给单次接收的增量文本补时间戳：每行行首加 [HH:mm:ss]。
        /// 行以 \n、\r、\r\n（含跨块拆分的 \r+ 开头\n）终结；空行不加戳；
        /// 仅当勾选"时间戳显示"时输出前缀，行首位置跟踪始终进行。
        /// </summary>
        private string GetTimestampedText(string increment)
        {
            // 未勾选时无需构造输出：行首状态只取决于块尾字符（块中间的换行不影响块尾是否处于行首），
            // 空块保持原状态，直接返回原文
            if (chkTimestamp.IsChecked != true)
            {
                if (increment.Length > 0)
                    _atLineStart = IsNewLineChar(increment[increment.Length - 1]);
                return increment;
            }

            StringBuilder sb = new StringBuilder(increment.Length + 12);
            bool atLineStart = _atLineStart;
            DateTime now = DateTime.Now;   // 同一数据块的所有行共享同一时刻（秒级精度下无差别）
            foreach (char c in increment)
            {
                if (atLineStart && !IsNewLineChar(c))
                {
                    sb.Append('[').Append(now.ToString("HH:mm:ss")).Append("] ");
                    atLineStart = false;
                }
                sb.Append(c);
                if (IsNewLineChar(c)) atLineStart = true;   // \n / \r（含 \r\n 与单独 \r）终结一行
            }
            _atLineStart = atLineStart;
            return sb.ToString();
        }

        // ============================================================
        // 发送
        // ============================================================
        private void BtnSend_Click(object sender, RoutedEventArgs e)
        {
            if (!_service.IsConnected)
            {
                MessageBox.Show("串口未连接，无法发送。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            SendText(txtSend.Text);
        }

        /// <summary>
        /// 发送界面文本：按当前模式（hex/文本、追加换行、编码）解析并发送，累计发送字节统计。
        /// 发送按钮、定时发送、快捷指令共用此入口；hex 解析失败时弹窗提示并返回 false。
        /// </summary>
        internal bool SendText(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;

            try
            {
                if (chkSendNewLine.IsChecked == true && chkSendHex.IsChecked != true)
                    text += "\r\n";

                if (chkSendHex.IsChecked == true)
                {
                    byte[] bytes = HexStringToBytes(text);
                    _service.SendBytes(bytes);
                    _sentBytesTotal += bytes.Length;
                    AppendLocalEcho(FormatHex(bytes));
                }
                else
                {
                    _service.SendText(text, _textEncoding);
                    _sentBytesTotal += _textEncoding.GetByteCount(text);
                    AppendLocalEcho(text);
                }
                return true;
            }
            catch (Exception ex)
            {
                // 设备拔出等待重连期间的发送已在服务层静默丢弃，这里只报告其余错误
                MessageBox.Show($"发送失败：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }
        }

        private void BtnClearSend_Click(object sender, RoutedEventArgs e) => txtSend.Clear();

        /// <summary>打开快捷指令窗口（非模态：发送指令时不阻塞主窗口操作）。</summary>
        private void BtnQuickCommands_Click(object sender, RoutedEventArgs e)
        {
            var wnd = new QuickCommandWindow(this);
            wnd.Show();
        }

        // ============================================================
        // RTS / DTR 引脚控制（连接后启用，重连后按勾选重写）
        // ============================================================
        private void ChkPin_Changed(object sender, RoutedEventArgs e)
        {
            if (!_service.IsConnected) return;   // 未连接时勾选仅作预设
            ApplyPinStates();
        }

        /// <summary>把当前勾选的引脚状态写入端口（手动连接 / 自动重连后端口实例重建，需重新应用）。</summary>
        private void ApplyPinStates()
        {
            _service.SetDtr(chkDtr.IsChecked == true);
            _service.SetRts(chkRts.IsChecked == true);
        }

        // ============================================================
        // 定时发送（后台线程按间隔循环发送发送框内容）
        // ============================================================
        private void BtnTimerSend_Click(object sender, RoutedEventArgs e)
        {
            if (_timerSending)
            {
                _timerSending = false;
                _timerGen++;   // 代次递增：旧线程（可能在 Sleep 中）醒来后立即退出
                btnTimerSend.Content = "定时发送";
                UpdateStatus("已停止定时发送");
                return;
            }
            if (!_service.IsConnected)
            {
                MessageBox.Show("串口未连接，无法定时发送。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            if (!int.TryParse(txtTimerInterval.Text.Trim(), out int interval) || interval <= 0)
            {
                MessageBox.Show("定时发送间隔必须是正整数（毫秒）。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            _timerSending = true;
            int gen = ++_timerGen;
            btnTimerSend.Content = "停止定时";
            UpdateStatus($"定时发送已启动：每 {interval} ms");
            var thread = new Thread(() => TimerSendLoop(interval, gen)) { IsBackground = true };
            thread.Start();
        }

        /// <summary>
        /// 定时发送循环：每次经 Dispatcher.Invoke 回 UI 线程取发送框内容并发送（控件只能 UI 线程访问），
        /// 间隔 = 发送耗时 + Sleep(interval)，后台线程不触碰任何控件。
        /// 代次（gen）守卫：停止后立即重启不会产生双线程并发发送；发送失败（hex 格式错误等）即停止。
        /// </summary>
        private void TimerSendLoop(int interval, int gen)
        {
            try
            {
                while (_timerSending && gen == _timerGen)
                {
                    Dispatcher.Invoke(() =>
                    {
                        // 停止标志在 Invoke 排队期间可能已被置位：再查一次，避免停止后仍发送一轮
                        if (!_timerSending || gen != _timerGen || !_service.IsConnected) return;
                        if (!SendText(txtSend.Text))
                        {
                            // 发送失败（如发送框被改成非法 hex）：停止定时，避免按间隔无限弹窗
                            _timerSending = false;
                            _timerGen++;
                            btnTimerSend.Content = "定时发送";
                        }
                    });
                    Thread.Sleep(interval);
                }
            }
            catch
            {
                // 窗口关闭 / 应用退出时 Invoke 可能抛异常：静默退出（后台线程不留下未处理异常）
            }
        }

        /// <summary>打开帧解析窗口（单实例，重复点击激活；关闭后引用置空，再次点击重建）。</summary>
        private void BtnFrameParser_Click(object sender, RoutedEventArgs e)
        {
            if (_frameWindow == null)
            {
                _frameWindow = new FrameParserWindow();
                _frameWindow.Closed += (s, args) => _frameWindow = null;
            }
            if (_frameWindow.IsVisible)
            {
                _frameWindow.Activate();
                return;
            }
            _frameWindow.Show();
        }

        private void BtnClearReceive_Click(object sender, RoutedEventArgs e)
        {
            _receivedBytesTotal = 0;
            _lastStatsRx = 0;   // 速率快照同步复位，避免清空后速率虚高
            _textDecoder.Reset();   // 视觉清空后解码状态同步复位，避免新数据开头残留乱码
            _atLineStart = true;    // 清空后回到行首状态：新数据首行应补时间戳
            _pausedBuffer.Clear();  // 暂停缓冲一并清空，恢复时不再显示旧数据
            // 同时复位搜索 / 筛选状态，保证清空后显示一致
            _searchIndex = -1;
            _filterState = null;
            txtReceive.Clear();
            _receiveTextLength = 0;
            UpdateStatus("已清空接收区");
        }

        // ============================================================
        // 搜索 / 筛选（接收区左下角悬浮按钮）
        // ============================================================
        private void BtnSearchReceive_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new SearchFilterWindow(this, false);
            dlg.ShowDialog();   // 模态打开；期间接收数据仍在追加，查找始终基于当前接收区内容
        }

        private void BtnFilterReceive_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new SearchFilterWindow(this, true);
            dlg.ShowDialog();
        }

        /// <summary>在接收区中查找匹配项并高亮显示；找到返回 true。backward = true 时向上查找（否则向下），找不到则环绕。</summary>
        internal bool Find(string term, bool matchCase, bool wholeWord, bool backward)
        {
            string text = txtReceive.Text;
            if (string.IsNullOrEmpty(term) || text.Length == 0) return false;
            UpdateSearchState(term, matchCase, wholeWord);

            int idx;
            if (backward)
            {
                int limit = _searchIndex < 0 ? text.Length : _searchIndex;
                idx = FindMatchIndexBackward(text, term, limit, matchCase, wholeWord);
                if (idx < 0 && limit > 0)
                    idx = FindMatchIndexBackward(text, term, text.Length, matchCase, wholeWord);   // 环绕
            }
            else
            {
                int from = _searchIndex < 0 ? 0 : _searchIndex + term.Length;
                idx = FindMatchIndex(text, term, from, matchCase, wholeWord);
                if (idx < 0 && from > 0) idx = FindMatchIndex(text, term, 0, matchCase, wholeWord);   // 环绕
            }
            if (idx < 0) return false;

            HighlightMatch(idx, term.Length);
            _searchIndex = idx;
            return true;
        }

        /// <summary>搜索词或匹配选项变化时重置查找位置（接收区清空时由 BtnClearReceive_Click 复位）。</summary>
        private void UpdateSearchState(string term, bool matchCase, bool wholeWord)
        {
            if (_searchTerm != term || _searchMatchCase != matchCase || _searchWholeWord != wholeWord)
                _searchIndex = -1;
            _searchTerm = term;
            _searchMatchCase = matchCase;
            _searchWholeWord = wholeWord;
        }

        /// <summary>选中并滚动到匹配项（接收框只读，选中即高亮显示）。</summary>
        private void HighlightMatch(int index, int length)
        {
            txtReceive.Select(index, length);
            txtReceive.ScrollToLine(txtReceive.GetLineIndexFromCharacterIndex(index));
        }

        /// <summary>应用筛选：接收区仅显示包含筛选内容的行，新数据只追加匹配行。</summary>
        internal void ApplyFilter(string term, bool matchCase, bool wholeWord)
        {
            if (string.IsNullOrEmpty(term))
            {
                ClearFilter();
                return;
            }
            if (_filterState == null)
            {
                // 首次启用：以当前接收区内容作为全量文本（此后新数据同时记入缓冲）
                _filterState = new FilterState();
                _filterState.Buffer.Append(txtReceive.Text);
            }
            _filterState.Term = term;
            _filterState.MatchCase = matchCase;
            _filterState.WholeWord = wholeWord;
            RebuildFilteredView();
            UpdateStatus($"筛选已启用：{term}");
        }

        /// <summary>清除筛选：恢复显示完整接收内容。</summary>
        internal void ClearFilter()
        {
            if (_filterState == null) return;
            txtReceive.Text = _filterState.Buffer.ToString();
            _receiveTextLength = txtReceive.Text.Length;
            _filterState = null;
            if (chkAutoScroll.IsChecked == true) txtReceive.ScrollToEnd();
            UpdateStatus("已清除筛选");
        }

        /// <summary>按当前筛选条件重建接收区显示内容。</summary>
        private void RebuildFilteredView()
        {
            txtReceive.Clear();
            _receiveTextLength = 0;
            if (_filterState == null) return;
            foreach (string line in SplitLines(_filterState.Buffer.ToString()))
                AppendLineIfMatches(line);
            if (chkAutoScroll.IsChecked == true) txtReceive.ScrollToEnd();
        }

        /// <summary>筛选启用期间追加新数据：只显示包含筛选内容的行，未结束的行暂存待续。</summary>
        private void AppendFiltered(string increment)
        {
            // 不变量：PendingLine 中不含换行；仅当本块含换行时才整行拆分处理
            if (increment.IndexOfAny(NewLineChars) < 0)
            {
                _filterState.PendingLine.Append(increment);
                return;
            }
            _filterState.PendingLine.Append(increment);
            string[] lines = SplitLines(_filterState.PendingLine.ToString());
            _filterState.PendingLine.Clear();
            _filterState.PendingLine.Append(lines[lines.Length - 1]);   // 末段可能是不完整的一行，继续暂存
            for (int i = 0; i < lines.Length - 1; i++)
                AppendLineIfMatches(lines[i]);
        }

        /// <summary>整行包含筛选内容时追加显示（含行尾换行）。</summary>
        private void AppendLineIfMatches(string line)
        {
            if (FindMatchIndex(line, _filterState.Term, 0, _filterState.MatchCase, _filterState.WholeWord) >= 0)
            {
                txtReceive.AppendText(line);
                txtReceive.AppendText("\r\n");
                _receiveTextLength += line.Length + 2;
            }
        }

        /// <summary>换行符检测（与 SplitLines 支持的范围一致）。</summary>
        private static readonly char[] NewLineChars = { '\r', '\n' };

        /// <summary>判断字符是否为换行符（与 <see cref="NewLineChars"/> 一致）。</summary>
        private static bool IsNewLineChar(char c) => c == '\r' || c == '\n';

        /// <summary>按行拆分（兼容 \r\n、\r、\n 三种换行）。</summary>
        private static string[] SplitLines(string text) =>
            text.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);

        /// <summary>在 text 中从 startIndex 起正向查找 term，返回匹配起始下标；未找到返回 -1。</summary>
        private static int FindMatchIndex(string text, string term, int startIndex, bool matchCase, bool wholeWord)
        {
            if (startIndex < 0 || startIndex >= text.Length) return -1;
            StringComparison cmp = matchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
            int idx = text.IndexOf(term, startIndex, cmp);
            while (idx >= 0)
            {
                if (!wholeWord || IsWholeWordAt(text, idx, term.Length)) return idx;
                idx = text.IndexOf(term, idx + 1, cmp);
            }
            return -1;
        }

        /// <summary>查找起点小于 limit 的最后一个匹配；未找到返回 -1。
        /// 利用 LastIndexOf 的语义（匹配整体落在 [0, startIndex] 内）：
        /// 起点 &lt; limit 等价于终点 &lt;= limit - 1，故 startIndex = limit + term.Length - 2。</summary>
        private static int FindMatchIndexBackward(string text, string term, int limit, bool matchCase, bool wholeWord)
        {
            if (limit <= 0) return -1;
            StringComparison cmp = matchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
            int startIndex = Math.Min(limit, text.Length) + term.Length - 2;
            if (startIndex > text.Length - 1) startIndex = text.Length - 1;
            int idx = text.LastIndexOf(term, startIndex, cmp);
            while (idx >= 0)
            {
                if (!wholeWord || IsWholeWordAt(text, idx, term.Length)) return idx;
                if (idx == 0) break;
                idx = text.LastIndexOf(term, idx + term.Length - 2, cmp);   // 向前找下一个起点更小的匹配
            }
            return -1;
        }

        /// <summary>判断 [index, index+length) 是否为全词匹配（两侧都不是单词字符，中文按一个词处理）。</summary>
        private static bool IsWholeWordAt(string text, int index, int length)
        {
            bool leftOk = index == 0 || !IsWordChar(text[index - 1]);
            bool rightOk = index + length >= text.Length || !IsWordChar(text[index + length]);
            return leftOk && rightOk;
        }

        /// <summary>单词字符：字母（含中文）、数字、下划线。</summary>
        private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_';

        /// <summary>筛选状态：启用筛选时保存完整接收文本与筛选条件；禁用时 _filterState 为 null。</summary>
        private sealed class FilterState
        {
            public readonly StringBuilder Buffer = new StringBuilder();        // 完整接收文本
            public readonly StringBuilder PendingLine = new StringBuilder();   // 未以换行结束的行片段（不含换行）
            public string Term;
            public bool MatchCase;
            public bool WholeWord;
        }

        // ============================================================
        // 文件保存（勾选后生成 yyyy-MM-dd_HH-mm-ss.txt，随接收数据追加写入）
        // ============================================================
        private void ChkSaveFile_Changed(object sender, RoutedEventArgs e)
        {
            if (chkSaveFile.IsChecked == true)
            {
                StartSaveFile();
                return;
            }
            // 取消勾选：关闭当前保存文件（排空队列后释放）
            DisposeSaveWriter();
            txtSaveFileName.Text = string.Empty;
            UpdateFileEditState(_service.IsConnected);
        }

        /// <summary>在保存路径下以默认文件名（年月日时分秒）新建文件；路径为空时回退到“我的文档”。</summary>
        private void StartSaveFile()
        {
            string dir = txtSavePath.Text.Trim();
            if (string.IsNullOrEmpty(dir))
            {
                dir = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                txtSavePath.Text = dir;
            }
            string name = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss") + ".txt";
            try
            {
                var writer = new StreamWriter(Path.Combine(dir, name), true, new UTF8Encoding(false))
                {
                    AutoFlush = false   // 由后台写队列批量写盘，避免高频 flush 阻塞 UI
                };
                lock (_saveLock) _saveFileWriter = writer;   // 与后台写线程同步可见
                _saveFilePath = Path.Combine(dir, name);
                txtSaveFileName.Text = name;
                UpdateStatus($"开始保存到 {_saveFilePath}");
            }
            catch (Exception ex)
            {
                lock (_saveLock) _saveFileWriter = null;
                chkSaveFile.IsChecked = false;   // 创建失败则取消勾选，保持状态一致
                MessageBox.Show($"创建保存文件失败：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            UpdateFileEditState(_service.IsConnected);
        }

        /// <summary>浏览按钮：弹出文件夹选择窗口，选择文件保存路径。</summary>
        private void BtnBrowsePath_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new System.Windows.Forms.FolderBrowserDialog
            {
                Description = "选择文件保存路径",
                SelectedPath = txtSavePath.Text.Trim()
            };
            // 指定主窗口为所有者，避免对话框跑到主窗口后面
            if (dlg.ShowDialog(new Win32Window(new WindowInteropHelper(this).Handle)) != System.Windows.Forms.DialogResult.OK) return;

            txtSavePath.Text = dlg.SelectedPath;
            // 保存已开启时路径变更：关闭旧文件，以新路径重新生成默认文件名文件
            if (chkSaveFile.IsChecked == true)
            {
                DisposeSaveWriter();
                StartSaveFile();
            }
        }

        /// <summary>修改文件名（仅串口关闭时可用）：重命名磁盘上的当前保存文件。</summary>
        private void BtnModifyFileName_Click(object sender, RoutedEventArgs e)
        {
            if (_service.IsConnected)
            {
                MessageBox.Show("串口已连接，无法修改文件名。请先关闭串口。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            lock (_saveLock) { if (_saveFileWriter == null) return; }   // 未勾选文件保存，无文件可改名

            string name = txtSaveFileName.Text.Trim();
            if (string.IsNullOrEmpty(name))
            {
                MessageBox.Show("文件名不能为空。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                MessageBox.Show("文件名包含非法字符。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            if (!name.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
                name += ".txt";

            string newPath = Path.Combine(Path.GetDirectoryName(_saveFilePath), name);
            if (string.Equals(newPath, _saveFilePath, StringComparison.OrdinalIgnoreCase)) return;   // 名字未变
            if (File.Exists(newPath))
            {
                MessageBox.Show($"同名文件已存在：{name}", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            try
            {
                File.Move(_saveFilePath, newPath);
                _saveFilePath = newPath;
                txtSaveFileName.Text = name;
                UpdateStatus($"保存文件已重命名为 {name}");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"重命名失败：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>文件名编辑状态：未勾选文件保存或串口已打开时均禁止修改文件名。</summary>
        private void UpdateFileEditState(bool connected)
        {
            bool saveChecked = chkSaveFile.IsChecked == true;
            txtSaveFileName.IsReadOnly = !saveChecked || connected;
            btnModifyFileName.IsEnabled = saveChecked && !connected;
        }

        /// <summary>把接收文本加入后台写盘队列（写盘不阻塞 UI 线程；队列空则启动排空任务）。</summary>
        private void EnqueueSaveText(string text)
        {
            lock (_saveLock)
            {
                if (_saveFileWriter == null) return;   // 未开启文件保存
                _saveQueue.Enqueue(text);
                if (_saveDraining) return;
                _saveDraining = true;
            }
            Task.Run(() => DrainSaveQueue());
        }

        /// <summary>
        /// 后台排空保存队列：串行写入 StreamWriter，队列空时刷盘并退出。
        /// AutoFlush 已关闭，由本循环控制写盘时机，高频数据下显著降低 flush 开销。
        /// 写盘失败（磁盘满等）：释放 writer 并在 UI 线程复位勾选，避免队列永久停摆。
        /// </summary>
        private void DrainSaveQueue()
        {
            while (true)
            {
                string text;
                lock (_saveLock)
                {
                    if (_saveQueue.Count == 0)
                    {
                        try { _saveFileWriter?.Flush(); } catch { }   // 刷盘失败不阻塞：下次 Enqueue 重新排空
                        _saveDraining = false;
                        return;
                    }
                    text = _saveQueue.Dequeue();
                    if (_saveFileWriter != null)
                    {
                        try
                        {
                            _saveFileWriter.Write(text);
                        }
                        catch
                        {
                            // 写盘失败（磁盘满等）：放弃剩余数据、释放 writer，UI 复位保存勾选
                            _saveQueue.Clear();
                            try { _saveFileWriter.Dispose(); } catch { }
                            _saveFileWriter = null;
                            _saveDraining = false;
                            // 窗口关闭的窄竞态下 BeginInvoke 可能抛异常：包住，不影响后台线程退出
                            try
                            {
                                Dispatcher.BeginInvoke(new Action(() =>
                                {
                                    chkSaveFile.IsChecked = false;
                                    UpdateStatus("文件保存失败（磁盘错误），已停止保存");
                                }));
                            }
                            catch { }
                            return;
                        }
                    }
                }
            }
        }

        /// <summary>关闭并释放保存文件流（线程安全：锁内排空队列后释放，取消勾选不丢已接收数据）。</summary>
        private void DisposeSaveWriter()
        {
            lock (_saveLock)
            {
                // 把队列中剩余数据写完再关闭，避免取消勾选/关窗瞬间丢失已接收数据
                while (_saveQueue.Count > 0 && _saveFileWriter != null)
                {
                    try { _saveFileWriter.Write(_saveQueue.Dequeue()); }
                    catch { _saveQueue.Clear(); break; }   // 写盘失败：放弃剩余数据
                }
                try { _saveFileWriter?.Dispose(); } catch { }
                _saveFileWriter = null;
            }
        }

        // ============================================================
        // 软件更新（状态栏“检查更新”入口；启动时自动检查一次）
        // ============================================================
        private void BtnCheckUpdate_Click(object sender, RoutedEventArgs e)
        {
            _manualCheck = true;
            UpdateStatus("正在检查更新…");
            _updateService.CheckForUpdatesAsync(forceRefresh: true);   // 手动检查：绕过本地缓存强制联网
        }

        private void OnUpdateCheckCompleted(object sender, UpdateCheckResult result)
        {
            if (result.IsUpdateAvailable)
            {
                UpdateStatus($"发现新版本 v{result.LatestVersion}");
                if (_manualCheck)
                {
                    // 手动检查：弹窗升级。检查流程到此结束，立即复位标志——后续下载错误由
                    // UpdateDialog 展示（UpdateError 为多订阅事件，不复位会叠加双弹窗）
                    _manualCheck = false;
                    var dlg = new UpdateDialog(_updateService, result);
                    dlg.ShowDialog();
                }
                // 自动检查（如启动时）：仅状态栏提示，不打断用户，点“检查更新”再弹升级窗口
            }
            else
            {
                // 已是最新：无论手动/自动都统一更新状态栏，手动检查才弹窗提示
                UpdateStatus($"已是最新版本 v{result.CurrentVersion}");
                if (_manualCheck)
                {
                    _manualCheck = false;
                    MessageBox.Show($"当前已是最新版本（v{result.CurrentVersion}）。", "检查更新",
                        MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
        }

        private void OnUpdateError(object sender, string message)
        {
            UpdateStatus(message);
            if (_manualCheck)
            {
                _manualCheck = false;
                MessageBox.Show(message, "检查更新", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        /// <summary>更新完成、新版本已启动：关闭串口后退出应用（由 UpdateService 在新版本进程启动后触发）。</summary>
        private void OnUpdateApplied(object sender, EventArgs e)
        {
            _service.Close();
            Application.Current.Shutdown();
        }

        // ============================================================
        // 工具方法
        // ============================================================
        private void UpdateStatus(string message) => txtStatus.Text = "  " + message;

        private static StopBits ParseStopBits(string s)
        {
            switch (s)
            {
                case "1": return StopBits.One;
                case "1.5": return StopBits.OnePointFive;
                case "2": return StopBits.Two;
                default: return StopBits.One;
            }
        }

        private static Parity ParseParity(string s)
        {
            switch (s)
            {
                case "None": return Parity.None;
                case "Even": return Parity.Even;
                case "Odd": return Parity.Odd;
                case "Mark": return Parity.Mark;
                case "Space": return Parity.Space;
                default: return Parity.None;
            }
        }

        private static Handshake ParseHandshake(string s)
        {
            switch (s)
            {
                case "None": return Handshake.None;
                case "RTS/CTS": return Handshake.RequestToSend;
                case "XOn/XOff": return Handshake.XOnXOff;
                default: return Handshake.None;
            }
        }

        /// <summary>解析波特率：Custom 占位项或未输入时抛出异常提示用户。</summary>
        private static int ParseBaudRate(string text)
        {
            if (string.IsNullOrWhiteSpace(text) || text.Trim() == "Custom")
                throw new FormatException("请选择或输入具体波特率数值。");

            if (!int.TryParse(text.Trim(), out int value) || value <= 0)
                throw new FormatException("波特率必须是正整数。");

            return value;
        }

        /// <summary>将 "A1 B2 3C" 形式的字符串解析为字节数组，忽略空格/换行。</summary>
        private static byte[] HexStringToBytes(string hex)
        {
            string cleaned = hex.Replace(" ", "").Replace("\r", "").Replace("\n", "").Replace("\t", "");
            if (cleaned.Length % 2 != 0)
                throw new FormatException("Hex 格式错误：字符数必须为偶数。");

            byte[] bytes = new byte[cleaned.Length / 2];
            for (int i = 0; i < bytes.Length; i++)
                bytes[i] = Convert.ToByte(cleaned.Substring(i * 2, 2), 16);
            return bytes;
        }
    }

    /// <summary>IWin32Window 包装：给 WinForms 对话框提供 WPF 窗口句柄作为所有者。</summary>
    internal sealed class Win32Window : System.Windows.Forms.IWin32Window
    {
        public Win32Window(IntPtr handle) { Handle = handle; }
        public IntPtr Handle { get; }
    }
}
