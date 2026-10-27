using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Neighbor.Core;

namespace Neighbor.Tests
{
    public static class UpdateCheckerTests
    {
        public static void Run(Action<bool, string> check)
        {
            // 1. SemVersion parsing
            check(SemVersion.TryParse("0.1.0-beta.3", out var v1) && v1.Major == 0 && v1.Minor == 1 && v1.Patch == 0 && v1.Prerelease == "beta.3", "SemVersion parses prerelease");
            check(SemVersion.TryParse("v1.2.3", out var v2) && v2.Major == 1 && v2.Minor == 2 && v2.Patch == 3 && !v2.IsPrerelease, "SemVersion parses leading v");
            check(SemVersion.TryParse("v2.0", out var v3) && v3.Major == 2 && v3.Minor == 0 && v3.Patch == 0, "SemVersion handles missing patch");
            check(SemVersion.TryParse("0.1.0-beta.3+commit123", out var v4) && v4.Prerelease == "beta.3", "SemVersion strips build metadata");

            // 2. SemVersion comparison
            check(SemVersion.Parse("0.1.0-beta.3").CompareTo(SemVersion.Parse("0.1.0-beta.2")) > 0, "beta.3 is newer than beta.2");
            check(SemVersion.Parse("0.1.0-beta.3").CompareTo(SemVersion.Parse("0.1.0-beta.4")) < 0, "beta.4 is newer than beta.3");
            check(SemVersion.Parse("0.1.0-beta.10").CompareTo(SemVersion.Parse("0.1.0-beta.2")) > 0, "beta.10 is newer than beta.2 numerically");
            check(SemVersion.Parse("0.1.0").CompareTo(SemVersion.Parse("0.1.0-beta.3")) > 0, "final release is newer than prerelease");
            check(SemVersion.Parse("0.2.0").CompareTo(SemVersion.Parse("0.1.9")) > 0, "minor bump is newer than patch bump");
            check(SemVersion.Parse("1.0.0").CompareTo(SemVersion.Parse("0.9.9")) > 0, "major bump is newer");
            check(SemVersion.Parse("0.1.0-beta.3").CompareTo(SemVersion.Parse("0.1.0-beta.3")) == 0, "identical versions are equal");

            // 3. JSON parsing and tag isolation
            string sampleJson = @"[
                {
                    ""tag_name"": ""nekochat-lite-v0.2.0"",
                    ""name"": ""NekoChat Lite 0.2.0"",
                    ""draft"": false,
                    ""html_url"": ""https://github.com/yuipan0611/whats-up-my-neighbor-ai-input-windows/releases/tag/nekochat-lite-v0.2.0"",
                    ""assets"": [{ ""name"": ""NekoChat-Setup-win-x64.exe"", ""browser_download_url"": ""https://example.com/neko.exe"" }]
                },
                {
                    ""tag_name"": ""v0.1.0-beta.4"",
                    ""name"": ""What's up my neighbor 0.1.0-beta.4"",
                    ""draft"": false,
                    ""body"": ""新版改善發布"",
                    ""html_url"": ""https://github.com/yuipan0611/whats-up-my-neighbor-ai-input-windows/releases/tag/v0.1.0-beta.4"",
                    ""assets"": [{ ""name"": ""Neighbor-Setup-0.1.0-beta.4-x64.exe"", ""browser_download_url"": ""https://example.com/neighbor.exe"" }]
                },
                {
                    ""tag_name"": ""v0.1.0-beta.3"",
                    ""name"": ""What's up my neighbor 0.1.0-beta.3"",
                    ""draft"": false
                },
                {
                    ""tag_name"": ""v0.9.0"",
                    ""draft"": true
                }
            ]";

            // Check Neighbor app matching with prefix "v"
            var neighborUpdate = UpdateChecker.ParseReleasesJson(sampleJson, SemVersion.Parse("0.1.0-beta.3"), "v");
            check(neighborUpdate.Success && neighborUpdate.HasUpdate, "Update detected when newer beta exists");
            check(neighborUpdate.LatestVersion == "0.1.0-beta.4", "Latest version extracted correctly");
            check(neighborUpdate.DownloadUrl == "https://example.com/neighbor.exe", "Installer asset URL extracted");
            check(!neighborUpdate.LatestVersion.Contains("nekochat"), "NekoChat Lite releases ignored for Neighbor app");

            // Already on beta.4 -> No update
            var noUpdate = UpdateChecker.ParseReleasesJson(sampleJson, SemVersion.Parse("0.1.0-beta.4"), "v");
            check(noUpdate.Success && !noUpdate.HasUpdate, "No update when already at latest version");

            // Check NekoChat Lite matching with prefix "nekochat-lite-v"
            var liteUpdate = UpdateChecker.ParseReleasesJson(sampleJson, SemVersion.Parse("0.1.0-beta.2"), "nekochat-lite-v");
            check(liteUpdate.Success && liteUpdate.HasUpdate, "Lite update detected");
            check(liteUpdate.LatestVersion == "0.2.0", "Lite extracted 0.2.0");
            check(liteUpdate.DownloadUrl == "https://example.com/neko.exe", "Lite installer asset extracted");

            // Malformed JSON handling
            var malformed = UpdateChecker.ParseReleasesJson("{invalid json", SemVersion.Parse("0.1.0"), "v");
            check(!malformed.Success && malformed.ErrorMessage != null, "Malformed JSON fails gracefully");

            // 4. Mock HTTP check
            RunHttpMockTests(check).GetAwaiter().GetResult();
        }

        private static async Task RunHttpMockTests(Action<bool, string> check)
        {
            var handler = new MockHttpMessageHandler((req, token) =>
            {
                check(req.Headers.UserAgent.ToString().StartsWith("WhatsUpMyNeighbor/"), "User-Agent header sent");
                check(req.Headers.Accept.ToString().Contains("application/vnd.github.v3+json"), "GitHub API accept header sent");
                var response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(@"[{""tag_name"":""v0.2.0"",""name"":""New Release"",""draft"":false,""html_url"":""https://example.com/rel""}]", Encoding.UTF8, "application/json")
                };
                return Task.FromResult(response);
            });

            using (var checker = new UpdateChecker("https://example.com/releases", handler))
            {
                var res = await checker.CheckForUpdatesAsync("0.1.0-beta.3", "v");
                check(res.Success && res.HasUpdate && res.LatestVersion == "0.2.0", "Mock HTTP update check succeeded");
            }

            var rateLimitedHandler = new MockHttpMessageHandler((req, token) =>
            {
                return Task.FromResult(new HttpResponseMessage((HttpStatusCode)403));
            });

            using (var checker = new UpdateChecker("https://example.com/releases", rateLimitedHandler))
            {
                var res = await checker.CheckForUpdatesAsync("0.1.0-beta.3", "v");
                check(!res.Success && res.ErrorMessage.Contains("上限"), "HTTP 403 rate limit handled gracefully");
            }
        }

        private sealed class MockHttpMessageHandler : HttpMessageHandler
        {
            private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler;
            public MockHttpMessageHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
            {
                this.handler = handler;
            }
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                return handler(request, cancellationToken);
            }
        }
    }
}
