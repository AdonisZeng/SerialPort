using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Media;

namespace SerialPort.UI
{
    /// <summary>
    /// 搜索高亮状态（可继承附加属性）：挂在主窗口上，接收区所有行元素自动继承。
    /// 属性变化经 WPF 继承传播触达每一个已生成的行容器，无需手动刷新列表。
    /// </summary>
    public static class SearchHighlight
    {
        public static readonly DependencyProperty TermProperty = DependencyProperty.RegisterAttached(
            "Term", typeof(string), typeof(SearchHighlight),
            new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.Inherits));

        public static readonly DependencyProperty MatchCaseProperty = DependencyProperty.RegisterAttached(
            "MatchCase", typeof(bool), typeof(SearchHighlight),
            new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.Inherits));

        public static readonly DependencyProperty WholeWordProperty = DependencyProperty.RegisterAttached(
            "WholeWord", typeof(bool), typeof(SearchHighlight),
            new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.Inherits));

        /// <summary>匹配片段背景色（主题相关，主题切换后由主窗口重新设置）。</summary>
        public static readonly DependencyProperty BrushProperty = DependencyProperty.RegisterAttached(
            "Brush", typeof(Brush), typeof(SearchHighlight),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.Inherits));

        public static string GetTerm(DependencyObject obj) { return (string)obj.GetValue(TermProperty); }
        public static void SetTerm(DependencyObject obj, string value) { obj.SetValue(TermProperty, value); }

        public static bool GetMatchCase(DependencyObject obj) { return (bool)obj.GetValue(MatchCaseProperty); }
        public static void SetMatchCase(DependencyObject obj, bool value) { obj.SetValue(MatchCaseProperty, value); }

        public static bool GetWholeWord(DependencyObject obj) { return (bool)obj.GetValue(WholeWordProperty); }
        public static void SetWholeWord(DependencyObject obj, bool value) { obj.SetValue(WholeWordProperty, value); }

        public static Brush GetBrush(DependencyObject obj) { return (Brush)obj.GetValue(BrushProperty); }
        public static void SetBrush(DependencyObject obj, Brush value) { obj.SetValue(BrushProperty, value); }
    }

    /// <summary>
    /// 接收区行文本块：显示一行 <see cref="ReceiveLine"/>，并按继承到的搜索状态
    /// 把行内匹配片段以背景色高亮（独立着色，不依赖控件选中态）。
    /// 虚拟化下只为可见行构建 Inlines，搜索状态变化时已生成容器经继承传播自动重建。
    /// internal：属性 Line 的类型 ReceiveLine 为 internal，类型可见性须一致（同程序集 XAML 可用）。
    /// </summary>
    internal class HighlightTextBlock : TextBlock
    {
        public static readonly DependencyProperty LineProperty = DependencyProperty.Register(
            "Line", typeof(ReceiveLine), typeof(HighlightTextBlock),
            new PropertyMetadata(null, OnRebuild));

        // 仅用于接收可继承搜索状态的内部依赖属性（绑定源 = 自身附加属性）
        private static readonly DependencyProperty TermProperty = DependencyProperty.Register(
            "Term", typeof(string), typeof(HighlightTextBlock), new PropertyMetadata(string.Empty, OnRebuild));
        private static readonly DependencyProperty MatchCaseProperty = DependencyProperty.Register(
            "MatchCase", typeof(bool), typeof(HighlightTextBlock), new PropertyMetadata(false, OnRebuild));
        private static readonly DependencyProperty WholeWordProperty = DependencyProperty.Register(
            "WholeWord", typeof(bool), typeof(HighlightTextBlock), new PropertyMetadata(false, OnRebuild));
        private static readonly DependencyProperty BrushProperty = DependencyProperty.Register(
            "Brush", typeof(Brush), typeof(HighlightTextBlock), new PropertyMetadata(null, OnRebuild));

        public ReceiveLine Line
        {
            get { return (ReceiveLine)GetValue(LineProperty); }
            set { SetValue(LineProperty, value); }
        }

        public HighlightTextBlock()
        {
            // 把继承链上的搜索状态"转发"为本类依赖属性：祖先（主窗口）上 Set* 后自动收到变化回调
            SetBinding(TermProperty, new Binding { Path = new PropertyPath(SearchHighlight.TermProperty), Source = this });
            SetBinding(MatchCaseProperty, new Binding { Path = new PropertyPath(SearchHighlight.MatchCaseProperty), Source = this });
            SetBinding(WholeWordProperty, new Binding { Path = new PropertyPath(SearchHighlight.WholeWordProperty), Source = this });
            SetBinding(BrushProperty, new Binding { Path = new PropertyPath(SearchHighlight.BrushProperty), Source = this });
        }

        private static void OnRebuild(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            ((HighlightTextBlock)d).Rebuild();
        }

        /// <summary>按当前搜索状态重建行内 Inlines（匹配片段加背景色 Run）。</summary>
        private void Rebuild()
        {
            Inlines.Clear();
            ReceiveLine line = Line;
            if (line == null) return;

            string text = line.Text;
            string term = (string)GetValue(TermProperty);

            // 无搜索词 / 无匹配：整行一个 Run；空行用一个空格占位保持行高（否则虚拟化下空行视觉上消失）
            if (string.IsNullOrEmpty(term) || string.IsNullOrEmpty(text) ||
                TextMatch.FindMatchIndex(text, term, 0,
                    (bool)GetValue(MatchCaseProperty), (bool)GetValue(WholeWordProperty)) < 0)
            {
                Inlines.Add(new Run(text.Length == 0 ? " " : text));
                return;
            }

            var brush = (Brush)GetValue(BrushProperty);
            var segments = TextMatch.GetMatchSegments(text, term,
                (bool)GetValue(MatchCaseProperty), (bool)GetValue(WholeWordProperty));

            int pos = 0;
            for (int i = 0; i < segments.Count; i++)
            {
                if (segments[i].Start > pos)
                    Inlines.Add(new Run(text.Substring(pos, segments[i].Start - pos)));
                var run = new Run(text.Substring(segments[i].Start, segments[i].Length));
                if (brush != null) run.Background = brush;
                Inlines.Add(run);
                pos = segments[i].Start + segments[i].Length;
            }
            if (pos < text.Length)
                Inlines.Add(new Run(text.Substring(pos)));
        }
    }
}
