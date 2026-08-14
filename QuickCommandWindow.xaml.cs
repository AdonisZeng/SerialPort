using System;
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Input;

namespace SerialPort
{
    /// <summary>
    /// 快捷指令窗口：管理预设发送命令（增删改 / 排序 / 发送）。
    /// 命令持久化到 Settings.QuickCommands（StringCollection，每项 "名称\t内容"）。
    /// 非模态打开，发送走主窗口 SendText（与发送按钮 / 定时发送同一入口）。
    /// </summary>
    public partial class QuickCommandWindow : Window
    {
        private readonly MainWindow _main;
        private readonly StringCollection _items = new StringCollection();
        private bool _loading;   // 加载 / 刷新列表时抑制 SelectionChanged 回填编辑框

        public QuickCommandWindow(MainWindow main)
        {
            InitializeComponent();
            _main = main;
            Owner = main;
            LoadCommands();
        }

        // ============ 数据加载 / 保存 ============

        private void LoadCommands()
        {
            _loading = true;
            _items.Clear();
            StringCollection saved = Properties.Settings.Default.QuickCommands;
            if (saved != null)
                foreach (string item in saved)
                    _items.Add(item);
            RefreshList();
            _loading = false;
        }

        private void SaveCommands()
        {
            StringCollection saved = Properties.Settings.Default.QuickCommands ?? new StringCollection();
            saved.Clear();
            foreach (string item in _items)
                saved.Add(item);
            Properties.Settings.Default.QuickCommands = saved;
            Properties.Settings.Default.Save();
        }

        private void RefreshList()
        {
            lstCommands.Items.Clear();
            foreach (string item in _items)
            {
                string name, text;
                SplitItem(item, out name, out text);
                lstCommands.Items.Add(string.IsNullOrEmpty(name) ? Truncate(text) : name);
            }
        }

        /// <summary>解析 "名称\t内容"；无分隔符时整项作为内容。</summary>
        private static void SplitItem(string item, out string name, out string text)
        {
            int idx = item.IndexOf('\t');
            if (idx < 0)
            {
                name = string.Empty;
                text = item;
            }
            else
            {
                name = item.Substring(0, idx);
                text = item.Substring(idx + 1);
            }
        }

        /// <summary>内容摘要（列表显示用）：超长截断。</summary>
        private static string Truncate(string s)
        {
            string flat = s.Replace("\r", " ").Replace("\n", " ");
            return flat.Length <= 20 ? flat : flat.Substring(0, 20) + "…";
        }

        // ============ 列表交互 ============

        private void LstCommands_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (_loading || lstCommands.SelectedIndex < 0 || lstCommands.SelectedIndex >= _items.Count) return;
            string name, text;
            SplitItem(_items[lstCommands.SelectedIndex], out name, out text);
            txtName.Text = name;
            txtText.Text = text;
        }

        private void BtnAdd_Click(object sender, RoutedEventArgs e)
        {
            string text = txtText.Text;
            if (string.IsNullOrEmpty(text))
            {
                txtStatus.Text = "内容不能为空。";
                return;
            }
            string name = txtName.Text.Trim().Replace("\t", " ");   // 名称中不允许制表符（序列化分隔符）
            if (string.IsNullOrEmpty(name))
                name = Truncate(text);
            string item = name + "\t" + text;

            int index = lstCommands.SelectedIndex;
            if (index >= 0 && index < _items.Count)
                _items[index] = item;   // 更新选中项
            else
                _items.Add(item);

            SaveCommands();
            RefreshList();
            lstCommands.SelectedIndex = index >= 0 && index < _items.Count ? index : _items.Count - 1;
            txtStatus.Text = index >= 0 && index < _items.Count ? "已更新指令。" : "已添加指令。";
        }

        private void BtnDelete_Click(object sender, RoutedEventArgs e)
        {
            int index = lstCommands.SelectedIndex;
            if (index < 0 || index >= _items.Count) return;
            _items.RemoveAt(index);
            SaveCommands();
            RefreshList();
            txtStatus.Text = "已删除指令。";
        }

        private void BtnUp_Click(object sender, RoutedEventArgs e) => MoveSelected(-1);

        private void BtnDown_Click(object sender, RoutedEventArgs e) => MoveSelected(1);

        private void MoveSelected(int delta)
        {
            int index = lstCommands.SelectedIndex;
            int target = index + delta;
            if (index < 0 || target < 0 || target >= _items.Count) return;
            string item = _items[index];
            _items.RemoveAt(index);
            _items.Insert(target, item);
            SaveCommands();
            RefreshList();
            lstCommands.SelectedIndex = target;
        }

        // ============ 发送 ============

        private void BtnSend_Click(object sender, RoutedEventArgs e) => SendSelected();

        private void LstCommands_MouseDoubleClick(object sender, MouseButtonEventArgs e) => SendSelected();

        private void SendSelected()
        {
            int index = lstCommands.SelectedIndex;
            if (index < 0 || index >= _items.Count)
            {
                txtStatus.Text = "请先选择要发送的指令。";
                return;
            }
            string name, text;
            SplitItem(_items[index], out name, out text);
            if (_main.SendText(text))
                txtStatus.Text = $"已发送：{(string.IsNullOrEmpty(name) ? "指令" : name)}";
            else
                txtStatus.Text = "发送失败（请检查串口连接与内容格式）。";
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();
    }
}
