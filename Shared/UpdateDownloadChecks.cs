#nullable disable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
#if NETFRAMEWORK
using Neighbor.Core;
namespace Neighbor.Tests
#else
using NekoChat.Core;
namespace NekoChat.Tests
#endif
{
    internal static class UpdateDownloadChecks
    {
        private const string Url = "https://github.com/yuipan0611/whats-up-my-neighbor-ai-input-windows/releases/download/nekochat-lite-v0.1.0-beta.9/NekoChat-Setup-win-x64.exe";
        private sealed class Handler : HttpMessageHandler
        {
            internal Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> Reply;
            internal int Requests, Payloads;
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Requests++;
                if (!request.RequestUri.AbsolutePath.EndsWith(".sha256", StringComparison.Ordinal)) Payloads++;
                return Reply(request, cancellationToken);
            }
        }
        private sealed class UnknownLength : HttpContent
        {
            private readonly byte[] bytes;
            private readonly bool slow;
            internal UnknownLength(byte[] bytes, bool slow = false) { this.bytes = bytes; this.slow = slow; }
            protected override bool TryComputeLength(out long length) { length = 0; return false; }
            protected override Task SerializeToStreamAsync(Stream stream, TransportContext context) => stream.WriteAsync(bytes, 0, bytes.Length);
            protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult<Stream>(slow ? new SlowStream() : new MemoryStream(bytes, false));
        }
        private sealed class SlowStream : MemoryStream
        {
            public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            { await Task.Delay(10000, cancellationToken).ConfigureAwait(false); return 0; }
        }
        private sealed class ProgressLog : IProgress<UpdateDownloadProgress>
        {
            internal readonly List<UpdateDownloadProgress> Entries = new List<UpdateDownloadProgress>();
            public void Report(UpdateDownloadProgress value) { lock (Entries) Entries.Add(value); }
        }
        private static string Hash(byte[] bytes)
        { using (var hash = SHA256.Create()) return BitConverter.ToString(hash.ComputeHash(bytes)).Replace("-", ""); }
        private static HttpResponseMessage Ok(HttpContent content) => new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        private static Handler Server(byte[] payload, string checksum = null)
        {
            return new Handler { Reply = (request, token) => Task.FromResult(Ok(request.RequestUri.AbsolutePath.EndsWith(".sha256", StringComparison.Ordinal)
                ? (HttpContent)new StringContent(checksum ?? Hash(payload)) : new ByteArrayContent(payload))) };
        }
        internal static async Task<int> Run(Action<bool, string> report = null)
        {
            int checks = 0;
            Action<bool, string> check = (ok, message) =>
            { checks++; report?.Invoke(ok, "Update download: " + message); if (!ok) throw new Exception(message); };
            Func<Func<Task>, Task> reject = async action =>
            {
                bool failed = false;
                try { await action().ConfigureAwait(false); }
                catch (Exception ex) when (ex is IOException || ex is InvalidDataException || ex is OperationCanceledException) { failed = true; }
                check(failed, "unsafe, corrupt or cancelled download rejected");
            };
            string releaseJson = "[{\"tag_name\":\"nekochat-lite-v0.1.0-beta.9\",\"draft\":false,\"assets\":[{\"name\":\"NekoChat-Lite.exe\",\"browser_download_url\":\"https://example.com/portable.exe\"},{\"name\":\"NekoChat-Setup-win-x64.exe.sha256\",\"browser_download_url\":\"" + Url + ".sha256\"},{\"name\":\"NekoChat-Setup-win-x64.exe\",\"browser_download_url\":\"" + Url + "\"}]}]";
#if NETFRAMEWORK
            var selected = UpdateChecker.ParseReleasesJson(releaseJson, SemVersion.Parse("0.1.0-beta.8"), "nekochat-lite-v");
#else
            UpdateCheckResult selected;
            using (var json = new MemoryStream(Encoding.UTF8.GetBytes(releaseJson)))
                selected = UpdateChecker.ParseReleases(json, SemVersion.Parse("0.1.0-beta.8"), "nekochat-lite-v");
#endif
            check(selected.Success && selected.DownloadUrl == Url, "setup is selected even when portable EXE is first");
            check(selected.ChecksumUrl == Url + ".sha256", "matching checksum asset is selected independently of asset order");
            var bytes = Encoding.ASCII.GetBytes("MZ" + new string('a', 512));
            var root = Path.Combine(Path.GetTempPath(), "neighbor-update-download-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            Func<string, string> folder = name => Path.Combine(root, name);
            try
            {
                var server = Server(bytes); var progress = new ProgressLog();
                using (var downloader = new UpdateDownloader(server))
                {
                    var file = await downloader.DownloadAsync(Url, Url + ".sha256", folder("success"), progress).ConfigureAwait(false);
                    check(File.ReadAllBytes(file).SequenceEqual(bytes), "downloaded installer bytes match official checksum");
                    check(progress.Entries.Count >= 2 && progress.Entries.Last().BytesReceived == bytes.Length, "streamed progress reports completion");
                    check(!File.Exists(file + ".part"), "successful download leaves no partial");
                    await downloader.DownloadAsync(Url, Url + ".sha256", folder("success")).ConfigureAwait(false);
                    check(server.Payloads == 1 && server.Requests == 3, "verified cache is reused without downloading payload twice");
                    check(Directory.GetFiles(folder("success"), "*.exe").Length == 1, "one fixed installer cache per edition");
                    if (Environment.OSVersion.Platform == PlatformID.Win32NT)
                        check(File.ReadAllText(file + ":Zone.Identifier").Contains("ZoneId=3"), "Windows Internet-zone security metadata preserved");
                }
                // Same implementation supports the standard edition and its filename checksum format.
                string standard = Url.Replace("nekochat-lite-v0.1.0-beta.9/NekoChat-Setup-win-x64.exe", "v0.1.0-beta.9/Neighbor-Setup-0.1.0-beta.9-x64.exe");
                using (var downloader = new UpdateDownloader(Server(bytes, Hash(bytes).ToLowerInvariant() + "  Neighbor-Setup-0.1.0-beta.9-x64.exe\n")))
                    check(File.Exists(await downloader.DownloadAsync(standard, standard + ".sha256", folder("standard")).ConfigureAwait(false)), "standard edition setup and checksum supported");

                foreach (var bad in new[] { Url.Replace("https:", "http:"), Url.Replace("github.com", "evil.example"), Url.Replace("yuipan0611", "someone-else"), Url + "?token=x", Url + "#fragment", Url.Replace("NekoChat-Setup-win-x64.exe", "NekoChat-Lite.exe") })
                {
                    var handler = Server(bytes);
                    using (var downloader = new UpdateDownloader(handler))
                        await reject(async () => { await downloader.DownloadAsync(bad, bad + ".sha256", folder("unsafe")).ConfigureAwait(false); }).ConfigureAwait(false);
                    check(handler.Requests == 0 && !Directory.Exists(folder("unsafe")), "untrusted URL or portable EXE writes nothing and sends no request");
                }
                using (var downloader = new UpdateDownloader(Server(bytes)))
                    await reject(async () => { await downloader.DownloadAsync(Url, Url.Replace("beta.9", "beta.8") + ".sha256", folder("cross-version")).ConfigureAwait(false); }).ConfigureAwait(false);

                foreach (var checksum in new[] { "broken", new string('0', 64), Hash(bytes) + " other.exe", Hash(bytes) + "\n" + Hash(bytes), new string('x', 5000) })
                {
                    string directory = folder(Guid.NewGuid().ToString("N"));
                    using (var downloader = new UpdateDownloader(Server(bytes, checksum)))
                        await reject(async () => { await downloader.DownloadAsync(Url, Url + ".sha256", directory).ConfigureAwait(false); }).ConfigureAwait(false);
                    check(Directory.GetFiles(directory, "*.part").Length == 0 && Directory.GetFiles(directory, "*.exe").Length == 0, "bad checksum leaves no executable or partial");
                }
                var html = Encoding.UTF8.GetBytes("<html>not an executable</html>");
                using (var downloader = new UpdateDownloader(Server(html)))
                    await reject(async () => { await downloader.DownloadAsync(Url, Url + ".sha256", folder("html")).ConfigureAwait(false); }).ConfigureAwait(false);

                foreach (var kind in new[] { "oversize-header", "oversize-stream", "truncated", "not-found" })
                {
                    var handler = Server(bytes);
                    handler.Reply = (request, token) =>
                    {
                        if (request.RequestUri.AbsolutePath.EndsWith(".sha256", StringComparison.Ordinal)) return Task.FromResult(Ok(new StringContent(Hash(bytes))));
                        if (kind == "not-found") return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
                        var content = kind == "oversize-stream" ? (HttpContent)new UnknownLength(bytes) : new ByteArrayContent(bytes);
                        if (kind == "oversize-header") content.Headers.ContentLength = 2000;
                        if (kind == "truncated") content.Headers.ContentLength = bytes.Length + 1;
                        return Task.FromResult(Ok(content));
                    };
                    using (var downloader = new UpdateDownloader(handler, kind.StartsWith("oversize", StringComparison.Ordinal) ? 256 : 1024))
                        await reject(async () => { await downloader.DownloadAsync(Url, Url + ".sha256", folder(kind)).ConfigureAwait(false); }).ConfigureAwait(false);
                    check(Directory.GetFiles(folder(kind), "*.part").Length == 0, "size/HTTP failure cleans partial download");
                }
                foreach (var host in new[] { "release-assets.githubusercontent.com", "evil.example" })
                {
                    var handler = Server(bytes);
                    handler.Reply = (request, token) =>
                    {
                        if (request.RequestUri.AbsolutePath.EndsWith(".sha256", StringComparison.Ordinal)) return Task.FromResult(Ok(new StringContent(Hash(bytes))));
                        if (request.RequestUri.Host == "github.com") return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Redirect) { Headers = { Location = new Uri("https://" + host + "/asset") } });
                        return Task.FromResult(Ok(new ByteArrayContent(bytes)));
                    };
                    using (var downloader = new UpdateDownloader(handler))
                    {
                        if (host == "evil.example")
                        { await reject(async () => { await downloader.DownloadAsync(Url, Url + ".sha256", folder(host)).ConfigureAwait(false); }).ConfigureAwait(false); check(handler.Requests == 2, "untrusted redirect never requested"); }
                        else check(File.Exists(await downloader.DownloadAsync(Url, Url + ".sha256", folder(host)).ConfigureAwait(false)), "official GitHub asset redirect supported");
                    }
                }
                // A bad replacement must not destroy an already verified cached installer.
                string cache = folder("preserve-cache"); Directory.CreateDirectory(cache);
                string cached = Path.Combine(cache, "NekoChat-Setup-win-x64.exe"); File.WriteAllBytes(cached, bytes);
                var replacement = Encoding.ASCII.GetBytes("MZreplacement");
                using (var downloader = new UpdateDownloader(Server(replacement, new string('0', 64))))
                    await reject(async () => { await downloader.DownloadAsync(Url, Url + ".sha256", cache).ConfigureAwait(false); }).ConfigureAwait(false);
                check(File.ReadAllBytes(cached).SequenceEqual(bytes), "failed replacement preserves prior cache");
                using (var downloader = new UpdateDownloader(Server(replacement)))
                    await downloader.DownloadAsync(Url, Url + ".sha256", cache).ConfigureAwait(false);
                check(File.ReadAllBytes(cached).SequenceEqual(replacement) && Directory.GetFiles(cache, "*.exe").Length == 1, "verified replacement atomically rotates one cached setup");

                var slow = Server(bytes);
                slow.Reply = (request, token) => Task.FromResult(Ok(request.RequestUri.AbsolutePath.EndsWith(".sha256", StringComparison.Ordinal)
                    ? (HttpContent)new StringContent(Hash(bytes)) : new UnknownLength(bytes, slow: true)));
                using (var cancellation = new CancellationTokenSource())
                using (var downloader = new UpdateDownloader(slow))
                {
                    var first = downloader.DownloadAsync(Url, Url + ".sha256", folder("cancel"), cancellation: cancellation.Token);
                    using (var second = new UpdateDownloader(Server(bytes)))
                        await reject(async () => { await second.DownloadAsync(Url, Url + ".sha256", folder("cancel")).ConfigureAwait(false); }).ConfigureAwait(false);
                    cancellation.Cancel();
                    await reject(async () => { await first.ConfigureAwait(false); }).ConfigureAwait(false);
                    check(Directory.GetFiles(folder("cancel"), "*.part").Length == 0 && Directory.GetFiles(folder("cancel"), "*.exe").Length == 0, "cancel and duplicate download leave no partial or executable");
                }
#if !NETFRAMEWORK
                string link = folder("linked"), external = folder("outside"); Directory.CreateDirectory(external);
                bool linkCreated = false;
                try { Directory.CreateSymbolicLink(link, external); linkCreated = true; }
                catch (UnauthorizedAccessException) when (OperatingSystem.IsWindows()) { }
                if (linkCreated)
                {
                    using (var downloader = new UpdateDownloader(Server(bytes)))
                        await reject(async () => { await downloader.DownloadAsync(Url, Url + ".sha256", link).ConfigureAwait(false); }).ConfigureAwait(false);
                    check(Directory.GetFileSystemEntries(external).Length == 0, "linked cache never writes outside app data");
                    Directory.Delete(link);
                }
#endif
            }
            finally { Directory.Delete(root, true); }
            Console.WriteLine("Direct update download: " + checks + " verification, progress, cancellation and boundary checks passed.");
            return checks;
        }
    }
}
