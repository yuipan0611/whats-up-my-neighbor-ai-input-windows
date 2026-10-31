#nullable disable
using System;
using System.IO;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

#if NETFRAMEWORK
namespace Neighbor.Core
#else
namespace NekoChat.Core
#endif
{
    public sealed class UpdateDownloadProgress
    {
        public long BytesReceived { get; }
        public long? TotalBytes { get; }
        public UpdateDownloadProgress(long received, long? total) { BytesReceived = received; TotalBytes = total; }
    }

    /// <summary>Consent-driven download of a verified installer. Never starts a process.</summary>
    public sealed class UpdateDownloader : IDisposable
    {
        private const string ReleasePrefix = "/yuipan0611/whats-up-my-neighbor-ai-input-windows/releases/download/";
        public const long DefaultMaximumBytes = 512L * 1024 * 1024;
        private readonly HttpClient client;
        private readonly long maximumBytes;
        private readonly TimeSpan idleTimeout;
        public static readonly TimeSpan DefaultIdleTimeout = TimeSpan.FromSeconds(60);

        public UpdateDownloader(HttpMessageHandler handler = null, long maximumBytes = DefaultMaximumBytes, TimeSpan? idleTimeout = null)
        {
            if (maximumBytes < 2 || maximumBytes > DefaultMaximumBytes) throw new ArgumentOutOfRangeException(nameof(maximumBytes));
            var idle = idleTimeout ?? DefaultIdleTimeout;
            if (idle <= TimeSpan.Zero || idle > TimeSpan.FromMinutes(30)) throw new ArgumentOutOfRangeException(nameof(idleTimeout));
            this.maximumBytes = maximumBytes;
            this.idleTimeout = idle;
            var transport = handler ?? new HttpClientHandler();
            if (transport is HttpClientHandler http) http.AllowAutoRedirect = false;
            client = new HttpClient(transport);
            client.Timeout = Timeout.InfiniteTimeSpan;
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Neighbor-UpdateDownloader/1.0");
        }

        public async Task<string> DownloadAsync(string installerUrl, string checksumUrl, string cacheDirectory,
            IProgress<UpdateDownloadProgress> progress = null, CancellationToken cancellation = default)
        {
            var installer = ValidateReleaseUrl(installerUrl);
            var checksum = ValidateReleaseUrl(checksumUrl);
            if (checksum.AbsolutePath != installer.AbsolutePath + ".sha256")
                throw new InvalidDataException("安裝檔與校驗檔不是同一個版本。");
            var relative = installer.AbsolutePath.Substring(ReleasePrefix.Length).Split('/');
            if (relative.Length != 2) throw new InvalidDataException("更新檔案網址不正確。");
            string tag = relative[0], name = relative[1];
            bool lite = tag.StartsWith("nekochat-lite-v", StringComparison.Ordinal);
            string version = lite ? tag.Substring("nekochat-lite-v".Length) : tag.StartsWith("v", StringComparison.Ordinal) ? tag.Substring(1) : "";
            if (!Regex.IsMatch(version, @"^\d+\.\d+\.\d+(?:-[A-Za-z0-9]+(?:\.[A-Za-z0-9]+)*)?$", RegexOptions.CultureInvariant) ||
                name != (lite ? "NekoChat-Setup-win-x64.exe" : "Neighbor-Setup-" + version + "-x64.exe"))
                throw new InvalidDataException("更新來源不是對應版本的安裝程式。");

            string root = Path.GetFullPath(cacheDirectory);
            NoLinks(root);
            Directory.CreateDirectory(root);
            string destination = Path.Combine(root, lite ? "NekoChat-Setup-win-x64.exe" : "Neighbor-Setup-x64.exe");
            string partial = destination + ".part";
            string lockPath = Path.Combine(root, ".update-download.lock");
            NoLinks(destination); NoLinks(partial); NoLinks(lockPath);
            using (var gate = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation))
            {
                timeout.CancelAfter(TimeSpan.FromMinutes(30));
                var token = timeout.Token;
                bool createdPartial = false;
                try
                {
                    string expected;
                    using (var response = await GetAsync(checksum, token).ConfigureAwait(false))
                    using (var body = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                    {
                        var bytes = new MemoryStream();
                        using (bytes)
                        {
                            await CopyBoundedAsync(body, bytes, 4096, null, null, token, idleTimeout, response.Dispose).ConfigureAwait(false);
                            expected = ParseChecksum(Encoding.UTF8.GetString(bytes.ToArray()), name);
                        }
                    }
                    bool reusable = false;
                    long cachedLength = 0;
                    if (File.Exists(destination))
                    {
                        using (var existing = new FileStream(destination, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true))
                        {
                            cachedLength = existing.Length;
                            reusable = existing.Length <= maximumBytes && await DigestAsync(existing, token).ConfigureAwait(false) == expected && IsExecutable(destination);
                        }
                    }
                    if (reusable)
                    {
                        MarkInternetDownload(destination, installer);
                        progress?.Report(new UpdateDownloadProgress(cachedLength, cachedLength));
                        return destination;
                    }
                    // Only one partial and one verified installer are retained per edition.
                    if (File.Exists(partial)) File.Delete(partial);
                    using (var response = await GetAsync(installer, token).ConfigureAwait(false))
                    {
                        long? length = response.Content.Headers.ContentLength;
                        if (length.HasValue && (length.Value < 2 || length.Value > maximumBytes))
                            throw new InvalidDataException("更新檔案大小不正確或超出限制。");
                        string actual;
                        using (var body = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                        using (var output = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
                        {
                            createdPartial = true;
                            actual = await CopyBoundedAsync(body, output, maximumBytes, length, progress, token, idleTimeout, response.Dispose).ConfigureAwait(false);
                            await output.FlushAsync(token).ConfigureAwait(false);
                        }
                        if (actual != expected || !IsExecutable(partial))
                            throw new InvalidDataException("安裝檔驗證失敗，已停止更新。請重新下載。");
                    }
                    token.ThrowIfCancellationRequested();
                    MarkInternetDownload(partial, installer);
                    if (File.Exists(destination)) File.Replace(partial, destination, null);
                    else File.Move(partial, destination);
                    createdPartial = false;
                    MarkInternetDownload(destination, installer);
                    return destination;
                }
                catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
                { throw new TimeoutException("下載更新逾時，請稍後重試。"); }
                catch (HttpRequestException ex)
                { throw new IOException("無法下載更新，請確認網路連線後重試。", ex); }
                finally
                {
                    if (createdPartial)
                    {
                        // A failure to delete must remain visible, not report a clean cancellation.
                        File.Delete(partial);
                    }
                }
            }
        }

        private async Task<HttpResponseMessage> GetAsync(Uri uri, CancellationToken cancellation)
        {
            for (int redirects = 0; redirects <= 5; redirects++)
            {
                cancellation.ThrowIfCancellationRequested();
                if (uri.Scheme != Uri.UriSchemeHttps || uri.Port != 443 || uri.UserInfo.Length != 0 ||
                    !(uri.Host == "github.com" || uri.Host == "release-assets.githubusercontent.com" ||
                      uri.Host == "objects.githubusercontent.com" || uri.Host == "github-releases.githubusercontent.com"))
                    throw new InvalidDataException("更新下載被導向未允許的來源。");
                var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellation).ConfigureAwait(false);
                if (response.StatusCode == HttpStatusCode.OK) return response;
                var status = (int)response.StatusCode;
                if (status is 301 or 302 or 303 or 307 or 308)
                {
                    var location = response.Headers.Location;
                    response.Dispose();
                    if (location == null) throw new InvalidDataException("更新下載的轉址資料不完整。");
                    uri = location.IsAbsoluteUri ? location : new Uri(uri, location);
                    continue;
                }
                response.Dispose();
                throw new IOException("更新伺服器回應失敗（HTTP " + status + "），請稍後重試。");
            }
            throw new InvalidDataException("更新下載轉址次數過多。");
        }

        private static Uri ValidateReleaseUrl(string value)
        {
            if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || uri.Host != "github.com" ||
                uri.Port != 443 || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0 ||
                !uri.AbsolutePath.StartsWith(ReleasePrefix, StringComparison.Ordinal) || uri.AbsolutePath.Contains("%"))
                throw new InvalidDataException("更新檔案來源無法確認。");
            return uri;
        }
        private static string ParseChecksum(string text, string fileName)
        {
            var lines = text.Trim('\uFEFF', ' ', '\r', '\n', '\t').Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            if (lines.Length != 1) throw new InvalidDataException("更新校驗資料不正確。");
            var match = Regex.Match(lines[0], @"^([A-Fa-f0-9]{64})(?:\s+\*?(.+))?\s*$", RegexOptions.CultureInvariant);
            if (!match.Success || match.Groups[2].Success && match.Groups[2].Value.Trim() != fileName)
                throw new InvalidDataException("更新校驗資料不正確。");
            return match.Groups[1].Value.ToUpperInvariant();
        }
        private static async Task<string> CopyBoundedAsync(Stream input, Stream output, long limit, long? expectedLength,
            IProgress<UpdateDownloadProgress> progress, CancellationToken cancellation, TimeSpan? idleTimeout = null, Action abort = null)
        {
            // Network reads get a per-read idle deadline; it is re-armed after every chunk.
            using (var idle = CancellationTokenSource.CreateLinkedTokenSource(cancellation))
            using (var hash = SHA256.Create())
            {
                var buffer = new byte[65536];
                long copied = 0;
                var clock = Stopwatch.StartNew();
                long lastReport = 0;
                int count;
                progress?.Report(new UpdateDownloadProgress(0, expectedLength));
                while (true)
                {
                    if (idleTimeout.HasValue) idle.CancelAfter(idleTimeout.Value);
                    try { count = await ReadAbortableAsync(input, buffer, idle.Token, abort).ConfigureAwait(false); }
                    catch (OperationCanceledException) when (idle.IsCancellationRequested && !cancellation.IsCancellationRequested)
                    { throw new TimeoutException("下載更新時連線停止回應，請稍後重試。"); }
                    if (count == 0) break;
                    copied += count;
                    if (copied > limit || expectedLength.HasValue && copied > expectedLength.Value)
                        throw new InvalidDataException("更新檔案大小超出限制。");
                    hash.TransformBlock(buffer, 0, count, buffer, 0);
                    await output.WriteAsync(buffer, 0, count, cancellation).ConfigureAwait(false);
                    if (clock.ElapsedMilliseconds - lastReport >= 100)
                    {
                        progress?.Report(new UpdateDownloadProgress(copied, expectedLength));
                        lastReport = clock.ElapsedMilliseconds;
                    }
                }
                if (expectedLength.HasValue && copied != expectedLength.Value)
                    throw new InvalidDataException("更新檔案下載不完整。");
                hash.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                progress?.Report(new UpdateDownloadProgress(copied, expectedLength));
                return BitConverter.ToString(hash.Hash).Replace("-", "");
            }
        }
        /// <summary>
        /// .NET Framework response streams inherit Stream.ReadAsync, which only checks the token before the
        /// read starts; a body that stalls after headers would ignore Cancel and every timeout. Race the read
        /// against the token so cancellation always returns promptly, and abort the transport off-thread so
        /// the abandoned read is released without blocking the caller (or the UI thread that cancelled).
        /// </summary>
        private static async Task<int> ReadAbortableAsync(Stream input, byte[] buffer, CancellationToken cancellation, Action abort)
        {
            cancellation.ThrowIfCancellationRequested();
            var read = input.ReadAsync(buffer, 0, buffer.Length, cancellation);
            if (read.IsCompleted) return await read.ConfigureAwait(false);
            var cancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using (cancellation.Register(state => ((TaskCompletionSource<bool>)state).TrySetResult(true), cancelled))
            {
                if (await Task.WhenAny(read, cancelled.Task).ConfigureAwait(false) == read)
                    return await read.ConfigureAwait(false);
            }
            // The read ignored cancellation. Observe its eventual fault and tear down the connection.
            _ = read.ContinueWith(t => _ = t.Exception, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try { if (abort != null) abort(); else input.Dispose(); } catch { }
            });
            throw new OperationCanceledException(cancellation);
        }
        private static Task<string> DigestAsync(Stream input, CancellationToken cancellation) =>
            CopyBoundedAsync(input, Stream.Null, DefaultMaximumBytes, null, null, cancellation);
        private static bool IsExecutable(string file)
        {
            using (var input = File.OpenRead(file)) return input.ReadByte() == 'M' && input.ReadByte() == 'Z';
        }
        [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateZoneFile(string file, uint access, uint share, IntPtr security,
            uint creation, uint attributes, IntPtr template);
        private static void MarkInternetDownload(string file, Uri source)
        {
            if (Environment.OSVersion.Platform != PlatformID.Win32NT) return;
            // .NET Framework's path parser rejects NTFS stream syntax. Use the
            // native API so both target frameworks retain Mark-of-the-Web.
            using (var handle = CreateZoneFile(file + ":Zone.Identifier", 0x40000000, 7, IntPtr.Zero, 2, 0x80, IntPtr.Zero))
            {
                if (handle.IsInvalid) throw new IOException("無法標記網路下載檔案，已停止更新。", new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()));
                using (var stream = new FileStream(handle, FileAccess.Write))
                {
                    var bytes = Encoding.ASCII.GetBytes("[ZoneTransfer]\r\nZoneId=3\r\nHostUrl=" + source.AbsoluteUri + "\r\n");
                    stream.Write(bytes, 0, bytes.Length);
                }
            }
        }
        private static void NoLinks(string path)
        {
            for (string item = path; item != null; item = Path.GetDirectoryName(item))
            {
                try
                {
                    if ((File.GetAttributes(item) & FileAttributes.ReparsePoint) != 0)
                        throw new IOException("更新暫存路徑包含連結，無法安全下載。");
                }
                catch (FileNotFoundException) { }
                catch (DirectoryNotFoundException) { }
            }
        }
        public void Dispose() { client.Dispose(); }
    }
}
