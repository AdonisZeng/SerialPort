using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

namespace SerialPort.Services
{
    /// <summary>
    /// 日志服务：把程序运行过程中的关键事件与异常写入文本日志文件，便于事后排查问题。
    ///
    /// 存放位置：exe 同级的 logs 目录（AppDomain.BaseDirectory\logs）；
    /// 文件命名：启动时刻的"年月日时分秒"（yyyy-MM-dd_HH-mm-ss.txt），每次启动新建一个文件；
    /// 保留策略：每次启动时自动删除 7 天（一个星期）以前的日志文件。
    ///
    /// 设计要点：
    /// - 写入在独立后台线程上完成：日志绝不能阻塞 UI（串口收发是高频路径）；
    /// - 日志自身的任何异常都被吞掉：日志出问题不能影响主程序运行；
    /// - 异常一律记录类型、消息与堆栈：状态栏只有一句中文提示，定位问题必须依赖日志。
    /// </summary>
    public static class LogService
    {
        /// <summary>日志级别。</summary>
        public enum Level
        {
            /// <summary>调试细节（极少使用，避免日志噪声）。</summary>
            Debug,
            /// <summary>正常业务事件（连接、断开、更新检查等）。</summary>
            Info,
            /// <summary>可恢复的异常情况（设备错误、写盘失败等）。</summary>
            Warn,
            /// <summary>错误与未处理异常。</summary>
            Error
        }

        /// <summary>日志保留天数：超过则启动时删除（需求：一个星期）。</summary>
        private const int RetentionDays = 7;

        /// <summary>日志文件名的时间戳格式（"年月日时分秒"）。</summary>
        private const string FileNameFormat = "yyyy-MM-dd_HH-mm-ss";

        /// <summary>日志目录名（位于 exe 同级）。</summary>
        private const string LogDirName = "logs";

        private static readonly object _lock = new object();
        private static readonly object _fileLock = new object();   // 保护日志文件写入器（后台写线程与 Fatal 直写互斥）
        private static readonly Queue<string> _queue = new Queue<string>();   // 待写日志队列（后台线程独占消费）
        private static readonly AutoResetEvent _signal = new AutoResetEvent(false);

        private static StreamWriter _file;        // 当前会话的日志文件写入器（仅写线程访问）
        private static Thread _writer;            // 后台写线程
        private static bool _initialized;
        private static bool _stopping;
        private static string _logFilePath;

        /// <summary>当前会话的日志文件完整路径（未初始化或初始化失败时为空）。</summary>
        public static string LogFilePath
        {
            get { lock (_lock) return _logFilePath; }
        }

        /// <summary>
        /// 初始化日志：建目录 → 清理过期日志 → 打开本次会话的日志文件 → 启动后台写线程。
        /// 必须最先调用（App.OnStartup 开头），否则启动阶段发生的异常无处可记。
        /// 本方法不抛异常：日志初始化失败只会让日志功能静默失效，程序照常运行。
        /// </summary>
        public static void Init()
        {
            lock (_lock)
            {
                if (_initialized) return;
                _initialized = true;
            }

            try
            {
                string dir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, LogDirName);
                try
                {
                    Directory.CreateDirectory(dir);
                }
                catch
                {
                    // exe 所在目录不可写（如装到 Program Files）：退回用户目录，
                    // 不能因日志目录建不出来就导致程序启动失败
                    dir = Path.Combine(
                        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SerialPort"),
                        LogDirName);
                    Directory.CreateDirectory(dir);
                }

                // 清理过期日志：每次启动删掉 7 天以前的文件
                CleanupOldLogs(dir);

                string path = Path.Combine(dir, DateTime.Now.ToString(FileNameFormat, CultureInfo.InvariantCulture) + ".txt");
                // UTF-8 带 BOM：Windows 记事本直接双击打开不乱码
                lock (_fileLock)
                {
                    _file = new StreamWriter(path, true, new UTF8Encoding(true));
                    _file.AutoFlush = true;   // 每条即刷盘：程序崩溃时也能留下现场
                }
                lock (_lock) { _logFilePath = path; }
            }
            catch
            {
                // 日志文件无法创建：保持静默（不弹窗、不抛异常），后续写日志调用自动失效
                _file = null;
            }

            _writer = new Thread(WriteLoop) { IsBackground = true, Name = "LogWriter" };
            _writer.Start();
        }

        /// <summary>记录调试级日志。</summary>
        public static void Debug(string message) => Enqueue(Level.Debug, message, null);

        /// <summary>记录普通事件日志。</summary>
        public static void Info(string message) => Enqueue(Level.Info, message, null);

        /// <summary>记录警告日志（可恢复的异常情况）。</summary>
        public static void Warn(string message) => Enqueue(Level.Warn, message, null);

        /// <summary>记录错误日志（不带异常对象）。</summary>
        public static void Error(string message) => Enqueue(Level.Error, message, null);

        /// <summary>记录错误日志并附带异常的类型、消息与堆栈。</summary>
        public static void Error(string message, Exception ex) => Enqueue(Level.Error, message, ex);

        /// <summary>记录警告日志并附带异常的类型、消息与堆栈。</summary>
        public static void Warn(string message, Exception ex) => Enqueue(Level.Warn, message, ex);

