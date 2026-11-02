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
    public string Coat { get; init; } = "original";
    public bool AutoStartOnLogin { get; init; } = true;
    public bool WanderEnabled { get; init; } = true;
    public string LastRecipientIp { get; init; } = "";
    public List<ContactItem> Contacts { get; init; } = new();
    public string TeacherIp { get; init; } = NekoChat.Core.ClassroomReactions.DefaultTeacherIp;
    public string ReactionDisplayDevice { get; init; } = "";

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

    internal static Settings Load() => LoadWithStatus().Value;

    internal static SettingsLoadResult LoadWithStatus(string? filePath = null)
    {
        string path = Path.GetFullPath(filePath ?? FilePath);
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
            return new(value with { PersistencePath = path }, SettingsSourceStatus.Loaded);
        }
        catch (FileNotFoundException) { return new(new Settings { PersistencePath = path }, SettingsSourceStatus.Missing); }
        catch (DirectoryNotFoundException) { return new(new Settings { PersistencePath = path }, SettingsSourceStatus.Missing); }
        catch (JsonException) { return Failed(SettingsSourceStatus.Invalid); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return Failed(SettingsSourceStatus.Unavailable); }
    }

    internal bool Save()
    {
        if (ReadOnlyFallback) return false;
        string path = PersistencePath ?? FilePath, temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp"; bool owned = false;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var options = new JsonSerializerOptions { WriteIndented = true };
            byte[] data = JsonSerializer.SerializeToUtf8Bytes(this, options);
            if (data.Length > 1048576) return false;
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { owned = true; output.Write(data); output.Flush(flushToDisk: true); }
            File.Move(temporary, path, true); return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return false;
        }
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
