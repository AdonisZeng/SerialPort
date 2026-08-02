using System;
using System.Windows;
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
        private bool _updating;   // 更新进行中

        public UpdateDialog(UpdateService service, UpdateCheckResult result)
        {
            InitializeComponent();
            _service = service;
            _result = result;

            txtTitle.Text = $"发现新版本 v{result.LatestVersion}";
            txtVersionInfo.Text = $"当前版本 v{result.CurrentVersion}　→　最新版本 v{result.LatestVersion}";
            txtReleaseNotes.Text = string.IsNullOrEmpty(result.ReleaseNotes)
                ? "（该版本未填写更新说明）"
                : result.ReleaseNotes;

            Loaded += OnLoaded;
            Closed += OnClosed;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            _service.UpdateProgress += OnUpdateProgress;
            _service.UpdateError += OnUpdateError;
        }

        private void OnClosed(object sender, EventArgs e)
        {
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
            _service.DownloadAndApplyAsync(_result);
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
