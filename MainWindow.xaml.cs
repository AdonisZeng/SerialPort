using System;
using System.IO.Ports;
using System.Text;
using System.Windows;
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
        private readonly DispatcherTimer _timerPortCheck = new DispatcherTimer();
        private long _receivedBytesTotal;

        public MainWindow()
        {
            InitializeComponent();

            // 串口列表（初始填充不提示；运行期由定时器自动检测变化）
            RefreshPortList(false);

            // 波特率
            cboBaudRate.Items.Clear();
            foreach (BaudRate br in Enum.GetValues(typeof(BaudRate)))
                cboBaudRate.Items.Add((int)br);
            cboBaudRate.SelectedItem = (int)BaudRate.Baud9600;

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

            // 端口自动检测：1.5 秒轮询一次
            _timerPortCheck.Interval = TimeSpan.FromMilliseconds(1500);
            _timerPortCheck.Tick += TimerPortCheck_Tick;
            _timerPortCheck.Start();

            // 订阅服务事件（服务在 UI 线程构造，事件回调已在 UI 线程，可直接操作控件）
            _service.DataReceived += OnDataReceived;
            _service.ConnectionChanged += OnConnectionChanged;

            Closed += (s, e) =>
            {
                _timerPortCheck.Stop();   // 停止端口轮询
                _service.Dispose();       // 关闭串口并释放资源（Dispose 幂等）
            };

            UpdateStatus("就绪");
        }

        // ============================================================
        // 端口自动检测（1.5 秒轮询一次）
        // ============================================================
        private void TimerPortCheck_Tick(object sender, EventArgs e)
        {
            // 下拉展开时不改 Items（避免重绘异常）；DropDownOpened 事件（每次打开下拉）会补刷
            if (cboPortName.IsDropDownOpen) return;
            RefreshPortList();
        }

        /// <summary>打开下拉时刷新一次，保证用户看到的列表是最新的（PortListEquals 兜底去重）。</summary>
        private void cboPortName_DropDown(object sender, EventArgs e) => RefreshPortList();

        private void RefreshPortList(bool notify = true)
        {
            string[] ports = SerialPortService.GetAvailablePorts();
            if (PortListEquals(ports)) return;   // 无变化

            string selected = cboPortName.SelectedItem as string;
            cboPortName.Items.Clear();
            foreach (string port in ports)
                cboPortName.Items.Add(port);

            if (selected != null && Array.IndexOf(ports, selected) >= 0)
                cboPortName.SelectedItem = selected;                      // 保留当前选中项
            else if (!_service.IsOpen)
                cboPortName.SelectedIndex = ports.Length > 0 ? 0 : -1;    // 选中项消失且未连接 → 选第一个 / 清空
            // 串口已打开：不强制改选、不动连接，收发错误由既有异常路径处理

            if (notify) UpdateStatus("检测到端口变化，已更新列表");
        }

        /// <summary>集合比较（GetPortNames 顺序不稳，用 Contains 判断而非按下标）。</summary>
        private bool PortListEquals(string[] ports)
        {
            if (cboPortName.Items.Count != ports.Length) return false;
            foreach (string port in ports)
                if (!cboPortName.Items.Contains(port)) return false;
            return true;
        }

        // ============================================================
        // 打开 / 关闭串口（单个切换按钮）
        // ============================================================
        private void BtnToggleConnect_Click(object sender, RoutedEventArgs e)
        {
            if (_service.IsOpen)
            {
                try { _service.Close(); }
                catch { }
                return;
            }

            if (cboPortName.SelectedItem == null)
            {
                MessageBox.Show("请先选择串口。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            try
            {
                var config = new SerialPortConfig
                {
                    PortName = cboPortName.SelectedItem.ToString(),
                    BaudRate = (BaudRate)Convert.ToInt32(cboBaudRate.SelectedItem),
                    DataBits = (DataBits)Convert.ToInt32(cboDataBits.SelectedItem),
                    StopBits = ParseStopBits(cboStopBits.SelectedItem.ToString()),
                    Parity = ParseParity(cboParity.SelectedItem.ToString()),
                    Handshake = ParseHandshake(cboHandshake.SelectedItem.ToString())
                };
                _service.Open(config);
                UpdateStatus($"已连接 {config.PortName} @ {(int)config.BaudRate}");
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
            if (!connected) UpdateStatus("已断开");
        }

        // ============================================================
        // 主题切换（右上角按钮）
        // ============================================================
        private void BtnThemeToggle_Click(object sender, RoutedEventArgs e)
        {
            ThemeManager.Toggle();
            btnThemeToggle.Content = ThemeManager.Current == ThemeMode.Dark ? "浅色模式" : "深色模式";
        }

        // ============================================================
        // 数据接收
        // ============================================================
        private void OnDataReceived(object sender, DataReceivedEventArgs e)
        {
            if (e.Data == null || e.Data.Length == 0) return;

            _receivedBytesTotal += e.Data.Length;

            // 增量追加（WPF 下全量重赋 Text 会重建整个文本，大数据量时卡顿）
            txtReceive.AppendText(GetIncrementText(e.Data));

            if (chkAutoScroll.IsChecked == true)
                txtReceive.ScrollToEnd();

            UpdateStatus($"累计接收 {_receivedBytesTotal} 字节");
        }

        /// <summary>计算本次接收数据对应的显示文本（hex 模式每字节 "XX " 加换行，文本模式为 UTF-8 解码结果）。</summary>
        private string GetIncrementText(byte[] data)
        {
            if (chkReceiveHex.IsChecked == true)
            {
                StringBuilder sb = new StringBuilder(data.Length * 3 + 2);
                foreach (byte b in data)
                    sb.Append(b.ToString("X2")).Append(' ');
                sb.AppendLine();
                return sb.ToString();
            }
            return Encoding.UTF8.GetString(data);
        }

        // ============================================================
        // 发送
        // ============================================================
        private void BtnSend_Click(object sender, RoutedEventArgs e)
        {
            if (!_service.IsOpen)
            {
                MessageBox.Show("串口未连接，无法发送。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            string text = txtSend.Text;
            if (string.IsNullOrEmpty(text)) return;

            try
            {
                if (chkSendNewLine.IsChecked == true && chkSendHex.IsChecked != true)
                    text += "\r\n";

                if (chkSendHex.IsChecked == true)
                {
                    byte[] bytes = HexStringToBytes(text);
                    _service.SendBytes(bytes);
                }
                else
                {
                    _service.SendText(text, Encoding.UTF8);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"发送失败：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void BtnClearSend_Click(object sender, RoutedEventArgs e) => txtSend.Clear();

        private void BtnClearReceive_Click(object sender, RoutedEventArgs e)
        {
            _receivedBytesTotal = 0;
            txtReceive.Clear();
            UpdateStatus("已清空接收区");
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
}
