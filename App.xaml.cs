using System.Windows;
using SerialPort.UI;

namespace SerialPort
{
    /// <summary>
    /// App.xaml 的交互逻辑
    /// </summary>
    public partial class App : Application
    {
        /// <summary>启动时应用持久化的主题（App.xaml 已加载 LightTheme，保存为深色则替换）。</summary>
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            // Application.Properties 属性遮蔽了命名空间，这里必须用全限定名
            if (SerialPort.Properties.Settings.Default.Theme == (int)ThemeMode.Dark)
                ThemeManager.Apply(ThemeMode.Dark);
        }
    }
}
