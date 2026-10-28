using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace NekoChat.Windows;

public sealed record ContactItem(string Ip, string Name);

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

    internal static Settings Load()
    {
        if (!File.Exists(FilePath)) return new();
        try
        {
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath), options) ?? new();
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            return new();
        }
    }

    internal void Save()
    {
        try
        {
            Directory.CreateDirectory(DirectoryPath);
            var options = new JsonSerializerOptions { WriteIndented = true };
            File.WriteAllText(FilePath + ".tmp", JsonSerializer.Serialize(this, options));
            File.Move(FilePath + ".tmp", FilePath, true);
        }
        catch
        {
            // Ignore temporary disk I/O errors
        }
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
