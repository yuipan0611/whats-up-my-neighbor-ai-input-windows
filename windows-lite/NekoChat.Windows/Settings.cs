using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using NekoChat.Core.Storage;

namespace NekoChat.Windows;

public sealed record ContactItem(string Ip, string Name);

internal enum SettingsSourceStatus { Missing, Loaded, Invalid, Unavailable }
internal sealed record SettingsLoadResult(Settings Value, SettingsSourceStatus Status)
{
    internal bool CanImportLegacyRoster => Status is SettingsSourceStatus.Missing or SettingsSourceStatus.Loaded;
    internal LegacyRosterImportResult? ImportLegacyRoster(IChatStore store) => CanImportLegacyRoster
        ? store.ImportLegacyRoster(Value.Contacts.Select(c => new LegacyRosterContact(c.Name, c.Ip)).ToArray()) : null;
}

internal record Settings
{
    public bool ClassRosterInitialized { get; init; }
    public string Coat { get; init; } = "original";
    public bool AutoStartOnLogin { get; init; } = true;
    public bool WanderEnabled { get; init; } = true;
    public string LastRecipientIp { get; init; } = "";
    public List<ContactItem> Contacts { get; init; } = new();
    public string TeacherIp { get; init; } = NekoChat.Core.ClassroomReactions.DefaultTeacherIp;
    public string ReactionDisplayDevice { get; init; } = "";
    public bool ShowReactionPreviews { get; init; } = true;
    public bool ReactionPreviewExplicitChoice { get; init; }

    // Legacy fields kept for seamless JSON compatibility
    public bool Lan { get; init; } = true;
    public string LanEndpoint { get; init; } = "ws://127.0.0.1:8765/nekochat/v1/ws";
    public string VpsEndpoint { get; init; } = "wss://relay.example.invalid/nekochat/v1/ws";
    public string User { get; init; } = "";
    public bool VoiceSetupSkipped { get; init; } = true;

    internal string Endpoint => Lan ? LanEndpoint : VpsEndpoint;
    internal static readonly string DirectoryPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NekoChat.WindowsLite");
    private static string FilePath => Path.Combine(DirectoryPath, "settings.json");
    internal string? PersistencePath { get; init; }
    internal bool ReadOnlyFallback { get; init; }

    internal static Settings ForSmoke(string outputDirectory) => new() {
        AutoStartOnLogin = false, PersistencePath = Path.Combine(Path.GetFullPath(outputDirectory), "settings-fixture-" + Guid.NewGuid().ToString("N") + ".json")
    };

    internal static Settings ForUiTest(string dataDirectory) => new() {
        AutoStartOnLogin = false, PersistencePath = Path.Combine(Path.GetFullPath(dataDirectory), "settings.json")
    };

    internal static Settings Load() => LoadWithStatus().Value;

