using System;
using System.IO;
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
        private readonly UpdateService _updateService = new UpdateService();
        private readonly DispatcherTimer _timerPortCheck = new DispatcherTimer();
        private bool _manualCheck;   // 手动检查标志：手动触发时结果/失败需要弹窗提示
        private long _receivedBytesTotal;
        private StreamWriter _saveFileWriter;   // 文件保存流（勾选“文件保存”时创建）
        private string _saveFilePath;           // 当前保存文件的完整路径

        // 搜索状态（接收区查找）
        private string _searchTerm;
        private bool _searchMatchCase;
        private bool _searchWholeWord;
        private int _searchIndex = -1;          // 最近一次匹配的起始下标；-1 = 无

        // 筛选状态（_filterState 非空表示筛选已启用）
        private FilterState _filterState;

        // 时间戳显示状态（状态机始终运行，与复选框勾选状态无关）
        private bool _atLineStart = true;   // 当前处于行首（下一个非换行字符应补时间戳）

        public MainWindow()
        {
            InitializeComponent();

            // 文件保存：默认保存路径为“我的文档”
            txtSavePath.Text = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

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

            // 订阅更新服务事件；启动时后台检查一次新版本（不阻塞界面）
            _updateService.CheckCompleted += OnUpdateCheckCompleted;
            _updateService.UpdateError += OnUpdateError;
            _updateService.UpdateApplied += OnUpdateApplied;
            _updateService.CheckForUpdatesAsync();

            Closed += (s, e) =>
            {
                _timerPortCheck.Stop();       // 停止端口轮询
                _saveFileWriter?.Dispose();   // 关闭保存文件
                _service.Dispose();           // 关闭串口并释放资源（Dispose 幂等）
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
            UpdateFileEditState(connected);   // 串口打开时禁止修改文件名
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

            string increment = GetTimestampedText(GetIncrementText(e.Data));

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
            }

            // 勾选“文件保存”时把同样的内容写入文件
            if (_saveFileWriter != null)
                _saveFileWriter.Write(increment);

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

        /// <summary>
        /// 给单次接收的增量文本补时间戳：每行行首加 [HH:mm:ss]。
        /// 行以 \n、\r、\r\n（含跨块拆分的 \r+ 开头\n）终结；空行不加戳；
        /// 仅当勾选"时间戳显示"时输出前缀，行首位置跟踪始终进行。
        /// </summary>
        private string GetTimestampedText(string increment)
        {
            // 未勾选时无需构造输出：只跟踪行首位置（复用 NewLineChars 快速扫描），直接返回原文
            if (chkTimestamp.IsChecked != true)
            {
                if (increment.IndexOfAny(NewLineChars) >= 0) _atLineStart = true;
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
            // 同时复位搜索 / 筛选状态，保证清空后显示一致
            _searchIndex = -1;
            _filterState = null;
            txtReceive.Clear();
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
            _filterState = null;
            if (chkAutoScroll.IsChecked == true) txtReceive.ScrollToEnd();
            UpdateStatus("已清除筛选");
        }

        /// <summary>按当前筛选条件重建接收区显示内容。</summary>
        private void RebuildFilteredView()
        {
            txtReceive.Clear();
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
            // 取消勾选：关闭当前保存文件
            if (_saveFileWriter != null)
            {
                _saveFileWriter.Dispose();
                _saveFileWriter = null;
            }
            txtSaveFileName.Text = string.Empty;
            UpdateFileEditState(_service.IsOpen);
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
                _saveFileWriter = new StreamWriter(Path.Combine(dir, name), true, new UTF8Encoding(false))
                {
                    AutoFlush = true
                };
                _saveFilePath = Path.Combine(dir, name);
                txtSaveFileName.Text = name;
                UpdateStatus($"开始保存到 {_saveFilePath}");
            }
            catch (Exception ex)
            {
                _saveFileWriter = null;
                chkSaveFile.IsChecked = false;   // 创建失败则取消勾选，保持状态一致
                MessageBox.Show($"创建保存文件失败：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            UpdateFileEditState(_service.IsOpen);
        }

        /// <summary>浏览按钮：弹出文件夹选择窗口，选择文件保存路径。</summary>
        private void BtnBrowsePath_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new System.Windows.Forms.FolderBrowserDialog
            {
                Description = "选择文件保存路径",
                SelectedPath = txtSavePath.Text.Trim()
            };
            if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;

            txtSavePath.Text = dlg.SelectedPath;
            // 保存已开启时路径变更：关闭旧文件，以新路径重新生成默认文件名文件
            if (chkSaveFile.IsChecked == true && _saveFileWriter != null)
            {
                _saveFileWriter.Dispose();
                _saveFileWriter = null;
                StartSaveFile();
            }
        }

        /// <summary>修改文件名（仅串口关闭时可用）：重命名磁盘上的当前保存文件。</summary>
        private void BtnModifyFileName_Click(object sender, RoutedEventArgs e)
        {
            if (_service.IsOpen)
            {
                MessageBox.Show("串口已连接，无法修改文件名。请先关闭串口。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            if (_saveFileWriter == null) return;   // 未勾选文件保存，无文件可改名

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
                var dlg = new UpdateDialog(_updateService, result);
                dlg.ShowDialog();
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
            try { _service.Close(); } catch { }
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
