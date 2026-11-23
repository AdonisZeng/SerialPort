using System;
using System.Windows;
using System.Windows.Threading;
using SerialPort.Services;
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

            // 日志最先初始化：启动阶段（含主题恢复）发生的异常也要能记录下来。
            // 必须在创建任何窗口之前完成，否则早期异常无处可记。
            LogService.Init();
            LogService.Info($"程序启动：版本 {UpdateService.CurrentVersion}，程序集 {System.Reflection.Assembly.GetExecutingAssembly().Location}");
            LogService.Info($"本次日志文件：{LogService.LogFilePath}");

            // 全局兜底保存：任何未处理异常（UI 线程 / 后台线程）导致进程退出前，
            // 把已写入 Settings 内存对象的配置落盘，配合各控件的"改动即存"保证配置不丢。
            DispatcherUnhandledException += (s, args) =>
            {
                try { SerialPort.Properties.Settings.Default.Save(); } catch { }
                // 记录未处理异常（含类型、消息、堆栈）：崩溃现场只能从这里还原
                LogService.Error("UI 线程未处理异常", args.Exception);
                // 交给 WPF 默认处理：弹窗并终止进程（不设 args.Handled = true）
            };
            AppDomain.CurrentDomain.UnhandledException += (s, args) =>
            {
                try { SerialPort.Properties.Settings.Default.Save(); } catch { }
                // 后台线程未处理异常：进程即将终止，此刻必须同步落盘，不能走异步队列
                LogService.Fatal("后台线程未处理异常", args.ExceptionObject as Exception);
            };

            // Application.Properties 属性遮蔽了命名空间，这里必须用全限定名
            if (SerialPort.Properties.Settings.Default.Theme == (int)ThemeMode.Dark)
                ThemeManager.Apply(ThemeMode.Dark);
        }

        /// <summary>程序退出时排空日志队列并关闭日志文件（避免丢失最后几条日志）。</summary>
        protected override void OnExit(ExitEventArgs e)
        {
            try { LogService.Info("程序退出。"); } catch { }
            try { LogService.Shutdown(); } catch { }
            base.OnExit(e);
        }
    }
}
