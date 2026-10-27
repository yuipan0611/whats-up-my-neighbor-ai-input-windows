using System.Text.Json;

namespace NekoChat.Core;

public readonly record struct SemVersion(int Major, int Minor, int Patch, string? Prerelease, string Raw) : IComparable<SemVersion>
{
    public bool IsPrerelease => !string.IsNullOrEmpty(Prerelease);

    public static bool TryParse(string? input, out SemVersion version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(input)) return false;
        var text = input.Trim();
        if (text.StartsWith("v", StringComparison.OrdinalIgnoreCase)) text = text[1..];
        int plus = text.IndexOf('+');
        if (plus >= 0) text = text[..plus];

        string? prerelease = null;
        int dash = text.IndexOf('-');
        if (dash >= 0)
        {
            prerelease = text[(dash + 1)..];
            text = text[..dash];
        }

        var parts = text.Split('.');
        if (parts.Length < 1) return false;
        if (!int.TryParse(parts[0], out int major)) return false;
        int minor = parts.Length > 1 && int.TryParse(parts[1], out int pMinor) ? pMinor : 0;
        int patch = parts.Length > 2 && int.TryParse(parts[2], out int pPatch) ? pPatch : 0;

        version = new SemVersion(major, minor, patch, prerelease, input);
        return true;
    }

    public static SemVersion Parse(string input) => TryParse(input, out var v) ? v : throw new FormatException("Invalid version: " + input);

    public int CompareTo(SemVersion other)
    {
        int c = Major.CompareTo(other.Major);
        if (c != 0) return c;
        c = Minor.CompareTo(other.Minor);
        if (c != 0) return c;
        c = Patch.CompareTo(other.Patch);
        if (c != 0) return c;

        if (!IsPrerelease && other.IsPrerelease) return 1;
        if (IsPrerelease && !other.IsPrerelease) return -1;
        if (!IsPrerelease && !other.IsPrerelease) return 0;

        return ComparePrerelease(Prerelease!, other.Prerelease!);
    }

    private static int ComparePrerelease(string a, string b)
    {
        if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase)) return 0;
        var partsA = a.Split('.');
        var partsB = b.Split('.');
        int len = Math.Max(partsA.Length, partsB.Length);
        for (int i = 0; i < len; i++)
        {
            if (i >= partsA.Length) return -1;
            if (i >= partsB.Length) return 1;
            string segA = partsA[i];
            string segB = partsB[i];

            bool numA = int.TryParse(segA, out int valA);
            bool numB = int.TryParse(segB, out int valB);

            if (numA && numB)
            {
                int diff = valA.CompareTo(valB);
                if (diff != 0) return diff;
            }
            else if (numA) return -1;
            else if (numB) return 1;
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

        string prefixA = a[..(iA + 1)];
        string prefixB = b[..(iB + 1)];
        int cmp = string.Compare(prefixA, prefixB, StringComparison.OrdinalIgnoreCase);
        if (cmp != 0) return cmp;

        string numStrA = a[(iA + 1)..];
        string numStrB = b[(iB + 1)..];
        if (int.TryParse(numStrA, out int nA) && int.TryParse(numStrB, out int nB))
            return nA.CompareTo(nB);

        return string.Compare(a, b, StringComparison.OrdinalIgnoreCase);
    }

    public override string ToString() => Raw;
}

public sealed record UpdateCheckResult(
    bool Success,
    bool HasUpdate,
    string CurrentVersion,
    string? LatestVersion,
    string? ReleaseTitle,
    string? ReleaseNotes,
    string? HtmlUrl,
    string? DownloadUrl,
    string? ErrorMessage = null)
{
    public static UpdateCheckResult NoUpdate(string currentVersion, string? latestVersion = null, string? htmlUrl = null) =>
        new(true, false, currentVersion, latestVersion ?? currentVersion, null, null, htmlUrl, null);

    public static UpdateCheckResult UpdateAvailable(string currentVersion, string latestVersion, string? title, string? notes, string? htmlUrl, string? downloadUrl) =>
        new(true, true, currentVersion, latestVersion, title, notes, htmlUrl, downloadUrl);

    public static UpdateCheckResult Failure(string currentVersion, string errorMessage) =>
        new(false, false, currentVersion, null, null, null, null, null, errorMessage);
}

public sealed class UpdateChecker : IDisposable
{
    public const string DefaultReleasesUrl = "https://api.github.com/repos/yuipan0611/whats-up-my-neighbor-ai-input-windows/releases";
    private readonly HttpClient client;
    private readonly string releasesUrl;

    public UpdateChecker(string? releasesUrl = null, HttpMessageHandler? handler = null)
    {
        this.releasesUrl = releasesUrl ?? DefaultReleasesUrl;
        client = new HttpClient(handler ?? new HttpClientHandler { AllowAutoRedirect = false })
        {
            Timeout = TimeSpan.FromSeconds(15)
        };
    }

