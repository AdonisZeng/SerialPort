using System;
using System.Threading;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using SerialPort.Services;

namespace SerialPort
{
    /// <summary>
    /// 软件更新对话框：展示版本信息与更新说明，确认后由 UpdateService 下载并替换程序。
    /// 下载进度 / 错误通过订阅 UpdateService 事件显示（事件已在 UI 线程回调）。
    /// </summary>
    public partial class UpdateDialog : Window
    {
        private readonly UpdateService _service;
        private readonly UpdateCheckResult _result;
        private bool _updating;                 // 更新进行中
        private CancellationTokenSource _cts;   // 更新任务取消源（关窗时取消）

        public UpdateDialog(UpdateService service, UpdateCheckResult result)
        {
            InitializeComponent();
            _service = service;
            _result = result;

            txtTitle.Text = $"发现新版本 v{result.LatestVersion}";
            txtVersionInfo.Text = $"当前版本 v{result.CurrentVersion}　→　最新版本 v{result.LatestVersion}";
            RenderMarkdown(string.IsNullOrEmpty(result.ReleaseNotes)
                ? "（该版本未填写更新说明）"
                : result.ReleaseNotes);

            Loaded += OnLoaded;
            Closed += OnClosed;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            _service.UpdateProgress += OnUpdateProgress;
            _service.UpdateError += OnUpdateError;
        }

        /// <summary>
        /// 轻量 Markdown 渲染（发布说明）：# 标题加粗放大、- 列表加项目符号、
        /// 反引号代码块等宽灰色、行内 **加粗** 与 `代码` 分别加粗 / 等宽着色。
        /// 仅支持常见行形态，其余按普通文本显示。
        /// </summary>
        private void RenderMarkdown(string markdown)
        {
            var doc = new FlowDocument();
            bool inCodeBlock = false;
            foreach (string rawLine in markdown.Split('\n'))
            {
                string line = rawLine.TrimEnd('\r');
                if (line.TrimStart().StartsWith("```"))
                {
                    inCodeBlock = !inCodeBlock;   // 代码块包裹行不渲染
                    continue;
                }
                if (inCodeBlock)
                {
                    var codePara = new Paragraph { Margin = new Thickness(0, 1, 0, 1) };
                    codePara.Foreground = new SolidColorBrush(Color.FromRgb(0x8A, 0x8F, 0x9C));
                    codePara.FontFamily = new FontFamily("Consolas");
                    codePara.Inlines.Add(line);
                    doc.Blocks.Add(codePara);
                    continue;
                }

                Paragraph p = new Paragraph { Margin = new Thickness(0, 2, 0, 2) };
                if (line.StartsWith("### "))
                    p.Inlines.Add(new Bold(new Run(line.Substring(4))) { FontSize = 13 });
                else if (line.StartsWith("## "))
                    p.Inlines.Add(new Bold(new Run(line.Substring(3))) { FontSize = 14 });
                else if (line.StartsWith("# "))
                    p.Inlines.Add(new Bold(new Run(line.Substring(2))) { FontSize = 15 });
                else if (line.TrimStart().StartsWith("- ") || line.TrimStart().StartsWith("* "))
                    p.Inlines.Add(new Run("• " + line.TrimStart().Substring(2)));
                else if (string.IsNullOrWhiteSpace(line))
                    continue;   // 空行：不产生段落
                else
                    p.Inlines.Add(ParseInline(line));

                doc.Blocks.Add(p);
            }
            txtReleaseNotes.Document = doc;
        }

        /// <summary>行内解析：**加粗** 与 `代码`（等宽 + 主题前景）。</summary>
        private static Span ParseInline(string text)
        {
            var span = new Span();
            int i = 0;
            while (i < text.Length)
            {
                int bold = text.IndexOf("**", i, StringComparison.Ordinal);
                int code = text.IndexOf('`', i);
                int next;
                if (bold < 0 && code < 0)
                {
                    span.Inlines.Add(text.Substring(i));
                    break;
                }
                if (code < 0 || (bold >= 0 && bold < code)) next = bold;
                else next = code;

                if (next > i) span.Inlines.Add(text.Substring(i, next - i));
                if (next == bold)
                {
                    int end = text.IndexOf("**", next + 2, StringComparison.Ordinal);
                    if (end < 0)
                    {
                        span.Inlines.Add(text.Substring(next));
                        break;
                    }
                    span.Inlines.Add(new Bold(new Run(text.Substring(next + 2, end - next - 2))));
                    i = end + 2;
                }
                else
                {
                    int end = text.IndexOf('`', next + 1);
                    if (end < 0)
                    {
                        span.Inlines.Add(text.Substring(next));
                        break;
                    }
                    var run = new Run(text.Substring(next + 1, end - next - 1))
                    {
                        FontFamily = new FontFamily("Consolas")
                    };
                    span.Inlines.Add(run);
                    i = end + 1;
                }
            }
            return span;
        }

        private void OnClosed(object sender, EventArgs e)
        {
            // 更新进行中关窗：取消后台任务。绝不让已关闭窗口的更新继续替换程序并强制退出应用
            if (_updating) _cts?.Cancel();
            _service.UpdateProgress -= OnUpdateProgress;
            _service.UpdateError -= OnUpdateError;
        }

        private void BtnUpdate_Click(object sender, RoutedEventArgs e)
        {
            if (_updating) return;
            _updating = true;
            btnUpdate.IsEnabled = false;
            btnLater.IsEnabled = false;
            txtError.Visibility = Visibility.Collapsed;
            txtProgress.Text = "正在下载…";
            panelProgress.Visibility = Visibility.Visible;
            _cts = new CancellationTokenSource();
            _service.DownloadAndApplyAsync(_result, _cts.Token);
        }

        private void OnUpdateProgress(object sender, int percent)
        {
            progressBar.Value = percent;
            txtProgress.Text = $"正在下载… {percent}%";
        }

        private void OnUpdateError(object sender, string message)
        {
            _updating = false;
            panelProgress.Visibility = Visibility.Collapsed;
            txtError.Text = message;
            txtError.Visibility = Visibility.Visible;
            // 失败后允许重试或取消
            btnUpdate.Content = "重试";
            btnUpdate.IsEnabled = true;
            btnLater.IsEnabled = true;
        }

        private void BtnLater_Click(object sender, RoutedEventArgs e) => Close();
    }
}
