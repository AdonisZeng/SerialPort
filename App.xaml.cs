using System;
using System.Windows;
using System.Windows.Threading;
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

            // 全局兜底保存：任何未处理异常（UI 线程 / 后台线程）导致进程退出前，
            // 把已写入 Settings 内存对象的配置落盘，配合各控件的"改动即存"保证配置不丢。
            DispatcherUnhandledException += (s, args) =>
            {
                try { SerialPort.Properties.Settings.Default.Save(); } catch { }
            };
            AppDomain.CurrentDomain.UnhandledException += (s, args) =>
            {
                try { SerialPort.Properties.Settings.Default.Save(); } catch { }
            };

            // Application.Properties 属性遮蔽了命名空间，这里必须用全限定名
            if (SerialPort.Properties.Settings.Default.Theme == (int)ThemeMode.Dark)
                ThemeManager.Apply(ThemeMode.Dark);
        }
    }
}