        /// <summary>
        /// 记录致命错误（进程即将终止时的未处理异常）。
        /// 走同步直写而非常规队列：进程随时会被杀掉，排队中的日志可能永远写不出去。
        /// </summary>
        public static void Fatal(string message, Exception ex)
        {
            try
            {
                string text = Format(Level.Error, message, ex);
                // 与后台写线程互斥：写盘期间不重复打开文件，避免两个线程交叉写
                lock (_fileLock)
                {
                    if (_file == null) TryReopenFile();
                    _file?.Write(text);
                    _file?.Flush();
                }
            }
            catch
            {
                // 致命日志写不出去也不能影响退出流程
            }
        }

        /// <summary>
        /// 关闭日志：排空队列后关闭文件。程序退出时调用，避免丢失最后几条日志。
        /// </summary>
        public static void Shutdown()
        {
            Thread writer;
            lock (_lock)
            {
                if (!_initialized) return;
                _stopping = true;
                writer = _writer;
            }
            _signal.Set();

            // 等待写线程把队列写完（最多 2 秒），正常情况下瞬间完成
            if (writer != null && writer.IsAlive)
            {
                try { writer.Join(2000); } catch { }
            }

            try { _file?.Dispose(); } catch { }
            lock (_fileLock) { _file = null; }
            lock (_lock) { _logFilePath = null; }
        }

        /// <summary>格式化一条日志并入队（入队即返回，绝不阻塞调用方）。</summary>
        private static void Enqueue(Level level, string message, Exception ex)
        {
            try
            {
                lock (_lock)
                {
                    if (!_initialized || _stopping) return;
                    _queue.Enqueue(Format(level, message, ex));
                }
                _signal.Set();
            }
            catch
            {
                // 格式化 / 入队异常一律忽略：日志不能成为故障源
            }
        }

        /// <summary>组织单条日志的文本（异常展开为多行，便于阅读）。</summary>
        private static string Format(Level level, string message, Exception ex)
        {
            var sb = new StringBuilder();
            sb.Append('[').Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)).Append("] ");
            sb.Append('[').Append(level.ToString().ToUpperInvariant()).Append("] ");
            sb.Append('[').Append(CurrentThreadTag()).Append("] ");
            sb.AppendLine(message ?? string.Empty);

            if (ex != null)
            {
                sb.AppendLine("    异常类型：" + ex.GetType().FullName);
                sb.AppendLine("    异常信息：" + ex.Message);
                if (ex.InnerException != null)
                    sb.AppendLine("    内部异常：" + ex.InnerException.GetType().FullName + " — " + ex.InnerException.Message);
                sb.AppendLine("    堆栈：" + (ex.StackTrace ?? "(无堆栈)"));
            }
            return sb.ToString();
        }

        /// <summary>当前线程标识：后台线程用线程名，主线程记为"主线程"。</summary>
        private static string CurrentThreadTag()
        {
            string name = Thread.CurrentThread.Name;
            return string.IsNullOrEmpty(name) ? "主线程" : name;
        }

        /// <summary>后台写线程：信号唤醒 → 批量取队列 → 逐条写盘。</summary>
        private static void WriteLoop()
        {
            while (true)
            {
                _signal.WaitOne();

                List<string> batch;
                lock (_lock)
                {
                    batch = new List<string>(_queue.Count);
                    while (_queue.Count > 0) batch.Add(_queue.Dequeue());
                    if (_stopping && batch.Count == 0) break;   // 已停止且排空：退出线程
                }

                foreach (string line in batch) WriteToFile(line);
            }
        }

        /// <summary>写一条到文件；写失败（磁盘满 / 文件被占用）时尝试重开文件，绝不抛出。</summary>
        private static void WriteToFile(string text)
        {
            try
            {
                lock (_fileLock)
                {
                    if (_file == null) return;
                    _file.Write(text);
                }
            }
            catch
            {
                TryReopenFile();
            }
        }

        /// <summary>尝试重新打开日志文件（写失败后的恢复手段）。</summary>
        private static void TryReopenFile()
        {
            try
            {
                lock (_fileLock)
                {
                    try { _file?.Dispose(); } catch { }
                    _file = null;

                    string path;
                    lock (_lock) path = _logFilePath;
                    if (string.IsNullOrEmpty(path)) return;

                    _file = new StreamWriter(path, true, new UTF8Encoding(true));
                    _file.AutoFlush = true;
                }
            }
            catch
            {
                _file = null;   // 仍失败则放弃写盘（后续日志静默丢弃）
            }
        }

        /// <summary>
        /// 清理过期日志：删除 7 天以前的日志文件。
        /// 只处理文件名符合本程序格式的文件（yyyy-MM-dd_HH-mm-ss.txt），避免误删目录里的其他文件。
        /// </summary>
        private static void CleanupOldLogs(string dir)
        {
            try
            {
                DateTime threshold = DateTime.Now.AddDays(-RetentionDays);
                foreach (string file in Directory.GetFiles(dir, "*.txt"))
                {
                    DateTime created;
                    if (!TryParseLogFileName(Path.GetFileName(file), out created)) continue;   // 非本程序格式：不动
                    if (created >= threshold) continue;
                    try { File.Delete(file); } catch { }   // 删除失败忽略：文件可能仍被占用
                }
            }
            catch
            {
                // 清理失败不影响启动
            }
        }

        /// <summary>解析日志文件名中的时间戳；不是本程序的文件名则返回 false。</summary>
        private static bool TryParseLogFileName(string fileName, out DateTime time)
        {
            time = default(DateTime);
            if (string.IsNullOrEmpty(fileName)) return false;
            if (!fileName.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)) return false;

            string stamp = fileName.Substring(0, fileName.Length - 4);
            return DateTime.TryParseExact(stamp, FileNameFormat, CultureInfo.InvariantCulture,
                DateTimeStyles.None, out time);
        }
    }
}
