#nullable disable
using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
#if NETFRAMEWORK
using Neighbor.Core;
namespace Neighbor.Windows.UI
#else
using NekoChat.Core;
namespace NekoChat.Windows
#endif
{
    /// <summary>Isolated native-UI smoke fixture; never contacts a server or executes a file.</summary>
    internal static class UpdateDownloadUiFixture
    {
        internal static readonly byte[] Payload = Encoding.ASCII.GetBytes("MZsynthetic-update-fixture-not-executable");
#if NETFRAMEWORK
        internal const string Url = "https://github.com/yuipan0611/whats-up-my-neighbor-ai-input-windows/releases/download/v0.1.0-beta.9/Neighbor-Setup-0.1.0-beta.9-x64.exe";
#else
        internal const string Url = "https://github.com/yuipan0611/whats-up-my-neighbor-ai-input-windows/releases/download/nekochat-lite-v0.1.0-beta.9/NekoChat-Setup-win-x64.exe";
#endif
        internal static UpdateCheckResult Release => UpdateCheckResult.UpdateAvailable("0.1.0-beta.8", "0.1.0-beta.9", "Synthetic", null, null, Url, Url + ".sha256");
        internal static UpdateDownloader Downloader(string mode) => new UpdateDownloader(new FixtureHandler(mode));
        private sealed class FixtureHandler : HttpMessageHandler
        {
            private readonly string mode;
            internal FixtureHandler(string mode) { this.mode = mode; }
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
            {
                if (request.RequestUri.AbsolutePath.EndsWith(".sha256", StringComparison.Ordinal))
                {
                    string checksum;
                    using (var hash = SHA256.Create()) checksum = BitConverter.ToString(hash.ComputeHash(Payload)).Replace("-", "");
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(mode == "failure" ? new string('0', 64) : checksum) });
                }
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = mode == "cancel" ? (HttpContent)new SlowContent() : new ByteArrayContent(Payload) });
            }
        }
        private sealed class SlowContent : HttpContent
        {
            protected override bool TryComputeLength(out long length) { length = 0; return false; }
            protected override Task SerializeToStreamAsync(Stream stream, TransportContext context) => Task.CompletedTask;
            protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult<Stream>(new SlowStream());
        }
        private sealed class SlowStream : MemoryStream
        {
            public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken token)
            { await Task.Delay(10000, token).ConfigureAwait(false); return 0; }
        }
    }
}
