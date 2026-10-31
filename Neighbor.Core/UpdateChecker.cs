using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace Neighbor.Core
{
    public struct SemVersion : IComparable<SemVersion>, IEquatable<SemVersion>
    {
        public int Major { get; }
        public int Minor { get; }
        public int Patch { get; }
        public string Prerelease { get; }
        public string Raw { get; }

        public SemVersion(int major, int minor, int patch, string prerelease = null, string raw = null)
        {
            Major = major;
            Minor = minor;
            Patch = patch;
            Prerelease = string.IsNullOrWhiteSpace(prerelease) ? null : prerelease.Trim();
            Raw = raw ?? string.Format("{0}.{1}.{2}{3}", major, minor, patch, string.IsNullOrEmpty(prerelease) ? "" : "-" + prerelease);
        }

        public bool IsPrerelease => !string.IsNullOrEmpty(Prerelease);

        public static bool TryParse(string input, out SemVersion version)
        {
            version = default(SemVersion);
            if (string.IsNullOrWhiteSpace(input)) return false;
            string text = input.Trim();
            if (text.StartsWith("v", StringComparison.OrdinalIgnoreCase)) text = text.Substring(1);
            int plus = text.IndexOf('+');
            if (plus >= 0) text = text.Substring(0, plus);

            string prerelease = null;
            int dash = text.IndexOf('-');
            if (dash >= 0)
            {
                prerelease = text.Substring(dash + 1);
                text = text.Substring(0, dash);
            }

            string[] parts = text.Split('.');
            if (parts.Length < 1) return false;
            int major, minor = 0, patch = 0;
            if (!int.TryParse(parts[0], out major)) return false;
            if (parts.Length > 1 && !int.TryParse(parts[1], out minor)) return false;
            if (parts.Length > 2 && !int.TryParse(parts[2], out patch)) return false;

            version = new SemVersion(major, minor, patch, prerelease, input);
            return true;
        }

        public static SemVersion Parse(string input)
        {
            SemVersion v;
            if (TryParse(input, out v)) return v;
            throw new FormatException("Invalid version string: " + input);
        }

        public int CompareTo(SemVersion other)
        {
            int c = Major.CompareTo(other.Major);
            if (c != 0) return c;
            c = Minor.CompareTo(other.Minor);
            if (c != 0) return c;
            c = Patch.CompareTo(other.Patch);
            if (c != 0) return c;

            // SemVer rule: 1.0.0 > 1.0.0-beta
            if (!IsPrerelease && other.IsPrerelease) return 1;
            if (IsPrerelease && !other.IsPrerelease) return -1;
            if (!IsPrerelease && !other.IsPrerelease) return 0;

            return ComparePrerelease(Prerelease, other.Prerelease);
        }

        private static int ComparePrerelease(string a, string b)
        {
            if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase)) return 0;
            string[] partsA = a.Split('.');
            string[] partsB = b.Split('.');
            int len = Math.Max(partsA.Length, partsB.Length);
            for (int i = 0; i < len; i++)
            {
                if (i >= partsA.Length) return -1;
                if (i >= partsB.Length) return 1;
                string segA = partsA[i];
                string segB = partsB[i];

                int valA, valB;
                bool numA = int.TryParse(segA, out valA);
                bool numB = int.TryParse(segB, out valB);

                if (numA && numB)
                {
                    int diff = valA.CompareTo(valB);
                    if (diff != 0) return diff;
                }
                else if (numA)
                {
                    return -1;
                }
                else if (numB)
                {
                    return 1;
                }
                else
                {
                    int diff = CompareAlphanumeric(segA, segB);
                    if (diff != 0) return diff;
                }
            }
            return 0;
        }

        private static int CompareAlphanumeric(string a, string b)
        {
            int iA = a.Length - 1;
            while (iA >= 0 && char.IsDigit(a[iA])) iA--;
            int iB = b.Length - 1;
            while (iB >= 0 && char.IsDigit(b[iB])) iB--;

            string prefixA = a.Substring(0, iA + 1);
            string prefixB = b.Substring(0, iB + 1);
            int cmp = string.Compare(prefixA, prefixB, StringComparison.OrdinalIgnoreCase);
            if (cmp != 0) return cmp;

            string numStrA = a.Substring(iA + 1);
            string numStrB = b.Substring(iB + 1);
            int nA, nB;
            if (int.TryParse(numStrA, out nA) && int.TryParse(numStrB, out nB))
            {
                return nA.CompareTo(nB);
            }
            return string.Compare(a, b, StringComparison.OrdinalIgnoreCase);
        }

        public bool Equals(SemVersion other) => CompareTo(other) == 0;
        public override bool Equals(object obj) => obj is SemVersion other && Equals(other);
        public override int GetHashCode() => (Major, Minor, Patch, Prerelease?.ToLowerInvariant()).GetHashCode();
        public override string ToString() => Raw;
    }

    public sealed class UpdateCheckResult
    {
        public bool Success { get; }
        public bool HasUpdate { get; }
        public string CurrentVersion { get; }
        public string LatestVersion { get; }
        public string ReleaseTitle { get; }
        public string ReleaseNotes { get; }
        public string HtmlUrl { get; }
        public string DownloadUrl { get; }
        public string ChecksumUrl { get; }
        public string ErrorMessage { get; }

        public UpdateCheckResult(
            bool success,
            bool hasUpdate,
            string currentVersion,
            string latestVersion,
            string releaseTitle,
            string releaseNotes,
            string htmlUrl,
            string downloadUrl,
            string errorMessage = null,
            string checksumUrl = null)
        {
            Success = success;
            HasUpdate = hasUpdate;
            CurrentVersion = currentVersion;
            LatestVersion = latestVersion;
            ReleaseTitle = releaseTitle;
            ReleaseNotes = releaseNotes;
            HtmlUrl = htmlUrl;
            DownloadUrl = downloadUrl;
            ChecksumUrl = checksumUrl;
            ErrorMessage = errorMessage;
        }

        public static UpdateCheckResult NoUpdate(string currentVersion, string latestVersion = null, string htmlUrl = null)
        {
            return new UpdateCheckResult(true, false, currentVersion, latestVersion ?? currentVersion, null, null, htmlUrl, null);
        }

        public static UpdateCheckResult UpdateAvailable(string currentVersion, string latestVersion, string title, string notes, string htmlUrl, string downloadUrl, string checksumUrl = null)
        {
            return new UpdateCheckResult(true, true, currentVersion, latestVersion, title, notes, htmlUrl, downloadUrl, checksumUrl: checksumUrl);
        }

        public static UpdateCheckResult Failure(string currentVersion, string errorMessage)
        {
            return new UpdateCheckResult(false, false, currentVersion, null, null, null, null, null, errorMessage);
        }
    }

    public sealed class UpdateChecker : IDisposable
    {
        public const string DefaultReleasesUrl = "https://api.github.com/repos/yuipan0611/whats-up-my-neighbor-ai-input-windows/releases";
        private readonly HttpClient client;
        private readonly string releasesUrl;

        public UpdateChecker(string releasesUrl = null, HttpMessageHandler handler = null)
        {
            this.releasesUrl = releasesUrl ?? DefaultReleasesUrl;
            client = new HttpClient(handler ?? new HttpClientHandler { AllowAutoRedirect = false });
            client.Timeout = TimeSpan.FromSeconds(15);
        }

        public async Task<UpdateCheckResult> CheckForUpdatesAsync(string currentVersionStr, string tagPrefix = "v", CancellationToken cancellation = default)
        {
            SemVersion currentVersion;
            if (!SemVersion.TryParse(currentVersionStr, out currentVersion))
            {
                return UpdateCheckResult.Failure(currentVersionStr, "無法解析目前版本號。");
            }

            try
            {
                using (var request = new HttpRequestMessage(HttpMethod.Get, releasesUrl))
                {
                    request.Headers.UserAgent.ParseAdd("WhatsUpMyNeighbor/" + currentVersionStr);
                    request.Headers.Accept.ParseAdd("application/vnd.github.v3+json");

                    using (var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation).ConfigureAwait(false))
                    {
                        if (!response.IsSuccessStatusCode)
                        {
                            return UpdateCheckResult.Failure(currentVersionStr, ErrorForStatus((int)response.StatusCode));
                        }

                        using (var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                        using (var memory = new MemoryStream())
                        {
                            var buffer = new byte[4096];
                            int read;
                            while ((read = await stream.ReadAsync(buffer, 0, buffer.Length, cancellation).ConfigureAwait(false)) != 0)
                            {
                                if (memory.Length + read > 1024 * 512)
                                    return UpdateCheckResult.Failure(currentVersionStr, "伺服器回應過大。");
                                memory.Write(buffer, 0, read);
                            }

                            string json = Encoding.UTF8.GetString(memory.ToArray());
                            return ParseReleasesJson(json, currentVersion, tagPrefix);
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                return UpdateCheckResult.Failure(currentVersionStr, "連線逾時，請確認網路連線。");
            }
            catch (HttpRequestException)
            {
                return UpdateCheckResult.Failure(currentVersionStr, "無法連線到 GitHub，請確認網路連線。");
            }
            catch (Exception ex)
            {
                return UpdateCheckResult.Failure(currentVersionStr, ex.Message);
            }
        }

        public static UpdateCheckResult ParseReleasesJson(string json, SemVersion currentVersion, string tagPrefix)
        {
            try
            {
                var serializer = new JavaScriptSerializer();
                var releases = serializer.Deserialize<object[]>(json);
                if (releases == null || releases.Length == 0)
                {
                    return UpdateCheckResult.NoUpdate(currentVersion.ToString());
                }

                SemVersion highestVersion = currentVersion;
                Dictionary<string, object> bestRelease = null;
                string bestDownloadUrl = null;
                string bestChecksumUrl = null;

                foreach (var item in releases)
                {
                    var rel = item as Dictionary<string, object>;
                    if (rel == null) continue;

                    if (rel.TryGetValue("draft", out var isDraft) && isDraft is bool draftBool && draftBool)
                        continue;

                    if (!rel.TryGetValue("tag_name", out var tagObj) || !(tagObj is string tagName))
                        continue;

                    string verText = null;
                    if (tagPrefix == "v")
                    {
                        if (tagName.StartsWith("v", StringComparison.OrdinalIgnoreCase) && !tagName.StartsWith("nekochat", StringComparison.OrdinalIgnoreCase))
                        {
                            verText = tagName.Substring(1);
                        }
                        else if (tagName.StartsWith("windows-v", StringComparison.OrdinalIgnoreCase))
                        {
                            verText = tagName.Substring(9);
                        }
                    }
                    else if (tagPrefix == "nekochat-lite-v")
                    {
                        if (tagName.StartsWith("nekochat-lite-v", StringComparison.OrdinalIgnoreCase))
                        {
                            verText = tagName.Substring("nekochat-lite-v".Length);
                        }
                    }
                    else
                    {
                        if (tagName.StartsWith(tagPrefix, StringComparison.OrdinalIgnoreCase))
                        {
                            verText = tagName.Substring(tagPrefix.Length);
                        }
                    }

                    if (string.IsNullOrEmpty(verText)) continue;

                    SemVersion parsedVer;
                    if (!SemVersion.TryParse(verText, out parsedVer)) continue;

                    if (parsedVer.CompareTo(highestVersion) > 0)
                    {
                        highestVersion = parsedVer;
                        bestRelease = rel;

                        bestDownloadUrl = null;
                        bestChecksumUrl = null;
                        string expectedName = tagPrefix == "nekochat-lite-v" ? "NekoChat-Setup-win-x64.exe" : "Neighbor-Setup-" + verText + "-x64.exe";
                        if (rel.TryGetValue("assets", out var assetsObj) && assetsObj is System.Collections.IEnumerable assets)
                        {
                            foreach (var itemAsset in assets)
                            {
                                var asset = itemAsset as Dictionary<string, object>;
                                if (asset == null || !asset.TryGetValue("name", out var n) || !(n is string name) ||
                                    !asset.TryGetValue("browser_download_url", out var u) || !(u is string assetUrl)) continue;
                                if (name.Equals(expectedName, StringComparison.OrdinalIgnoreCase)) bestDownloadUrl = assetUrl;
                                if (name.Equals(expectedName + ".sha256", StringComparison.OrdinalIgnoreCase)) bestChecksumUrl = assetUrl;
                            }
                        }
                    }
                }

                if (bestRelease != null && highestVersion.CompareTo(currentVersion) > 0)
                {
                    string title = bestRelease.TryGetValue("name", out var t) && t is string titleStr ? titleStr : null;
                    string body = bestRelease.TryGetValue("body", out var b) && b is string bodyStr ? bodyStr : null;
                    string htmlUrl = bestRelease.TryGetValue("html_url", out var u) && u is string urlStr ? urlStr : null;

                    return UpdateCheckResult.UpdateAvailable(
                        currentVersion.ToString(),
                        highestVersion.ToString(),
                        title,
                        body,
                        htmlUrl,
                        bestDownloadUrl,
                        bestChecksumUrl
                    );
                }

                return UpdateCheckResult.NoUpdate(currentVersion.ToString(), highestVersion.ToString());
            }
            catch (Exception ex)
            {
                return UpdateCheckResult.Failure(currentVersion.ToString(), "解析版本資訊失敗：" + ex.Message);
            }
        }

        private static string ErrorForStatus(int status)
        {
            switch (status)
            {
                case 403: return "GitHub 請求次數已達上限，請稍候再試。";
                case 404: return "找不到版本發布頁面。";
                default: return "GitHub 伺服器回應錯誤（HTTP " + status + "）。";
            }
        }

        public void Dispose()
        {
            client.Dispose();
        }
    }
}
