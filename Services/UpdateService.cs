using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SerialPort.Services
{
    /// <summary>
    /// 更新服务：通过 GitHub Releases API 检查新版本，下载新程序并替换自身完成升级。
    /// 线程编组模式与 <see cref="SerialPortService"/> 一致：网络 / 文件操作在后台线程执行，
    /// 所有事件通过构造函数捕获的 SynchronizationContext 编组回 UI 线程回调。
    /// </summary>
    public sealed class UpdateService
    {
        // ===== 发布前检查：以下两个常量必须与你的 GitHub 仓库匹配 =====
        /// <summary>GitHub 用户名（仓库所有者）。</summary>
        public const string GitHubOwner = "AdonisZeng";
        /// <summary>GitHub 仓库名。</summary>
        public const string GitHubRepo = "SerialPort";

        private const string LatestReleaseUrl = "https://api.github.com/repos/{0}/{1}/releases/latest";
        /// <summary>网页端 latest release 地址：302 跳转到具体 tag，不受 API 配额限制（版本检查首选）。</summary>
        private const string ReleaseLatestWebUrl = "https://github.com/{0}/{1}/releases/latest";

        /// <summary>检查结果缓存有效期：此时间内直接使用本地缓存，不再发网络请求（自动检查用）。</summary>
        private static readonly TimeSpan CacheMaxAge = TimeSpan.FromHours(12);

        /// <summary>版本检查请求超时（默认 100 秒过长：网络不通时应尽快给出提示）。</summary>
        private static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(15);

        /// <summary>下载请求超时：单个附件的整体时限（程序本体可能数 MB，慢网下需放宽）。</summary>
        private static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(10);

        private readonly SynchronizationContext _syncContext;
        // 检查中标志（0 = 空闲，1 = 检查中）：UI 线程置位、后台线程复位，用 Interlocked 保证跨线程可见性
        private int _checking;
        // 上次上报的下载进度百分比（同一百分比不重复上报，降低大文件下载时的 UI 事件频率）
        private int _lastProgressPercent = -1;

        /// <summary>版本检查完成时触发（已在 UI 线程回调），携带检查结果。</summary>
        public event EventHandler<UpdateCheckResult> CheckCompleted;

        /// <summary>下载进度变化时触发（已在 UI 线程回调），Percent 为 0-100。</summary>
        public event EventHandler<int> UpdateProgress;

        /// <summary>检查 / 下载 / 替换任一环节出错时触发（已在 UI 线程回调）。</summary>
        public event EventHandler<string> UpdateError;

        /// <summary>新版本已替换完成、即将退出时触发（已在 UI 线程回调），订阅方负责退出应用。</summary>
        public event EventHandler UpdateApplied;

        public UpdateService()
        {
            // 捕获当前同步上下文，把后台线程事件切回 UI 线程（服务须在 UI 线程上构造）
            _syncContext = SynchronizationContext.Current ?? new SynchronizationContext();
        }

        /// <summary>当前程序版本（取自程序集版本号）。</summary>
        public static Version CurrentVersion => Assembly.GetExecutingAssembly().GetName().Version;

        /// <summary>
        /// 后台检查最新版本。notifyWhenUpToDate = true 时即使已是最新也触发 CheckCompleted（用于手动检查）；
        /// forceRefresh = true 时绕过本地缓存强制联网检查（手动点击"检查更新"时用）。
        /// </summary>
        public void CheckForUpdatesAsync(bool notifyWhenUpToDate = false, bool forceRefresh = false)
        {
            if (Interlocked.CompareExchange(ref _checking, 1, 0) != 0)
            {
                // 已在检查中：自动检查静默忽略，手动检查给出提示（避免状态栏停在"正在检查…"永不结束）
                if (forceRefresh)
                    Post(() => UpdateError?.Invoke(this, "正在检查更新，请稍后再试。"));
                return;
            }

            Task.Run(() =>
            {
                try
                {
                    UpdateCheckResult result = null;
                    if (!forceRefresh)
                        result = TryReadCache();   // 缓存未过期直接使用，不发网络请求
                    if (result == null)
                    {
                        result = FetchLatestRelease();
                        WriteCache(result);        // 缓存最新结果（含"已是最新"状态），写失败静默忽略
                    }
                    if (!notifyWhenUpToDate && !result.IsUpdateAvailable && result.LatestVersion == null)
                        return;   // 自动检查且仓库无 Release：静默
                    Post(() => CheckCompleted?.Invoke(this, result));
                }
                catch (RateLimitExceededException ex)
                {
                    Post(() => UpdateError?.Invoke(this, "GitHub 访问频率受限（rate limit exceeded），" + ex.Message + "。"));
                }
                catch (HttpRequestException)
                {
                    Post(() => UpdateError?.Invoke(this, "检查更新失败：无法连接到 GitHub，请检查网络后重试。"));
                }
                catch (Exception raw)
                {
                    // 版本检查走同步 .Result：HttpClient 超时（CheckTimeout）抛出的 TaskCanceledException
                    // 会被包成 AggregateException，直接取 raw.Message 只会显示"发生一个或多个错误。"
                    // 只拆"恰好一个内层"的聚合体，避免把真正的多异常压平后丢失信息
                    Exception ex = raw is AggregateException agg && agg.InnerExceptions.Count == 1
                        ? agg.InnerExceptions[0]
                        : raw;
                    string message = ex is TaskCanceledException
                        ? "检查更新超时（" + (int)CheckTimeout.TotalSeconds + " 秒），请检查网络后重试。"
                        : "检查更新失败：" + ex.Message;
                    Post(() => UpdateError?.Invoke(this, message));
                }
                finally
                {
                    Interlocked.Exchange(ref _checking, 0);
                }
            });
        }

        /// <summary>
        /// 下载并应用更新（下载 → SHA256 校验 → 替换 → 启动新版本 → 触发 UpdateApplied 退出）。
        /// 任何失败都会回滚并抛出，通过 UpdateError 报告，不影响当前程序运行。
        /// token 由调用方（UpdateDialog）提供：更新窗口关闭即取消，替换程序前做最后一次确认。
        /// </summary>
        public void DownloadAndApplyAsync(UpdateCheckResult result, CancellationToken token = default)
        {
            Task.Run(async () =>
            {
                string newExe = null;   // 下载目标路径：取消时清理残留
                try
                {
                    _lastProgressPercent = -1;   // 重试时保证 0% 会重新上报
                    string workDir = Path.Combine(Path.GetTempPath(), "SerialPortUpdate");
                    Directory.CreateDirectory(workDir);

                    string exePath = Assembly.GetExecutingAssembly().Location;
                    string exeName = Path.GetFileName(exePath);
                    newExe = Path.Combine(workDir, exeName);

                    // 1. 下载新程序与 SHA256 校验文件（带进度报告，支持取消）
                    //    多个文件共用一条进度条：按大小分配区间（程序 0-90%，校验文件 90-95%，配置文件 95-100%）。
                    //    无 config 附件时校验文件独占 90-100%，否则进度条会永远停在 95%。
                    Post(() => UpdateProgress?.Invoke(this, 0));
                    await DownloadFileAsync(result.ExeUrl, newExe, token, 0, 90).ConfigureAwait(false);
                    if (string.IsNullOrEmpty(result.Sha256Url))
                        throw new IOException("Release 附件中缺少 SHA256 校验文件，为安全起见已取消更新。");
                    int hashSpan = string.IsNullOrEmpty(result.ConfigUrl) ? 10 : 5;
                    string hashFile = Path.Combine(workDir, exeName + ".sha256");
                    await DownloadFileAsync(result.Sha256Url, hashFile, token, 90, hashSpan).ConfigureAwait(false);
                    VerifySha256(newExe, hashFile);

                    // 2. 尽力下载配置文件（发布包含 config 时）；失败不阻断升级。
                    //    必须在替换 exe 之前完成：替换之后不再有任何可取消的等待点，保证“取消即不替换”
                    string newConfig = null;
                    if (!string.IsNullOrEmpty(result.ConfigUrl))
                    {
                        try
                        {
                            newConfig = Path.Combine(workDir, exeName + ".config");
                            await DownloadFileAsync(result.ConfigUrl, newConfig, token, 90 + hashSpan, 100 - 90 - hashSpan).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException)
                        {
                            throw;   // 取消不能被吞掉：否则会继续替换并强制退出
                        }
                        catch
                        {
                            newConfig = null;   // 配置文件下载失败可忽略（不影响程序本体运行）
                        }
                    }

                    // 3. 替换运行中的 exe（Windows 允许重命名运行中的映像，故可先改名再移入新文件）。
                    //    替换前最后一次确认取消状态：窗口已关闭则中止，绝不静默替换程序并退出应用。
                    token.ThrowIfCancellationRequested();
                    string oldExe = exePath + ".old";
                    try
                    {
                        if (File.Exists(oldExe)) File.Delete(oldExe);   // 清理上次更新的残留
                        File.Move(exePath, oldExe);
                        try
                        {
                            File.Move(newExe, exePath);
                        }
                        catch
                        {
                            File.Move(oldExe, exePath);   // 回滚：恢复原程序
                            throw;
                        }
                    }
                    catch (Exception ex)
                    {
                        throw new IOException("替换程序文件失败：" + ex.Message, ex);
                    }

                    // 4. 替换配置文件（同步快速，失败忽略）；此后仅剩启动新版本，无等待点
                    if (newConfig != null)
                    {
                        try { File.Copy(newConfig, exePath + ".config", true); } catch { }
                    }

                    // 5. 启动新版本，退出当前进程。
                    //    此刻程序文件已被替换，启动失败不能笼统报"更新失败"——用户只会以为没更新成功，
                    //    实际磁盘上已是新版本，必须明确告知手动启动。
                    try
                    {
                        Process.Start(exePath);
                    }
                    catch (Exception ex)
                    {
                        throw new IOException("新版本已安装，但启动失败，请手动启动程序：" + ex.Message, ex);
                    }
                    Post(() => UpdateApplied?.Invoke(this, EventArgs.Empty));
                }
                catch (OperationCanceledException)
                {
                    // 用户取消（更新窗口已关闭）：清理整个临时目录，仅提示不报错。
                    // 注意：HttpClient 超时抛出的 TaskCanceledException 也派生自 OperationCanceledException，
                    // 以 token 状态区分"用户取消"与"下载超时"，避免超时被误报成用户取消。
                    TryDeleteWorkDir(newExe);
                    string message = token.IsCancellationRequested
                        ? "更新已取消。"
                        : "更新下载超时，请检查网络后重试。";
                    Post(() => UpdateError?.Invoke(this, message));
                }
                catch (Exception ex)
                {
                    // 失败同样清理临时目录，不留半下载的残留文件
                    TryDeleteWorkDir(newExe);
                    Post(() => UpdateError?.Invoke(this, "更新失败：" + ex.Message));
                }
            });
        }

        /// <summary>尽力删除更新工作目录（含下载残留）；目录不存在 / 被占用时静默忽略。</summary>
        private static void TryDeleteWorkDir(string newExe)
        {
            if (string.IsNullOrEmpty(newExe)) return;
            try { Directory.Delete(Path.GetDirectoryName(newExe), true); } catch { }
        }

        /// <summary>
        /// 查询 GitHub 最新 Release 并解析为检查结果。
        /// 版本检查走网页端 302 重定向（不消耗 API 配额），确认有新版本后才调 API 补全发布说明与附件地址。
        /// </summary>
        private static UpdateCheckResult FetchLatestRelease()
        {
            var result = new UpdateCheckResult { CurrentVersion = CurrentVersion };

            // 1. 网页端 /releases/latest → 302 跳转到 /releases/tag/vX.Y.Z，解析出最新版本
            Version latest = FetchLatestVersionFromWeb();
            if (latest == null)
                return result;   // 仓库还没有任何 Release：视为“无可用更新”
            result.LatestVersion = latest;
            result.IsUpdateAvailable = latest.CompareTo(result.CurrentVersion) > 0;
            if (!result.IsUpdateAvailable)
                return result;

            // 2. 有新版本才调 API 补全发布说明与附件（频率极低，偶发限流只影响真正的升级流程）
            FillReleaseDetails(result);
            return result;
        }

        /// <summary>
        /// 通过网页端 /releases/latest 的 302 跳转解析最新 tag 版本。
        /// 不访问 api.github.com，不受 API 每 IP 每小时 60 次的未认证配额限制。
        /// </summary>
        private static Version FetchLatestVersionFromWeb()
        {
            var handler = new HttpClientHandler { AllowAutoRedirect = false };   // 不跟随跳转，直接读 Location 头
            using (var client = new HttpClient(handler))
            {
                client.Timeout = CheckTimeout;   // 网络不通时尽快失败，不在后台挂 100 秒
                client.DefaultRequestHeaders.Add("User-Agent", "SerialPort-Update-Checker");   // GitHub 要求 UA
                using (HttpResponseMessage resp = client.GetAsync(string.Format(ReleaseLatestWebUrl, GitHubOwner, GitHubRepo)).Result)
                {
                    if (resp.StatusCode == HttpStatusCode.NotFound)
                        return null;   // 仓库还没有任何 Release
                    if (resp.StatusCode == HttpStatusCode.Forbidden)
                        throw new RateLimitExceededException();   // 网页端被反爬限流
                    if (resp.StatusCode != HttpStatusCode.Found && resp.StatusCode != HttpStatusCode.MovedPermanently)
                    {
                        resp.EnsureSuccessStatusCode();
                        return null;   // 200 且无跳转（理论上不会出现）：视为无更新
                    }

                    // Location 形如 https://github.com/owner/repo/releases/tag/v1.0.0
                    string location = resp.Headers.Location?.ToString() ?? "";
                    const string marker = "/releases/tag/";
                    int idx = location.LastIndexOf(marker, StringComparison.OrdinalIgnoreCase);
                    if (idx < 0) return null;
                    return ParseTagVersion(Uri.UnescapeDataString(location.Substring(idx + marker.Length)));
                }
            }
        }

        /// <summary>通过 GitHub API 补全发布说明与附件地址（仅在有新版本时调用）。</summary>
        private static void FillReleaseDetails(UpdateCheckResult result)
        {
            using (var client = new HttpClient())
            {
                client.Timeout = CheckTimeout;   // 同版本检查：网络不通时尽快失败
                client.DefaultRequestHeaders.Add("User-Agent", "SerialPort-Update-Checker");   // GitHub API 要求 UA
                using (HttpResponseMessage resp = client.GetAsync(string.Format(LatestReleaseUrl, GitHubOwner, GitHubRepo)).Result)
                {
                    // API 未认证配额耗尽：给出恢复时间提示，不再抛原始英文错误
                    if (resp.StatusCode == HttpStatusCode.Forbidden)
                    {
                        string reset = resp.Headers.TryGetValues("X-RateLimit-Reset", out var values)
                            ? "约 " + DateTimeOffset.FromUnixTimeSeconds(long.Parse(values.First())).ToLocalTime().ToString("HH:mm") + " 后恢复"
                            : "请稍后再试";
                        throw new RateLimitExceededException(reset);
                    }
                    resp.EnsureSuccessStatusCode();

                    using (Stream stream = resp.Content.ReadAsStreamAsync().Result)
                    {
                        var serializer = new DataContractJsonSerializer(typeof(ReleaseInfo));
                        var release = (ReleaseInfo)serializer.ReadObject(stream);

                        result.ReleaseNotes = release.Body;

                        // 从附件中按文件名定位程序 / 校验文件 / 配置文件
                        string exeName = Path.GetFileName(Assembly.GetExecutingAssembly().Location);
                        if (release.Assets == null) return;
                        foreach (ReleaseAsset asset in release.Assets)
                        {
                            if (string.Equals(asset.Name, exeName, StringComparison.OrdinalIgnoreCase))
                                result.ExeUrl = asset.DownloadUrl;
                            else if (string.Equals(asset.Name, exeName + ".sha256", StringComparison.OrdinalIgnoreCase))
                                result.Sha256Url = asset.DownloadUrl;
                            else if (string.Equals(asset.Name, exeName + ".config", StringComparison.OrdinalIgnoreCase))
                                result.ConfigUrl = asset.DownloadUrl;
                        }
                        // 校验文件或程序附件缺失时给出明确提示
                        if (string.IsNullOrEmpty(result.ExeUrl))
                            throw new IOException("Release 附件中找不到 " + exeName + "，发布不完整。");
                        if (string.IsNullOrEmpty(result.Sha256Url))
                            throw new IOException("Release 附件中找不到 " + exeName + ".sha256 校验文件，发布不完整。");
                    }
                }
            }
        }

        /// <summary>读取本地缓存；缓存缺失、过期或损坏时返回 null（调用方正常走网络检查）。</summary>
        private static UpdateCheckResult TryReadCache()
        {
            try
            {
                if (!File.Exists(CacheFilePath)) return null;
                using (var fs = new FileStream(CacheFilePath, FileMode.Open, FileAccess.Read))
                {
                    var serializer = new DataContractJsonSerializer(typeof(UpdateCheckCache));
                    var cache = (UpdateCheckCache)serializer.ReadObject(fs);
                    if (cache == null || DateTime.UtcNow - cache.CheckedAt > CacheMaxAge)
                        return null;   // 已过期：需要重新检查
                    Version latest = cache.LatestVersion == null ? null : Version.Parse(cache.LatestVersion);
                    return new UpdateCheckResult
                    {
                        CurrentVersion = CurrentVersion,
                        LatestVersion = latest,
                        ReleaseNotes = cache.ReleaseNotes,
                        ExeUrl = cache.ExeUrl,
                        Sha256Url = cache.Sha256Url,
                        ConfigUrl = cache.ConfigUrl,
                        IsUpdateAvailable = latest != null && latest.CompareTo(CurrentVersion) > 0,
                    };
                }
            }
            catch
            {
                return null;   // 文件损坏 / 版本解析失败：忽略缓存，走网络检查
            }
        }

        /// <summary>把本次检查结果写入本地缓存；写失败（如目录不可写）静默忽略，不影响检查流程。</summary>
        private static void WriteCache(UpdateCheckResult result)
        {
            try
            {
                var cache = new UpdateCheckCache
                {
                    CheckedAt = DateTime.UtcNow,
                    LatestVersion = result.LatestVersion?.ToString(),
                    ReleaseNotes = result.ReleaseNotes,
                    ExeUrl = result.ExeUrl,
                    Sha256Url = result.Sha256Url,
                    ConfigUrl = result.ConfigUrl,
                };
                Directory.CreateDirectory(Path.GetDirectoryName(CacheFilePath));
                using (var fs = new FileStream(CacheFilePath, FileMode.Create, FileAccess.Write))
                {
                    var serializer = new DataContractJsonSerializer(typeof(UpdateCheckCache));
                    serializer.WriteObject(fs, cache);
                }
            }
            catch
            {
                // 缓存写失败不影响更新检查本身
            }
        }

        /// <summary>检查结果缓存文件的完整路径（%LocalAppData%\SerialPort\update_check.cache）。</summary>
        private static string CacheFilePath
        {
            get
            {
                string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SerialPort");
                return Path.Combine(dir, "update_check.cache");
            }
        }

        /// <summary>
        /// 下载文件到目标路径，期间按已读字节数报告进度。
        /// basePercent / spanPercent：本文件在整条进度条上占据的区间（见 DownloadAndApplyAsync 的分配），
        /// 使连续下载多个文件时进度单调上升，不会每换一个文件就从 0 重新计。
        /// </summary>
        private async Task DownloadFileAsync(string url, string destPath, CancellationToken token,
            int basePercent, int spanPercent)
        {
            using (var client = new HttpClient())
            {
                client.Timeout = DownloadTimeout;   // 下载整体时限（默认 100 秒对大文件偏短，放宽）
                client.DefaultRequestHeaders.Add("User-Agent", "SerialPort-Update-Checker");
                // 传 token：响应头阶段也能响应取消（否则关窗后仍会卡在等待响应）
                using (HttpResponseMessage resp = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false))
                {
                    resp.EnsureSuccessStatusCode();
                    long total = resp.Content.Headers.ContentLength ?? 0;
                    using (Stream input = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false))
                    using (var output = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true))
                    {
                        var buffer = new byte[81920];
                        long done = 0;
                        int read;
                        while ((read = await input.ReadAsync(buffer, 0, buffer.Length, token).ConfigureAwait(false)) > 0)
                        {
                            await output.WriteAsync(buffer, 0, read, token).ConfigureAwait(false);
                            done += read;
                            ReportProgress(done, total, basePercent, spanPercent);
                        }
                    }
                }
            }
        }

        /// <summary>校验下载文件的 SHA256 与校验文件一致，不一致时中止（防止下载损坏或被篡改）。</summary>
        private static void VerifySha256(string filePath, string hashFilePath)
        {
            string expected = ReadSha256(hashFilePath);
            string actual;
            using (var fs = File.OpenRead(filePath))
            using (SHA256 sha = SHA256.Create())
                actual = BitConverter.ToString(sha.ComputeHash(fs)).Replace("-", "").ToLowerInvariant();

            if (!string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("下载文件 SHA256 校验失败，已中止更新。");
        }

        /// <summary>读取校验文件的第一段（64 位十六进制，可能后随文件名）。</summary>
        private static string ReadSha256(string hashFilePath)
        {
            string content;
            // 带 BOM 探测：UTF-8 BOM 不会自动被 Trim 掉，残留会导致哈希比对恒失败、更新无法进行
            using (var reader = new StreamReader(hashFilePath, Encoding.UTF8, true))
                content = reader.ReadToEnd();
            content = content.Trim().Trim(BomChar);
            string[] parts = content.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) throw new InvalidDataException("SHA256 校验文件为空。");
            return parts[0].ToLowerInvariant();
        }

        /// <summary>字节序标记字符（U+FEFF）。</summary>
        private const char BomChar = '\uFEFF';

        /// <summary>把 "v1.0.1" 形式的 tag 解析为版本号；解析失败返回 null。</summary>
        private static Version ParseTagVersion(string tag)
        {
            if (string.IsNullOrEmpty(tag)) return null;
            string s = tag.TrimStart('v', 'V');
            Version v;
            return Version.TryParse(s, out v) ? v : null;
        }

        /// <summary>
        /// 进度事件编组回 UI 线程（同一百分比只上报一次，避免大文件下载时 UI 事件风暴）。
        /// 本文件进度映射到 [basePercent, basePercent + spanPercent] 区间，保证多文件下载时进度单调上升。
        /// </summary>
        private void ReportProgress(long done, long total, int basePercent, int spanPercent)
        {
            int percent = total > 0 ? basePercent + (int)(done * spanPercent / total) : basePercent;
            if (percent > 100) percent = 100;   // 防御：ContentLength 与实际大小不符时钳位
            if (percent == _lastProgressPercent) return;
            _lastProgressPercent = percent;
            Post(() => UpdateProgress?.Invoke(this, percent));
        }

        private void Post(Action action) => _syncContext.Post(_ => action(), null);
    }

    /// <summary>版本检查结果。</summary>
    public sealed class UpdateCheckResult
    {
        /// <summary>当前程序版本。</summary>
        public Version CurrentVersion { get; set; }

        /// <summary>GitHub 最新 Release 的版本号；仓库无 Release 或解析失败时为 null。</summary>
        public Version LatestVersion { get; set; }

        /// <summary>存在比当前更新的版本。</summary>
        public bool IsUpdateAvailable { get; set; }

        /// <summary>Release 更新说明（正文）。</summary>
        public string ReleaseNotes { get; set; }

        /// <summary>程序附件下载地址（可为 null，说明发布不完整）。</summary>
        public string ExeUrl { get; set; }

        /// <summary>SHA256 校验文件下载地址（可选，缺失时跳过校验）。</summary>
        public string Sha256Url { get; set; }

        /// <summary>配置文件附件下载地址（可选）。</summary>
        public string ConfigUrl { get; set; }
    }

    /// <summary>GitHub Releases API 响应模型（仅取需要的字段）。</summary>
    [DataContract]
    internal sealed class ReleaseInfo
    {
        [DataMember(Name = "tag_name")]
        public string TagName { get; set; }

        [DataMember(Name = "body")]
        public string Body { get; set; }

        [DataMember(Name = "assets")]
        public ReleaseAsset[] Assets { get; set; }
    }

    /// <summary>Release 附件模型。</summary>
    [DataContract]
    internal sealed class ReleaseAsset
    {
        [DataMember(Name = "name")]
        public string Name { get; set; }

        [DataMember(Name = "browser_download_url")]
        public string DownloadUrl { get; set; }
    }

    /// <summary>检查结果缓存模型（序列化为 JSON 存于 %LocalAppData%\SerialPort\update_check.cache）。</summary>
    [DataContract]
    internal sealed class UpdateCheckCache
    {
        /// <summary>检查完成时间（UTC），用于判断缓存是否过期。</summary>
        [DataMember(Name = "checked_at")]
        public DateTime CheckedAt { get; set; }

        /// <summary>最新版本号字符串（"1.0.0"）；仓库无 Release 时为 null。</summary>
        [DataMember(Name = "latest_version")]
        public string LatestVersion { get; set; }

        [DataMember(Name = "release_notes")]
        public string ReleaseNotes { get; set; }

        [DataMember(Name = "exe_url")]
        public string ExeUrl { get; set; }

        [DataMember(Name = "sha256_url")]
        public string Sha256Url { get; set; }

        [DataMember(Name = "config_url")]
        public string ConfigUrl { get; set; }
    }

    /// <summary>GitHub 接口访问频率受限（HTTP 403 rate limit exceeded）时抛出，由服务层翻译成友好提示。</summary>
    internal sealed class RateLimitExceededException : Exception
    {
        public RateLimitExceededException() { }
        public RateLimitExceededException(string message) : base(message) { }
    }
}
