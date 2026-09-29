using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Text;

namespace SerialPort.UI
{
    /// <summary>
    /// 接收区行模型：一行 = 一条最终显示文本（已含时间戳与 [RX]/[TX] 前缀，不含换行符）。
    /// 文本可变：未完成行在数据续到时原位更新（集合抛 Replace 通知，虚拟化下只重建该行容器）。
    /// </summary>
    internal sealed class ReceiveLine
    {
        public string Text { get; internal set; }

        public ReceiveLine(string text)
        {
            Text = text ?? string.Empty;
        }
    }

    /// <summary>行内一段匹配区间（搜索高亮用）。</summary>
    internal struct MatchSegment
    {
        public readonly int Start;
        public readonly int Length;

        public MatchSegment(int start, int length)
        {
            Start = start;
            Length = length;
        }
    }

    /// <summary>查找结果（搜索窗口状态栏显示用）：Found = 是否找到；Current = 当前第几处；Total = 共几处。</summary>
    internal sealed class FindResult
    {
        public bool Found;
        public int Current;
        public int Total;
    }

    /// <summary>行内文本匹配工具（搜索与筛选共用）：行内子串查找 + 全词校验 + 匹配分段。</summary>
    internal static class TextMatch
    {
        /// <summary>在 text 中从 startIndex 起正向查找 term，返回匹配起始下标；未找到返回 -1。</summary>
        public static int FindMatchIndex(string text, string term, int startIndex, bool matchCase, bool wholeWord)
        {
            if (startIndex < 0 || startIndex >= text.Length) return -1;
            StringComparison cmp = matchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
            int idx = text.IndexOf(term, startIndex, cmp);
            while (idx >= 0)
            {
                if (!wholeWord || IsWholeWordAt(text, idx, term.Length)) return idx;
                idx = text.IndexOf(term, idx + 1, cmp);
            }
            return -1;
        }

        /// <summary>枚举行内全部匹配区间（按出现顺序）。</summary>
        public static List<MatchSegment> GetMatchSegments(string text, string term, bool matchCase, bool wholeWord)
        {
            var segments = new List<MatchSegment>();
            int idx = 0;
            while ((idx = FindMatchIndex(text, term, idx, matchCase, wholeWord)) >= 0)
            {
                segments.Add(new MatchSegment(idx, term.Length));
                idx += term.Length;
            }
            return segments;
        }

        /// <summary>判断 [index, index+length) 是否为全词匹配（两侧都不是单词字符，中文按一个词处理）。</summary>
        private static bool IsWholeWordAt(string text, int index, int length)
        {
            bool leftOk = index == 0 || !IsWordChar(text[index - 1]);
            bool rightOk = index + length >= text.Length || !IsWordChar(text[index + length]);
            return leftOk && rightOk;
        }

        /// <summary>单词字符：字母（含中文）、数字、下划线。</summary>
        private static bool IsWordChar(char c)
        {
            return char.IsLetterOrDigit(c) || c == '_';
        }
    }

    /// <summary>
    /// 跨块行组装器：接收数据不按行对齐（一行可能拆到多个数据块、多个换行风格混用），
    /// 维护未完成行缓冲，按 \r\n / \r / \n 分行；\r\n 算一个换行（含 \r 与 \n 拆在相邻块的情况）。
    /// 仅在 UI 线程使用（数据经 SerialPortService 攒批 + Post 编组后到达），无锁。
    /// </summary>
    internal sealed class ReceiveLineAssembler
    {
        private readonly StringBuilder _pending = new StringBuilder();
        private bool _mergeLf;   // 上一个换行是 \r：其后紧邻的 \n 与之合并为同一个换行，不再重复分行

        /// <summary>一行完成（行文本不含换行；空串 = 空行）。同一次 Append 可能触发多次。</summary>
        public event Action<string> LineCompleted;

        /// <summary>一块数据处理完毕：未完成行缓冲可能已变化（含被清空），调用方据此同步显示末条目。</summary>
        public event Action PendingUpdated;

        /// <summary>当前未完成行文本（不含换行；空串 = 无未完成内容）。</summary>
        public string PendingText
        {
            get { return _pending.ToString(); }
        }

        /// <summary>当前未完成行长度。</summary>
        public int PendingLength
        {
            get { return _pending.Length; }
        }

