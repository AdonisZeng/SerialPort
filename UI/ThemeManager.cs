using System;
using System.Windows;

namespace SerialPort.UI
{
    /// <summary>主题模式。</summary>
    public enum ThemeMode
    {
        Light,
        Dark
    }

    /// <summary>
    /// 主题管理器（WPF 版）：通过替换 App 级合并字典中的主题资源字典实现深浅主题切换。
    /// 所有引用主题颜色的控件样式均使用 DynamicResource，替换字典后自动刷新。
    /// 强调色（打开串口 / 发送按钮）不随主题变化，连接状态色由 MainWindow 维护。
    /// 主题不持久化，程序启动恒为浅色（与源项目一致）。
    /// </summary>
    public static class ThemeManager
    {
        /// <summary>当前主题。</summary>
        public static ThemeMode Current { get; private set; } = ThemeMode.Light;

        /// <summary>切换当前主题并立即生效。</summary>
        public static void Toggle()
        {
            Apply(Current == ThemeMode.Light ? ThemeMode.Dark : ThemeMode.Light);
        }

        /// <summary>
        /// 应用指定主题：在 App 级合并字典中按 Source 定位主题字典并替换。
        /// 不依赖字典位置约定（旧实现假设主题字典恒为第 0 项），找不到时才插入到最前。
        /// </summary>
        public static void Apply(ThemeMode mode)
        {
            Current = mode;
            string source = mode == ThemeMode.Dark ? "Themes/DarkTheme.xaml" : "Themes/LightTheme.xaml";
            string other = mode == ThemeMode.Dark ? "Themes/LightTheme.xaml" : "Themes/DarkTheme.xaml";

            var dictionaries = Application.Current.Resources.MergedDictionaries;
            for (int i = 0; i < dictionaries.Count; i++)
            {
                string current = dictionaries[i].Source?.OriginalString ?? string.Empty;
                if (current.EndsWith(source, StringComparison.OrdinalIgnoreCase) ||
                    current.EndsWith(other, StringComparison.OrdinalIgnoreCase))
                {
                    dictionaries[i] = new ResourceDictionary { Source = new Uri(source, UriKind.Relative) };
                    return;
                }
            }

            // 防御：合并字典中不存在主题项（理论上不会发生，App.xaml 启动即含 LightTheme）
            dictionaries.Insert(0, new ResourceDictionary { Source = new Uri(source, UriKind.Relative) });
        }
    }
}
