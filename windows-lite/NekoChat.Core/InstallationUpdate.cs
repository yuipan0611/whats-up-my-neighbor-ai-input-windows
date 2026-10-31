using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace NekoChat.Core;

/// <summary>Per-user replacement, rollback until launch acceptance, then bounded owned-file cleanup.</summary>
public sealed class InstallationUpdate : IDisposable
{
    private const string Product = "NekoChat.WindowsLite";
    private const string ManifestName = ".nekochat-install.json";
    private static readonly Regex BackupName = new(@"^\.nekochat-previous-\d{14}-[0-9a-f]{32}$", RegexOptions.CultureInvariant);
    private sealed record Manifest(int Format, string Product, Dictionary<string,string> Files);
    private readonly string parent, staging, previous, preservedRoot;
    private readonly Manifest package;
    private readonly FileStream gate;
    private bool moved, placedNew, finished, disposed;
    public string Destination { get; }
    public bool IsPending => !finished && !disposed;
    public sealed record CleanupResult(int PreservedFiles, IReadOnlyList<string> Warnings);

    private InstallationUpdate(string parent, string preservedRoot)
    {
        this.parent = Path.GetFullPath(parent);
        this.preservedRoot = Path.GetFullPath(preservedRoot);
        if (this.preservedRoot.StartsWith(this.parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            this.preservedRoot.Equals(this.parent, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("保留資料必須位於程式安裝目錄之外。", nameof(preservedRoot));
        Destination = Path.Combine(this.parent, Product);
        staging = Path.Combine(this.parent, ".nekochat-install-" + Guid.NewGuid().ToString("N"));
        previous = Path.Combine(this.parent, ".nekochat-previous-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss") + "-" + Guid.NewGuid().ToString("N"));
        NoLinks(this.parent); NoLinks(this.preservedRoot); NoLinks(Destination);
        Directory.CreateDirectory(this.parent);
        var gatePath = Path.Combine(this.parent, ".nekochat-upgrade.lock");
        NoLinks(gatePath);
        gate = new FileStream(gatePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        package = new Manifest(1, Product, new(StringComparer.OrdinalIgnoreCase));
    }

    public static InstallationUpdate Begin(Stream payload, string parent, string preservedRoot)
    {
        var update = new InstallationUpdate(parent, preservedRoot);
        try
        {
            if (File.Exists(update.Destination) || Directory.Exists(update.Destination) &&
                !PayloadArchive.RequiredFiles.All(p => File.Exists(Path.Combine(update.Destination, p))))
                throw new IOException("安裝位置已有無法確認的內容，尚未覆寫任何檔案。");
            PayloadArchive.Extract(payload, update.staging);
            foreach (var file in Snapshot(update.staging))
                update.package.Files.Add(file.Key, file.Value);
            if (File.Exists(Path.Combine(update.staging, ManifestName)))
                throw new InvalidDataException("安裝包使用了保留的檔案名稱。");
            WriteManifest(update.staging, update.package);
            if (Directory.Exists(update.Destination))
            {
                Directory.Move(update.Destination, update.previous);
                update.moved = true;
            }
            Directory.Move(update.staging, update.Destination);
            update.placedNew = true;
            return update;
        }
        catch { update.Dispose(); throw; }
    }

    /// <summary>Caller supplies the real launch request (or a native smoke launch in CI).</summary>
    public CleanupResult AcceptLaunch(Action<string> launch)
    {
        if (!IsPending) throw new InvalidOperationException("安裝交易已結束。");
        try
        {
            var actual = Snapshot(Destination);
            foreach (var file in package.Files)
                if (!actual.TryGetValue(file.Key, out var hash) || hash != file.Value)
                    throw new InvalidDataException("新版程式驗證失敗，將還原原版。");
            launch(Destination);
        }
        catch { Rollback(); throw; }
        finished = true; // Cleanup errors never roll back an App whose launch was accepted.
        int preserved = 0;
        var warnings = new List<string>();
        string[] backups;
        try { backups = Directory.GetDirectories(parent, ".nekochat-previous-*"); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { return new CleanupResult(0, ["新版已啟動，但無法列出舊版備份：" + parent + "（" + ex.Message + "）"]); }
        foreach (var backup in backups)
        {
            if (!BackupName.IsMatch(Path.GetFileName(backup))) continue;
            try { preserved += CleanBackup(backup); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
            { warnings.Add("舊版清理未完成，已保留剩餘檔案：" + backup + "（" + ex.Message + "）"); }
        }
        return new CleanupResult(preserved, warnings);
    }

    private int CleanBackup(string backup)
    {
        NoLinks(backup);
        var files = Snapshot(backup);
        Manifest? old = null;
        if (files.ContainsKey(ManifestName))
        {
            old = JsonSerializer.Deserialize<Manifest>(File.ReadAllText(Path.Combine(backup, ManifestName)));
            if (old is not { Format: 1, Product: Product } || old.Files is null || old.Files.Count > 4096 ||
                old.Files.Any(f => !ValidRelative(f.Key) || f.Value is null || !Regex.IsMatch(f.Value, "^[A-F0-9]{64}$")))
                throw new IOException("備份識別資料無法確認。");
            try { old = old with { Files = new(old.Files, StringComparer.OrdinalIgnoreCase) }; }
            catch (ArgumentException ex) { throw new IOException("備份識別資料重複。", ex); }
        }
        else if (!PayloadArchive.RequiredFiles.All(f => files.ContainsKey(f)))
            throw new IOException("此資料夾不具備完整的舊版程式識別。");
        var owned = new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            if (file.Key == ManifestName) continue;
            bool managed = old is not null
                ? old.Files.TryGetValue(file.Key, out var prior) && prior == file.Value
                : package.Files.TryGetValue(file.Key, out var current) &&
                  (current == file.Value || IsProgramFile(file.Key));
            if (managed) owned.Add(file.Key, file.Value);
        }
        // A legacy backup gets an explicit ownership record, so locked/partial cleanup can retry.
        if (old is null) WriteManifest(backup, new Manifest(1, Product, owned));
        int preserved = 0;
        foreach (var file in files)
        {
            if (file.Key == ManifestName || owned.ContainsKey(file.Key)) continue;
            NoLinks(preservedRoot);
            var destination = Path.Combine(preservedRoot, file.Value, file.Key.Replace('/', Path.DirectorySeparatorChar));
            NoLinks(destination);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            var source = Path.Combine(backup, file.Key.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(destination)) File.Copy(source, destination, overwrite: false);
            if (Hash(destination) != file.Value) throw new IOException("自訂檔案尚未安全保留。");
            preserved++;
        }
        // No deletion until every unowned/modified file has a verified recovery copy.
        var after = Snapshot(backup);
        if (after.Count != files.Count + (old is null ? 1 : 0) ||
            files.Any(f => f.Key != ManifestName && (!after.TryGetValue(f.Key, out var hash) || hash != f.Value)))
            throw new IOException("備份在清理前發生變更。");
        // Keep the ownership marker until all files and subdirectories are gone.
        foreach (var relative in after.Keys.Where(p => p != ManifestName))
            File.Delete(Path.Combine(backup, relative.Replace('/', Path.DirectorySeparatorChar)));
        void RemoveEmpty(string directory)
        {
            NoLinks(directory);
            foreach (var child in Directory.EnumerateDirectories(directory)) RemoveEmpty(child);
            Directory.Delete(directory, recursive: false);
        }
        foreach (var child in Directory.EnumerateDirectories(backup)) RemoveEmpty(child);
        File.Delete(Path.Combine(backup, ManifestName));
        Directory.Delete(backup, recursive: false);
        return preserved;
    }

    private static bool IsProgramFile(string path) => path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".deps.json", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".runtimeconfig.json", StringComparison.OrdinalIgnoreCase);
    private static string Hash(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
    private static bool ValidRelative(string path) => !Path.IsPathRooted(path) &&
        !path.Contains('\\') && !path.Contains(':') && path.Split('/').All(p => p.Length > 0 && p is not "." and not "..");
    private static void NoLinks(string path)
    {
        for (var item = new FileInfo(Path.GetFullPath(path)); item is not null; item = item.Directory is { } parent ? new FileInfo(parent.FullName) : null)
            if ((File.Exists(item.FullName) || Directory.Exists(item.FullName) || item.LinkTarget is not null) &&
                (File.GetAttributes(item.FullName) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("安裝或備份路徑包含連結，未執行清理。");
    }
    private static Dictionary<string,string> Snapshot(string root)
    {
        NoLinks(root);
        var files = new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        void Visit(string directory)
        {
            foreach (var path in Directory.EnumerateFileSystemEntries(directory))
            {
                NoLinks(path);
                if (Directory.Exists(path)) Visit(path);
                else
                {
                    var relative = Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');
                    if (!ValidRelative(relative)) throw new IOException("備份路徑不安全。");
                    if (!files.TryAdd(relative, Hash(path))) throw new IOException("備份包含重複的檔案路徑。");
                }
            }
        }
        Visit(root);
        return files;
    }
    private static void WriteManifest(string directory, Manifest manifest) =>
        File.WriteAllText(Path.Combine(directory, ManifestName), JsonSerializer.Serialize(manifest));

    private void Rollback()
    {
        if (finished) return;
        // Keep any failed replacement recoverable instead of deleting unexpected files.
        if (moved && Directory.Exists(previous))
        {
            if (Directory.Exists(Destination)) Directory.Move(Destination, staging);
            Directory.Move(previous, Destination);
        }
        else if (placedNew && Directory.Exists(Destination))
            Directory.Move(Destination, staging);
        finished = true;
    }
    public void Dispose()
    {
        if (disposed) return;
        try
        {
            if (!finished) Rollback();
            // Staging only contains this attempt's verified files. Unknown/modified files stay.
            if (Directory.Exists(staging))
            {
                var remaining = Snapshot(staging);
                if (remaining.All(f => f.Key == ManifestName || package.Files.TryGetValue(f.Key, out var hash) && hash == f.Value))
                    Directory.Delete(staging, recursive: true);
            }
        }
        finally { disposed = true; gate.Dispose(); }
    }
}