    public async Task<UpdateCheckResult> CheckForUpdatesAsync(string currentVersionStr, string tagPrefix = "nekochat-lite-v", CancellationToken cancellation = default)
    {
        if (!SemVersion.TryParse(currentVersionStr, out var currentVersion))
            return UpdateCheckResult.Failure(currentVersionStr, "無法解析目前版本號。");

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, releasesUrl);
            request.Headers.UserAgent.ParseAdd("NekoChat/" + currentVersionStr);
            request.Headers.Accept.ParseAdd("application/vnd.github.v3+json");

            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return UpdateCheckResult.Failure(currentVersionStr, ErrorForStatus((int)response.StatusCode));

            using var stream = await response.Content.ReadAsStreamAsync(cancellation).ConfigureAwait(false);
            using var memory = new MemoryStream();
            var buffer = new byte[4096];
            int read;
            while ((read = await stream.ReadAsync(buffer, cancellation).ConfigureAwait(false)) != 0)
            {
                if (memory.Length + read > 1024 * 512)
                    return UpdateCheckResult.Failure(currentVersionStr, "伺服器回應過大。");
                memory.Write(buffer, 0, read);
            }

            memory.Position = 0;
            return ParseReleases(memory, currentVersion, tagPrefix);
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

    public static UpdateCheckResult ParseReleases(Stream jsonStream, SemVersion currentVersion, string tagPrefix)
    {
        try
        {
            using var doc = JsonDocument.Parse(jsonStream);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return UpdateCheckResult.Failure(currentVersion.ToString(), "回傳格式無效。");

            SemVersion highestVersion = currentVersion;
            JsonElement? bestRelease = null;
            string? bestDownloadUrl = null;

            foreach (var rel in doc.RootElement.EnumerateArray())
            {
                if (rel.TryGetProperty("draft", out var draft) && draft.GetBoolean())
                    continue;

                if (!rel.TryGetProperty("tag_name", out var tagProp) || tagProp.GetString() is not { } tagName)
                    continue;

                string? verText = null;
                if (tagPrefix == "nekochat-lite-v")
                {
                    if (tagName.StartsWith("nekochat-lite-v", StringComparison.OrdinalIgnoreCase))
                        verText = tagName["nekochat-lite-v".Length..];
                }
                else if (tagPrefix == "v")
                {
                    if (tagName.StartsWith("v", StringComparison.OrdinalIgnoreCase) && !tagName.StartsWith("nekochat", StringComparison.OrdinalIgnoreCase))
                        verText = tagName[1..];
                    else if (tagName.StartsWith("windows-v", StringComparison.OrdinalIgnoreCase))
                        verText = tagName[9..];
                }
                else
                {
                    if (tagName.StartsWith(tagPrefix, StringComparison.OrdinalIgnoreCase))
                        verText = tagName[tagPrefix.Length..];
                }

                if (string.IsNullOrEmpty(verText)) continue;
                if (!SemVersion.TryParse(verText, out var parsedVer)) continue;

                if (parsedVer.CompareTo(highestVersion) > 0)
                {
                    highestVersion = parsedVer;
                    bestRelease = rel.Clone();

                    bestDownloadUrl = null;
                    if (rel.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var asset in assets.EnumerateArray())
                        {
                            if (asset.TryGetProperty("name", out var nameProp) && nameProp.GetString() is { } name && name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                            {
                                if (asset.TryGetProperty("browser_download_url", out var dlProp))
                                {
                                    bestDownloadUrl = dlProp.GetString();
                                    break;
                                }
                            }
                        }
                    }
                }
            }

            if (bestRelease is { } best && highestVersion.CompareTo(currentVersion) > 0)
            {
                string? title = best.TryGetProperty("name", out var tp) ? tp.GetString() : null;
                string? body = best.TryGetProperty("body", out var bp) ? bp.GetString() : null;
                string? htmlUrl = best.TryGetProperty("html_url", out var up) ? up.GetString() : null;

                return UpdateCheckResult.UpdateAvailable(
                    currentVersion.ToString(),
                    highestVersion.ToString(),
                    title,
                    body,
                    htmlUrl,
                    bestDownloadUrl
                );
            }

            return UpdateCheckResult.NoUpdate(currentVersion.ToString(), highestVersion.ToString());
        }
        catch (Exception ex)
        {
            return UpdateCheckResult.Failure(currentVersion.ToString(), "解析版本資訊失敗：" + ex.Message);
        }
    }

    private static string ErrorForStatus(int status) => status switch
    {
        403 => "GitHub 請求次數已達上限，請稍候再試。",
        404 => "找不到版本發布頁面。",
        _ => "GitHub 伺服器回應錯誤（HTTP " + status + "）。"
    };

    public void Dispose() => client.Dispose();
}