    internal static SettingsLoadResult LoadWithStatus(string? filePath = null)
    {
        string path = Path.GetFullPath(filePath ?? FilePath);
        CleanStaleTemporaryFiles(path);
        SettingsLoadResult Failed(SettingsSourceStatus status) => new(new Settings { AutoStartOnLogin = false, PersistencePath = path, ReadOnlyFallback = true }, status);
        try
        {
            using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (input.Length > 1048576) return Failed(SettingsSourceStatus.Invalid);
            using var bytes = new MemoryStream(); byte[] buffer = new byte[8192]; int read;
            while ((read = input.Read(buffer)) != 0)
            { if (bytes.Length + read > 1048576) return Failed(SettingsSourceStatus.Invalid); bytes.Write(buffer, 0, read); }
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            bytes.Position = 0; using var text = new StreamReader(bytes, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            var value = JsonSerializer.Deserialize<Settings>(text.ReadToEnd(), options);
            if (value is null || value.Contacts is null || value.Contacts.Any(c => c is null || c.Ip is null || c.Name is null) ||
                value.Coat is null || value.LastRecipientIp is null || value.TeacherIp is null || value.ReactionDisplayDevice is null ||
                value.LanEndpoint is null || value.VpsEndpoint is null || value.User is null) return Failed(SettingsSourceStatus.Invalid);
            // Read-only normalization: do not rewrite a legacy settings file during load.
            // The next successful ordinary save persists it; only a visible toggle click sets explicit choice.
            return new(value with { PersistencePath = path,
                ShowReactionPreviews = NekoChat.Core.ReactionPreviewPolicy.AfterLoad(value.ShowReactionPreviews, value.ReactionPreviewExplicitChoice)
            }, SettingsSourceStatus.Loaded);
        }
        catch (FileNotFoundException) { return new(new Settings { PersistencePath = path }, SettingsSourceStatus.Missing); }
        catch (DirectoryNotFoundException) { return new(new Settings { PersistencePath = path }, SettingsSourceStatus.Missing); }
        catch (JsonException) { return Failed(SettingsSourceStatus.Invalid); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return Failed(SettingsSourceStatus.Unavailable); }
        // Anything else (serializer or decoder surprises such as NotSupported/InvalidOperation/Argument/Format) means the content cannot be used.
        // It must never crash startup; keep the original file untouched and fall back read-only like any other invalid source.
        catch (Exception ex) when (ex is not (OutOfMemoryException or StackOverflowException or AccessViolationException)) { return Failed(SettingsSourceStatus.Invalid); }
    }

    /// <summary>Fixed, content-free reason codes: "read-only-fallback", "too-large", "io", "invalid". Null after a successful save.</summary>
    internal static string? LastSaveProblem { get { lock (ProblemGate) return _lastSaveProblem; } }
    private static readonly object ProblemGate = new();
    private static string? _lastSaveProblem;
    /// <summary>Raised on every failed <see cref="Save"/> so the UI can show a persistent "settings are not being saved" notice. Handlers must not throw.</summary>
    internal static event Action<string>? SaveProblem;
    /// <summary>Non-null while this instance can never be saved (unreadable or invalid source). A window can show it permanently, not just once at startup.</summary>
    internal string? StatusNote => ReadOnlyFallback ? "設定檔無法讀取，原檔已保留；目前的變更不會儲存。" : null;
    private static bool SaveFailed(string reason)
    {
        lock (ProblemGate) _lastSaveProblem = reason;
        try { SaveProblem?.Invoke(reason); } catch { }
        return false;
    }

    /// <summary>Deletes leftover "settings.json.&lt;32 hex&gt;.tmp" files from saves that were killed mid-write. Only regular files, only that exact name shape, only if older than a minute (a concurrent instance's live temp file is younger). Best effort.</summary>
    internal static int CleanStaleTemporaryFiles(string settingsPath, TimeSpan? olderThan = null)
    {
        int removed = 0;
        try
        {
            string full = Path.GetFullPath(settingsPath), directory = Path.GetDirectoryName(full) ?? "", prefix = Path.GetFileName(full) + ".";
            if (directory.Length == 0 || !Directory.Exists(directory)) return 0;
            var limit = DateTime.UtcNow - (olderThan ?? TimeSpan.FromMinutes(1));
            foreach (string file in Directory.EnumerateFiles(directory, prefix + "*.tmp"))
            {
                string name = Path.GetFileName(file);
                if (name.Length != prefix.Length + 32 + 4 || !name.AsSpan(prefix.Length, 32).ToString().All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f'))) continue;
                try
                {
                    var attributes = File.GetAttributes(file);
                    if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0 || File.GetLastWriteTimeUtc(file) > limit) continue;
                    File.Delete(file); removed++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { }
        return removed;
    }

    internal bool Save()
    {
        if (ReadOnlyFallback) return SaveFailed("read-only-fallback");
        string path = PersistencePath ?? FilePath, temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp"; bool owned = false;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var options = new JsonSerializerOptions { WriteIndented = true };
            byte[] data = JsonSerializer.SerializeToUtf8Bytes(this, options);
            if (data.Length > 1048576) return SaveFailed("too-large");
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { owned = true; output.Write(data); output.Flush(flushToDisk: true); }
            File.Move(temporary, path, true);
            lock (ProblemGate) _lastSaveProblem = null;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return SaveFailed("io"); }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or ArgumentException or InvalidOperationException) { return SaveFailed("invalid"); }
        finally { try { if (owned) File.Delete(temporary); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } }
    }

    internal string ResolveDisplayName(string ip)
    {
        var trimmed = ip?.Trim() ?? "";
        var match = Contacts.FirstOrDefault(c => string.Equals(c.Ip.Trim(), trimmed, StringComparison.OrdinalIgnoreCase));
        return string.IsNullOrWhiteSpace(match?.Name) ? trimmed : $"{match.Name} ({trimmed})";
    }

    internal Settings WithContact(string ip, string name)
    {
        var trimmedIp = ip?.Trim() ?? "";
        var trimmedName = name?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(trimmedIp)) return this;

        var list = Contacts.Where(c => !string.Equals(c.Ip.Trim(), trimmedIp, StringComparison.OrdinalIgnoreCase)).ToList();
        if (!string.IsNullOrWhiteSpace(trimmedName))
        {
            list.Add(new ContactItem(trimmedIp, trimmedName));
        }
        return this with { Contacts = list };
    }
}
