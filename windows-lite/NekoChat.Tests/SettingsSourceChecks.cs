using System.Text;
using System.Text.Json;
using NekoChat.Core.Storage;
using NekoChat.Windows;

namespace NekoChat.Tests;

internal static class SettingsSourceChecks
{
    public static int Run()
    {
        int checks = 0;
        void Check(bool ok, string name) { checks++; if (!ok) throw new Exception("Settings source: " + name); }
        string root = Path.Combine(Path.GetTempPath(), "neko-settings-source-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            string path = Path.Combine(root, "settings.json");
            var missing = Settings.LoadWithStatus(path);
            Check(missing.Status == SettingsSourceStatus.Missing && missing.CanImportLegacyRoster && !File.Exists(path), "missing source is explicitly empty without creating file");
            using (var store = ChatStore.Open(new(Path.Combine(root, "missing-db"))))
                Check(missing.ImportLegacyRoster(store) is { Added: 0, Skipped: 0 } && store.LegacyRosterImportCompleted(), "actual missing source can commit the one-time empty import");
            var edited = missing.Value.WithContact("10.0.0.1", "測試同學");
            Check(edited.Save() && Settings.LoadWithStatus(path) is { Status: SettingsSourceStatus.Loaded, Value.Contacts.Count: 1 }, "valid save atomically creates and reloads settings");
            string json = File.ReadAllText(path);
            Check(!json.Contains("PersistencePath") && !json.Contains("ReadOnlyFallback") && Directory.GetFiles(root, "*.tmp").Length == 0, "internal path/error state absent from JSON and no owned tmp retained");
            using (var store = ChatStore.Open(new(Path.Combine(root, "valid-db"))))
            {
                var loaded = Settings.LoadWithStatus(path); var imported = loaded.ImportLegacyRoster(store); var row = store.GetRosterSnapshot().Single();
                Check(imported is { Added: 1, Skipped: 0 } && row.Entry.DisplayName == "測試同學" && row.Entry.Endpoint == "10.0.0.1" && row.Entry.PeerId is null,
                    "real loaded source maps name/IP correctly and creates no paired identity");
                Check(loaded.ImportLegacyRoster(store) is { AlreadyCompleted: true } && store.GetRosterSnapshot().Count == 1, "one-time marker preserves idempotence");
            }
            foreach (var encoding in new Encoding[] { new UTF8Encoding(true), Encoding.Unicode, Encoding.BigEndianUnicode, Encoding.UTF32 })
            {
                File.WriteAllText(path, "{\"cOnTaCtS\":[{\"Ip\":\"10.0.0.2\",\"Name\":\"中文\"}],\"LanEndpoint\":\"legacy\",\"UnknownFuture\":1}", encoding);
                var loaded = Settings.LoadWithStatus(path);
                Check(loaded is { Status: SettingsSourceStatus.Loaded, Value.Contacts.Count: 1 } && loaded.Value.Contacts[0].Name == "中文" && loaded.Value.LanEndpoint == "legacy",
                    "legacy BOM and case-insensitive fields retained: " + encoding.WebName);
            }
            foreach (string invalid in new[] { "null", "[]", "{", "{\"Contacts\":null}", "{\"Contacts\":[null]}", "{\"Contacts\":[{\"Ip\":null,\"Name\":\"A\"}]}", "{\"Contacts\":[{\"Ip\":\"10.0.0.1\",\"Name\":null}]}", "{\"TeacherIp\":null}" })
            {
                File.WriteAllText(path, invalid); byte[] original = File.ReadAllBytes(path); var failed = Settings.LoadWithStatus(path);
                using var store = ChatStore.Open(new(Path.Combine(root, "invalid-" + checks)));
                Check(failed.Status == SettingsSourceStatus.Invalid && !failed.CanImportLegacyRoster && !failed.Value.AutoStartOnLogin && failed.ImportLegacyRoster(store) is null && !store.LegacyRosterImportCompleted(),
                    "malformed source cannot become completed empty import");
                Check(!failed.Value.WithContact("10.0.0.3", "new").Save() && File.ReadAllBytes(path).AsSpan().SequenceEqual(original), "fallback edits cannot overwrite malformed original");
            }
            File.WriteAllBytes(path, new byte[1048577]); var oversized = Settings.LoadWithStatus(path);
            Check(oversized.Status == SettingsSourceStatus.Invalid && !oversized.Value.Save() && new FileInfo(path).Length == 1048577, "oversized source bounded and preserved");
            string unavailable = Path.Combine(root, "not-a-file"); Directory.CreateDirectory(unavailable); File.WriteAllText(Path.Combine(unavailable, "preserve"), "untouched");
            var blocked = Settings.LoadWithStatus(unavailable);
            using (var store = ChatStore.Open(new(Path.Combine(root, "unavailable-db"))))
                Check(blocked.Status == SettingsSourceStatus.Unavailable && blocked.ImportLegacyRoster(store) is null && !store.LegacyRosterImportCompleted() && !blocked.Value.Save() && File.ReadAllText(Path.Combine(unavailable, "preserve")) == "untouched",
                    "unreadable file target cannot be treated as missing or overwritten");
            var invalidDestination = new Settings { PersistencePath = unavailable, Contacts = [new("10.0.0.4", "Current")] };
            Check(!invalidDestination.Save() && Directory.GetFiles(root, "*.tmp").Length == 0 && Directory.Exists(unavailable), "failed rename cleans only owned temp and preserves original target");
            File.WriteAllText(path, "{\"Contacts\":[]}"); byte[] beforeOversizedSave = File.ReadAllBytes(path);
            Check(!new Settings { PersistencePath = path, LastRecipientIp = new string('x', 1048577) }.Save() && File.ReadAllBytes(path).AsSpan().SequenceEqual(beforeOversizedSave), "oversized serialization never touches current settings file");
            var empty = Settings.LoadWithStatus(path);
            using (var store = ChatStore.Open(new(Path.Combine(root, "empty-db"))))
                Check(empty.Status == SettingsSourceStatus.Loaded && empty.ImportLegacyRoster(store) is { Added: 0 } && store.LegacyRosterImportCompleted(), "valid explicitly empty list is distinct from corrupt source");
            Check(new Settings { PersistencePath = Path.Combine(root, "new-parent", "settings.json"), Coat = "black", TeacherIp = "10.0.0.9" }.Save(), "valid save can create its missing parent directory");
        }
        finally { Directory.Delete(root, true); }
        Console.WriteLine($"Settings source: {checks} actual file, legacy encoding, no-overwrite and SQLite import-marker checks."); return checks;
    }
}
