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
            Check(missing.Value.AutoUpdateEnabled, "AutoUpdateEnabled defaults to true for seamless updates");
            using (var store = ChatStore.Open(new(Path.Combine(root, "missing-db"))))
                Check(missing.ImportLegacyRoster(store) is { Added: 0, Skipped: 0 } && store.LegacyRosterImportCompleted(), "actual missing source can commit the one-time empty import");
            var edited = missing.Value.WithContact("10.0.0.1", "測試同學");
            Check(edited.Save() && Settings.LoadWithStatus(path) is { Status: SettingsSourceStatus.Loaded, Value.Contacts.Count: 1, Value.AutoUpdateEnabled: true }, "valid save atomically creates and reloads settings");
            var disabledAuto = edited with { AutoUpdateEnabled = false };
            Check(disabledAuto.Save() && Settings.LoadWithStatus(path).Value.AutoUpdateEnabled == false, "AutoUpdateEnabled persists false when toggled off");
            var reEnabledAuto = Settings.LoadWithStatus(path).Value with { AutoUpdateEnabled = true };
            Check(reEnabledAuto.Save() && Settings.LoadWithStatus(path).Value.AutoUpdateEnabled == true, "AutoUpdateEnabled persists true when toggled on");
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
            // Failed saves are visible: fixed reason codes, an event, and a persistent note on read-only instances.
            var problems = new List<string>(); Action<string> hear = problems.Add; Settings.SaveProblem += hear;
            try
            {
                File.WriteAllText(path, "{"); var broken = Settings.LoadWithStatus(path);
                Check(broken.Value.StatusNote is { Length: > 0 } && new Settings().StatusNote is null, "read-only fallback exposes a persistent status note; a normal instance has none");
                Check(!broken.Value.Save() && problems.SequenceEqual(new[] { "read-only-fallback" }) && Settings.LastSaveProblem == "read-only-fallback", "read-only save failure raises the event with a fixed reason");
                Check(!new Settings { PersistencePath = path, LastRecipientIp = new string('x', 1048577) }.Save() && Settings.LastSaveProblem == "too-large", "oversized save reports too-large");
                Check(!new Settings { PersistencePath = unavailable }.Save() && Settings.LastSaveProblem == "io", "IO failure reports io");
                File.WriteAllText(path, "{\"Contacts\":[]}");
                Check(new Settings { PersistencePath = path }.Save() && Settings.LastSaveProblem is null && problems.Count == 3, "a successful save clears the problem and raises nothing");
                Settings.SaveProblem += _ => throw new InvalidOperationException("handler bug");
                Check(!broken.Value.Save(), "a throwing handler cannot turn a failed save into a crash");
            }
            finally { Settings.SaveProblem -= hear; }
            // Unexpected exception types while loading never crash startup and never touch the file.
            foreach (var weird in new[] { "{\"Contacts\":[],\"Coat\":{\"a\":1}}", "{\"Contacts\":\"x\"}", "{\"AutoStartOnLogin\":\"maybe\"}", "\u0000\u0000", "{\"Contacts\":[1,2]}" })
            {
                File.WriteAllText(path, weird); byte[] before = File.ReadAllBytes(path); SettingsLoadResult result = default!; bool threw = false; try { result = Settings.LoadWithStatus(path); } catch (Exception) { threw = true; }
                Check(!threw && result.Status == SettingsSourceStatus.Invalid && result.Value.ReadOnlyFallback && File.ReadAllBytes(path).AsSpan().SequenceEqual(before), "unexpected content falls back read-only instead of throwing: " + weird.Replace("\0", "\\0"));
            }
            // Stale temp files left by killed saves are removed at load; fresh, foreign and linked files are not.
            string tmpRoot = Path.Combine(root, "tmp-clean"); Directory.CreateDirectory(tmpRoot); string settingsFile = Path.Combine(tmpRoot, "settings.json"); File.WriteAllText(settingsFile, "{\"Contacts\":[]}");
            string Tmp(string hex) => settingsFile + "." + hex + ".tmp";
            string stale = Tmp(new string('a', 32)), fresh = Tmp(new string('b', 32)), foreign = Tmp(new string('c', 31) + "g"), shortName = Tmp(new string('d', 31)), other = Path.Combine(tmpRoot, "other.json." + new string('e', 32) + ".tmp");
            foreach (string f2 in new[] { stale, fresh, foreign, shortName, other }) File.WriteAllText(f2, "x");
            File.SetLastWriteTimeUtc(stale, DateTime.UtcNow.AddMinutes(-5)); File.SetLastWriteTimeUtc(foreign, DateTime.UtcNow.AddMinutes(-5)); File.SetLastWriteTimeUtc(shortName, DateTime.UtcNow.AddMinutes(-5)); File.SetLastWriteTimeUtc(other, DateTime.UtcNow.AddMinutes(-5));
            string linkTarget = Path.Combine(tmpRoot, "target.bin"); File.WriteAllText(linkTarget, "keep"); string linkTmp = Tmp(new string('f', 32)); File.CreateSymbolicLink(linkTmp, linkTarget);
            var cleaned = Settings.LoadWithStatus(settingsFile);
            Check(cleaned.Status == SettingsSourceStatus.Loaded && !File.Exists(stale) && File.Exists(fresh) && File.Exists(foreign) && File.Exists(shortName) && File.Exists(other) && File.Exists(linkTarget) && File.Exists(File.ResolveLinkTarget(linkTmp, false)!.FullName),
                "load removes only old well-formed settings temp files, never fresh, malformed-name, other-file or linked ones");
            Check(Settings.CleanStaleTemporaryFiles(settingsFile, TimeSpan.Zero) >= 1 && !File.Exists(fresh) && File.Exists(linkTarget) && File.ReadAllText(settingsFile) == "{\"Contacts\":[]}", "age threshold is a parameter; the real settings file and link targets are never touched");
        }
        finally { Directory.Delete(root, true); }
        Console.WriteLine($"Settings source: {checks} actual file, legacy encoding, no-overwrite and SQLite import-marker checks."); return checks;
    }
}
