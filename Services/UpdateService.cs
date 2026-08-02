using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
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

        private readonly SynchronizationContext _syncContext;
        private bool _checking;   // 检查中标志，避免重复触发

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
        /// 后台检查最新版本。notifyWhenUpToDate = true 时即使已是最新也触发 CheckCompleted（用于手动检查）。
        /// </summary>
        public void CheckForUpdatesAsync(bool notifyWhenUpToDate = false)
        {
            if (_checking) return;
            _checking = true;

            Task.Run(() =>
            {
                try
                {
                    UpdateCheckResult result = FetchLatestRelease();
                    if (!notifyWhenUpToDate && !result.IsUpdateAvailable && result.LatestVersion == null)
                        return;   // 自动检查且仓库无 Release：静默
                    Post(() => CheckCompleted?.Invoke(this, result));
                }
                catch (Exception ex)
                {
                    Post(() => UpdateError?.Invoke(this, "检查更新失败：" + ex.Message));
                }
                finally
                {
                    _checking = false;
                }
            });
        }

        /// <summary>
        /// 下载并应用更新（下载 → SHA256 校验 → 替换 → 启动新版本 → 触发 UpdateApplied 退出）。
        /// 任何失败都会回滚并抛出，通过 UpdateError 报告，不影响当前程序运行。
        /// </summary>
        public void DownloadAndApplyAsync(UpdateCheckResult result)
        {
            Task.Run(async () =>
            {
                try
                {
                    string workDir = Path.Combine(Path.GetTempPath(), "SerialPortUpdate");
                    Directory.CreateDirectory(workDir);

                    string exePath = Assembly.GetExecutingAssembly().Location;
                    string exeName = Path.GetFileName(exePath);
                    string newExe = Path.Combine(workDir, exeName);

                    // 1. 下载新程序与 SHA256 校验文件（带进度报告）
                    Post(() => UpdateProgress?.Invoke(this, 0));
                    await DownloadFileAsync(result.ExeUrl, newExe).ConfigureAwait(false);
                    if (!string.IsNullOrEmpty(result.Sha256Url))
                    {
                        string hashFile = Path.Combine(workDir, exeName + ".sha256");
                        await DownloadFileAsync(result.Sha256Url, hashFile).ConfigureAwait(false);
                        VerifySha256(newExe, hashFile);
                    }

                    // 2. 替换运行中的 exe（Windows 允许重命名运行中的映像，故可先改名再移入新文件）
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

                    // 3. 尽力替换配置文件（发布包含 config 时）；失败不阻断升级
                    if (!string.IsNullOrEmpty(result.ConfigUrl))
                    {
                        try
                        {
                            string newConfig = Path.Combine(workDir, exeName + ".config");
                            await DownloadFileAsync(result.ConfigUrl, newConfig).ConfigureAwait(false);
                            File.Copy(newConfig, exePath + ".config", true);
                        }
                        catch
                        {
                            // 配置文件替换失败可忽略（不影响程序本体运行）
                        }
                    }

                    // 4. 启动新版本，退出当前进程
                    Process.Start(exePath);
                    Post(() => UpdateApplied?.Invoke(this, EventArgs.Empty));
                }
                catch (Exception ex)
                {
                    Post(() => UpdateError?.Invoke(this, "更新失败：" + ex.Message));
                }
            });
        }

        /// <summary>查询 GitHub 最新 Release 并解析为检查结果。</summary>
        private static UpdateCheckResult FetchLatestRelease()
        {
            var result = new UpdateCheckResult { CurrentVersion = CurrentVersion };
            using (var client = new HttpClient())
            {
                client.DefaultRequestHeaders.Add("User-Agent", "SerialPort-Update-Checker");   // GitHub API 要求 UA
                using (HttpResponseMessage resp = client.GetAsync(string.Format(LatestReleaseUrl, GitHubOwner, GitHubRepo)).Result)
                {
                    // 仓库还没有任何 Release：视为“无可用更新”
                    if (resp.StatusCode == HttpStatusCode.NotFound)
                        return result;
                    resp.EnsureSuccessStatusCode();

                    using (Stream stream = resp.Content.ReadAsStreamAsync().Result)
                    {
                        var serializer = new DataContractJsonSerializer(typeof(ReleaseInfo));
                        var release = (ReleaseInfo)serializer.ReadObject(stream);

                        result.LatestVersion = ParseTagVersion(release.TagName);
                        result.ReleaseNotes = release.Body;
                        if (result.LatestVersion == null) return result;

                        result.IsUpdateAvailable = result.LatestVersion.CompareTo(result.CurrentVersion) > 0;
                        if (!result.IsUpdateAvailable) return result;

                        // 从附件中按文件名定位程序 / 校验文件 / 配置文件
                        string exeName = Path.GetFileName(Assembly.GetExecutingAssembly().Location);
                        if (release.Assets == null) return result;
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
                        return result;
                    }
                }
            }
        }

        /// <summary>下载文件到目标路径，期间按已读字节数报告进度。</summary>
        private async Task DownloadFileAsync(string url, string destPath)
        {
            using (var client = new HttpClient())
            {
                client.DefaultRequestHeaders.Add("User-Agent", "SerialPort-Update-Checker");
                using (HttpResponseMessage resp = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false))
                {
                    resp.EnsureSuccessStatusCode();
                    long total = resp.Content.Headers.ContentLength ?? 0;
                    using (Stream input = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false))
                    using (var output = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true))
                    {
                        var buffer = new byte[81920];
                        long done = 0;
                        int read;
                        while ((read = await input.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false)) > 0)
                        {
                            await output.WriteAsync(buffer, 0, read).ConfigureAwait(false);
                            done += read;
                            ReportProgress(done, total);
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
            string content = File.ReadAllText(hashFilePath).Trim();
            string[] parts = content.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) throw new InvalidDataException("SHA256 校验文件为空。");
            return parts[0].ToLowerInvariant();
        }

        /// <summary>把 "v1.0.1" 形式的 tag 解析为版本号；解析失败返回 null。</summary>
        private static Version ParseTagVersion(string tag)
        {
            if (string.IsNullOrEmpty(tag)) return null;
            string s = tag.TrimStart('v', 'V');
            Version v;
            return Version.TryParse(s, out v) ? v : null;
        }

        /// <summary>进度事件编组回 UI 线程。</summary>
        private void ReportProgress(long done, long total)
        {
            int percent = total > 0 ? (int)(done * 100 / total) : 0;
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
}