        /// <summary>追加一段解码后的接收文本，过程中逐行触发 <see cref="LineCompleted"/>，末尾触发一次 <see cref="PendingUpdated"/>。</summary>
        public void Append(string text)
        {
            if (string.IsNullOrEmpty(text)) return;

            int segStart = 0;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (_mergeLf)
                {
                    _mergeLf = false;
                    if (c == '\n')
                    {
                        segStart = i + 1;   // \r\n 中的 \n：并入上一个换行，不产生空行
                        continue;
                    }
                }
                if (c == '\r' || c == '\n')
                {
                    _pending.Append(text, segStart, i - segStart);
                    LineCompleted?.Invoke(_pending.ToString());
                    _pending.Clear();
                    segStart = i + 1;
                    _mergeLf = c == '\r';
                }
            }
            if (segStart < text.Length)
                _pending.Append(text, segStart, text.Length - segStart);

            PendingUpdated?.Invoke();
        }

        /// <summary>复位（清空接收区时调用）：丢弃未完成行缓冲。</summary>
        public void Reset()
        {
            _pending.Clear();
            _mergeLf = false;
        }

        /// <summary>
        /// 未完成行超限截头（无换行的持续数据流如二进制会使其无界增长），保留末尾 keep 个字符。
        /// 被截掉的头部不再进入显示与文件（与旧版筛选缓冲截头行为一致：该行本就不完整，丢弃可接受）。
        /// 截头后触发 <see cref="PendingUpdated"/>：调用方同步显示条目与待保存片段，避免残留超长行。
        /// </summary>
        public bool TrimPendingIfNeeded(int limit, int keep)
        {
            if (_pending.Length <= limit) return false;
            _pending.Remove(0, _pending.Length - keep);
            PendingUpdated?.Invoke();
            return true;
        }
    }

    /// <summary>
    /// 接收区批量行集合（虚拟化 ListBox 的 ItemsSource）：
    /// 支持整批 AddRange 与批量头部移除（FIFO 截头），每次操作只抛一个 CollectionChanged 事件，
    /// 避免逐行 Add 在高频接收时产生海量通知。全部操作仅在 UI 线程执行，无锁。
    /// TotalChars 维护行字符合计（含换行开销估算），供容量截头判断，避免全表扫描。
    /// </summary>
    internal sealed class ReceiveLineCollection : IList<ReceiveLine>, IList,
        INotifyCollectionChanged, INotifyPropertyChanged
    {
        private readonly List<ReceiveLine> _items = new List<ReceiveLine>();

        public event NotifyCollectionChangedEventHandler CollectionChanged;
        public event PropertyChangedEventHandler PropertyChanged;

        /// <summary>行字符合计（每行按"文本长度 + 2"估算换行开销，仅用于容量核算）。</summary>
        public int TotalChars { get; private set; }

        public int Count
        {
            get { return _items.Count; }
        }

        public bool IsReadOnly
        {
            get { return false; }
        }

        public ReceiveLine this[int index]
        {
            get { return _items[index]; }
            set
            {
                ReceiveLine old = _items[index];
                TotalChars += Weight(value.Text) - Weight(old.Text);
                _items[index] = value;
                OnPropertyChanged("Item[]");
                OnCollectionChanged(new NotifyCollectionChangedEventArgs(
                    NotifyCollectionChangedAction.Replace, value, old, index));
            }
        }

        /// <summary>追加一行（创建 ReceiveLine 并返回，便于后续原位更新未完成行）。</summary>
        public ReceiveLine AddLine(string text)
        {
            var line = new ReceiveLine(text);
            Add(line);
            return line;
        }

        public void Add(ReceiveLine item)
        {
            _items.Add(item);
            TotalChars += Weight(item.Text);
            OnPropertyChanged("Count");
            OnCollectionChanged(new NotifyCollectionChangedEventArgs(
                NotifyCollectionChangedAction.Add, item, _items.Count - 1));
        }

        /// <summary>整批追加：单次 Add 通知（WPF CollectionView 支持多元素 Add/Remove 事件）。</summary>
        public void AddRange(List<ReceiveLine> items)
        {
            if (items == null || items.Count == 0) return;
            int start = _items.Count;
            _items.AddRange(items);
            for (int i = 0; i < items.Count; i++)
                TotalChars += Weight(items[i].Text);
            OnPropertyChanged("Count");
            OnCollectionChanged(new NotifyCollectionChangedEventArgs(
                NotifyCollectionChangedAction.Add, items, start));
        }

        /// <summary>原位更新一行文本（未完成行续到数据时调用），抛 Replace 通知只刷新该行。</summary>
        public void SetText(ReceiveLine item, string text)
        {
            int index = _items.IndexOf(item);
            if (index < 0) return;
            TotalChars += Weight(text) - Weight(item.Text);
            item.Text = text;
            OnCollectionChanged(new NotifyCollectionChangedEventArgs(
                NotifyCollectionChangedAction.Replace, item, item, index));
        }

        /// <summary>从头部批量移除（FIFO 截头）：单次 Remove 通知，虚拟化下不触发全文重排。</summary>
        public void RemoveFromHead(int count)
        {
            if (count <= 0) return;
            if (count > _items.Count) count = _items.Count;
            List<ReceiveLine> removed = _items.GetRange(0, count);
            for (int i = 0; i < removed.Count; i++)
                TotalChars -= Weight(removed[i].Text);
            _items.RemoveRange(0, count);
            OnPropertyChanged("Count");
            OnCollectionChanged(new NotifyCollectionChangedEventArgs(
                NotifyCollectionChangedAction.Remove, removed, 0));
        }

        /// <summary>清空（Reset 通知）。</summary>
        public void ClearItems()
        {
            if (_items.Count == 0)
            {
                TotalChars = 0;
                return;
            }
            _items.Clear();
            TotalChars = 0;
            OnPropertyChanged("Count");
            OnCollectionChanged(new NotifyCollectionChangedEventArgs(
                NotifyCollectionChangedAction.Reset));
        }

        /// <summary>ICollection.Clear：与 ClearItems 相同（Reset 通知）。</summary>
        public void Clear()
        {
            ClearItems();
        }

        public int IndexOf(ReceiveLine item)
        {
            return _items.IndexOf(item);
        }

        public bool Contains(ReceiveLine item)
        {
            return _items.Contains(item);
        }

        public void CopyTo(ReceiveLine[] array, int arrayIndex)
        {
            _items.CopyTo(array, arrayIndex);
        }

        public void Insert(int index, ReceiveLine item)
        {
            _items.Insert(index, item);
            TotalChars += Weight(item.Text);
            OnPropertyChanged("Count");
            OnCollectionChanged(new NotifyCollectionChangedEventArgs(
                NotifyCollectionChangedAction.Add, item, index));
        }

        public void RemoveAt(int index)
        {
            ReceiveLine item = _items[index];
            TotalChars -= Weight(item.Text);
            _items.RemoveAt(index);
            OnPropertyChanged("Count");
            OnCollectionChanged(new NotifyCollectionChangedEventArgs(
                NotifyCollectionChangedAction.Remove, item, index));
        }

        public bool Remove(ReceiveLine item)
        {
            int index = _items.IndexOf(item);
            if (index < 0) return false;
            RemoveAt(index);
            return true;
        }

        public IEnumerator<ReceiveLine> GetEnumerator()
        {
            return _items.GetEnumerator();
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return _items.GetEnumerator();
        }

        private static int Weight(string text)
        {
            return text.Length + 2;   // 计入换行开销（估算，仅用于容量核算）
        }

        private void OnCollectionChanged(NotifyCollectionChangedEventArgs e)
        {
            CollectionChanged?.Invoke(this, e);
        }

        private void OnPropertyChanged(string name)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }

        // ---- 非泛型 IList 显式实现（ListBox 经 IList 访问项）----

        object IList.this[int index]
        {
            get { return _items[index]; }
            set { this[index] = (ReceiveLine)value; }
        }

        bool IList.IsFixedSize
        {
            get { return false; }
        }

        bool IList.IsReadOnly
        {
            get { return false; }
        }

        int IList.Add(object value)
        {
            Add((ReceiveLine)value);
            return _items.Count - 1;
        }

        bool IList.Contains(object value)
        {
            return Contains(value as ReceiveLine);
        }

        int IList.IndexOf(object value)
        {
            return IndexOf(value as ReceiveLine);
        }

        void IList.Insert(int index, object value)
        {
            Insert(index, (ReceiveLine)value);
        }

        void IList.Remove(object value)
        {
            Remove(value as ReceiveLine);
        }

        void IList.Clear()
        {
            ClearItems();
        }

        void ICollection.CopyTo(Array array, int index)
        {
            ((ICollection)_items).CopyTo(array, index);
        }

        bool ICollection.IsSynchronized
        {
            get { return false; }
        }

        object ICollection.SyncRoot
        {
            get { return ((ICollection)_items).SyncRoot; }
        }
    }
}
