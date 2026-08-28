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
            if (chkPause.IsChecked == true) return;
            List<FrameParser.Frame> frames = _parser.Push(data);
            foreach (FrameParser.Frame frame in frames)
                AddFrame(frame);
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
