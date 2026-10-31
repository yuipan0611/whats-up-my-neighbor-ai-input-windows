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

        public UpdateDownloader(HttpMessageHandler handler = null, long maximumBytes = DefaultMaximumBytes)
        {
            if (maximumBytes < 2 || maximumBytes > DefaultMaximumBytes) throw new ArgumentOutOfRangeException(nameof(maximumBytes));
            this.maximumBytes = maximumBytes;
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
                            await CopyBoundedAsync(body, bytes, 4096, null, null, token).ConfigureAwait(false);
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
                            actual = await CopyBoundedAsync(body, output, maximumBytes, length, progress, token).ConfigureAwait(false);
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
            IProgress<UpdateDownloadProgress> progress, CancellationToken cancellation)
        {
            using (var hash = SHA256.Create())
            {
                var buffer = new byte[65536];
                long copied = 0;
                var clock = Stopwatch.StartNew();
                long lastReport = 0;
                int count;
                progress?.Report(new UpdateDownloadProgress(0, expectedLength));
                while ((count = await input.ReadAsync(buffer, 0, buffer.Length, cancellation).ConfigureAwait(false)) != 0)
                {
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
        private static Task<string> DigestAsync(Stream input, CancellationToken cancellation) =>
            CopyBoundedAsync(input, Stream.Null, DefaultMaximumBytes, null, null, cancellation);
        private static bool IsExecutable(string file)
        {
            using (var input = File.OpenRead(file)) return input.ReadByte() == 'M' && input.ReadByte() == 'Z';
        }
        private static void MarkInternetDownload(string file, Uri source)
        {
            if (Environment.OSVersion.Platform == PlatformID.Win32NT)
                File.WriteAllText(file + ":Zone.Identifier", "[ZoneTransfer]\r\nZoneId=3\r\nHostUrl=" + source.AbsoluteUri + "\r\n", Encoding.ASCII);
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
