using System.Windows;
using System.Windows.Input;

namespace SerialPort
{
    /// <summary>
    /// 查找 / 筛选窗口（按 filterMode 切换）：
    /// 搜索模式在接收区中查找下一个 / 上一个匹配项并高亮；
    /// 筛选模式按行过滤接收区显示内容。模态打开，期间数据仍在追加。
    /// </summary>
    public partial class SearchFilterWindow : Window
    {
        private readonly MainWindow _main;
        private readonly bool _filterMode;

        public SearchFilterWindow(MainWindow main, bool filterMode)
        {
            InitializeComponent();
            _main = main;
            _filterMode = filterMode;
            Owner = main;

            Title = filterMode ? "筛选" : "搜索";
            lblInput.Content = filterMode ? "筛选内容" : "查找内容";
            btnFindNext.Visibility = filterMode ? Visibility.Collapsed : Visibility.Visible;
            btnFindPrev.Visibility = filterMode ? Visibility.Collapsed : Visibility.Visible;
            btnApplyFilter.Visibility = filterMode ? Visibility.Visible : Visibility.Collapsed;
            btnClearFilter.Visibility = filterMode ? Visibility.Visible : Visibility.Collapsed;

            Loaded += (s, e) => txtTerm.Focus();
        }

        private void BtnFindNext_Click(object sender, RoutedEventArgs e) => Find(false);

        private void BtnFindPrev_Click(object sender, RoutedEventArgs e) => Find(true);

        private void BtnApplyFilter_Click(object sender, RoutedEventArgs e) => ApplyFilter();

        private void BtnClearFilter_Click(object sender, RoutedEventArgs e)
        {
            _main.ClearFilter();
            txtStatus.Text = "已清除筛选。";
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();

        /// <summary>输入框内按回车：搜索模式 = 查找下一个，筛选模式 = 筛选。</summary>
        private void TxtTerm_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                if (_filterMode) ApplyFilter(); else Find(false);
                e.Handled = true;
            }
        }

        private void Find(bool backward)
        {
            string term = txtTerm.Text;
            if (string.IsNullOrEmpty(term))
            {
                txtStatus.Text = "请输入查找内容。";
                return;
            }
            bool found = _main.Find(term, chkMatchCase.IsChecked == true, chkWholeWord.IsChecked == true, backward);
            txtStatus.Text = found ? string.Empty : "未找到匹配项。";
        }

        private void ApplyFilter()
        {
            string term = txtTerm.Text;
            if (string.IsNullOrEmpty(term))
            {
                txtStatus.Text = "请输入筛选内容。";
                return;
            }
            _main.ApplyFilter(term, chkMatchCase.IsChecked == true, chkWholeWord.IsChecked == true);
            txtStatus.Text = "筛选已应用。";
        }
    }
}
