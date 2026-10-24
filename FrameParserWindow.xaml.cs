using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SerialPort.Services;

namespace SerialPort
{
    /// <summary>
    /// 协议帧解析窗口：配置帧头 / 帧尾 / 校验后，由 MainWindow 在接收数据时喂入原始字节，
    /// 提取完整帧并以 时间 / hex / ASCII / 校验 列表展示。非模态，窗口关闭即停止解析。
    /// </summary>
    public partial class FrameParserWindow : Window
    {
        private readonly FrameParser _parser = new FrameParser();

        // 暂停期间缓存的原始字节：解析必须连续，丢弃会让帧边界永久错位
        private readonly List<byte> _pausedData = new List<byte>();
        /// <summary>暂停缓存上限：超出后丢弃最旧数据，防止长时间暂停内存无界。</summary>
        private const int MaxPausedBytes = 1024 * 1024;

        /// <summary>帧列表上限：超出截头，防止长时间运行内存无界增长。</summary>
        private const int MaxFrameRows = 2000;

        public FrameParserWindow()
        {
            InitializeComponent();
            cboChecksum.Items.Add("无");
            cboChecksum.Items.Add("CRC16-Modbus");
            cboChecksum.Items.Add("Sum8");
            cboChecksum.SelectedIndex = 0;
            ApplyConfig();
        }

        /// <summary>MainWindow 喂入接收数据（UI 线程调用；窗口打开期间才被调用）。</summary>
        internal void PushData(byte[] data)
        {
            if (data == null || data.Length == 0) return;
            if (chkPause.IsChecked == true)
            {
                int before = _pausedData.Count;
                _pausedData.AddRange(data);
                // 超限保留最新 MaxPausedBytes 字节：丢的是最旧的数据，帧边界从此处开始错位
                if (_pausedData.Count > MaxPausedBytes)
                    _pausedData.RemoveRange(0, _pausedData.Count - MaxPausedBytes);
                int discarded = before + data.Length - _pausedData.Count;
                if (discarded > 0)
                {
                    // 必须显式告知：此前 tooltip 承诺"不丢字节"，而这里确实丢了最旧的一段
                    txtStatus.Text = $"暂停缓存超限，已丢弃最旧 {discarded} 字节（帧边界可能错位）";
                }
                return;
            }
            ProcessFrames(_parser.Push(data));
        }

        /// <summary>把一批解析出的帧追加到列表（保持列表上限与底部跟随逻辑）。</summary>
        private void ProcessFrames(List<FrameParser.Frame> frames)
        {
            if (frames == null || frames.Count == 0) return;
            for (int i = 0; i < frames.Count; i++)
                AddFrame(frames[i]);
        }

        /// <summary>暂停状态切换：恢复时按序补喂暂停期间缓存的字节，再输出解析结果。</summary>
        private void ChkPause_Changed(object sender, RoutedEventArgs e)
        {
            if (chkPause.IsChecked == true || _pausedData.Count == 0) return;
            byte[] buffered = _pausedData.ToArray();
            _pausedData.Clear();
            // 分块喂入：一次性喂 1 MB 会让解析器在 UI 线程上长时间占用
            const int chunk = 16 * 1024;
            for (int offset = 0; offset < buffered.Length; offset += chunk)
            {
                int count = Math.Min(chunk, buffered.Length - offset);
                var slice = new byte[count];
                Array.Copy(buffered, offset, slice, 0, count);
                ProcessFrames(_parser.Push(slice));
            }
        }

        /// <summary>输出解析器残留的末帧（串口断开 / 流结束时由主窗口调用）。</summary>
        internal void FlushPendingFrame()
        {
            ProcessFrames(_parser.Flush());
        }

        private void AddFrame(FrameParser.Frame frame)
        {
            // 添加前判断是否停在底部：仅跟随滚动，用户上翻查看历史时不被强行拉底
            bool stickToBottom = IsListAtBottom();
            string checksum = frame.ChecksumOk ? "✓ " + frame.ChecksumText : "✗ " + frame.ChecksumText;
            lstFrames.Items.Add(new
            {
                Time = DateTime.Now.ToString("HH:mm:ss.fff"),
                Hex = HexText(frame.Data),
                Ascii = AsciiText(frame.Data),
                Checksum = checksum
            });
            while (lstFrames.Items.Count > MaxFrameRows)
                lstFrames.Items.RemoveAt(0);
            if (stickToBottom && lstFrames.Items.Count > 0)
                lstFrames.ScrollIntoView(lstFrames.Items[lstFrames.Items.Count - 1]);
        }

        private ScrollViewer _framesViewer;   // 列表模板内的 ScrollViewer（首次查找后缓存）

        /// <summary>列表是否停在底部（滚动条位于最下端时返回 true）。</summary>
        private bool IsListAtBottom()
        {
            if (_framesViewer == null)
                _framesViewer = FindVisualChild<ScrollViewer>(lstFrames);
            if (_framesViewer == null) return true;   // 模板尚未生成：保持原行为（跟随滚动）
            return _framesViewer.VerticalOffset >= _framesViewer.ScrollableHeight - 1;
        }

        /// <summary>在视觉树中递归查找指定类型的子元素。</summary>
        private static T FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
        {
            int count = VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < count; i++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(parent, i);
                if (child is T hit) return hit;
                T result = FindVisualChild<T>(child);
                if (result != null) return result;
            }
            return null;
        }

        private static string HexText(byte[] data)
        {
            var sb = new StringBuilder(data.Length * 3);
            foreach (byte b in data)
                sb.Append(b.ToString("X2")).Append(' ');
            return sb.ToString().TrimEnd();
        }

        private static string AsciiText(byte[] data)
        {
            var sb = new StringBuilder(data.Length);
            foreach (byte b in data)
                sb.Append(b >= 0x20 && b < 0x7F ? (char)b : '.');
            return sb.ToString();
        }

        // ============ 配置 ============

        private void BtnApply_Click(object sender, RoutedEventArgs e) => ApplyConfig();

        private void ApplyConfig()
        {
            byte[] header = TryParseHex(txtHeader.Text);
            byte[] footer = TryParseHex(txtFooter.Text);
            if (header == null || footer == null)
            {
                txtStatus.Text = "帧头/帧尾格式错误：hex 必须为偶数位（如 AA 55）。";
                return;
            }
            if (header.Length == 0 && footer.Length == 0)
            {
                txtStatus.Text = "帧头/帧尾至少配置一项。";
                return;
            }
            FrameChecksum checksum = cboChecksum.SelectedIndex == 1 ? FrameChecksum.Crc16Modbus
                : cboChecksum.SelectedIndex == 2 ? FrameChecksum.Sum8
                : FrameChecksum.None;
            _parser.Configure(header, footer, checksum);
            txtStatus.Text = "配置已应用。";
        }

        /// <summary>解析 hex 字符串；空串返回空数组，格式错误返回 null。</summary>
        private static byte[] TryParseHex(string hex)
        {
            string cleaned = (hex ?? "").Replace(" ", "").Replace("\t", "").Replace("\r", "").Replace("\n", "");
            if (cleaned.Length == 0) return new byte[0];
            if (cleaned.Length % 2 != 0) return null;
            byte[] bytes = new byte[cleaned.Length / 2];
            for (int i = 0; i < bytes.Length; i++)
                if (!byte.TryParse(cleaned.Substring(i * 2, 2), NumberStyles.HexNumber, null, out bytes[i]))
                    return null;
            return bytes;
        }

        private void BtnClear_Click(object sender, RoutedEventArgs e)
        {
            lstFrames.Items.Clear();
            txtStatus.Text = "列表已清空。";
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();
    }
}
