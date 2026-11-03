using System;
using System.IO;
using NekoChat.Core;
using NekoChat.Windows;

namespace NekoChat.Tests;

public static class UiTestDataChecks
{
    public static void Run()
    {
        int checks = 0;
        void Check(bool cond, string name)
        {
            checks++;
            if (!cond) throw new InvalidOperationException($"Check failed: {name}");
        }

        void Expect<TEx>(Action action, string name) where TEx : Exception
        {
            checks++;
            bool threw = false;
            try { action(); }
            catch (TEx) { threw = true; }
            catch (Exception ex) { throw new InvalidOperationException($"Expected {typeof(TEx).Name} for {name}, but got {ex.GetType().Name}: {ex.Message}"); }
            if (!threw) throw new InvalidOperationException($"Expected {typeof(TEx).Name} for {name}, but nothing was thrown.");
        }

        string tempBase = Path.Combine(Path.GetTempPath(), "nekochat-ui-test-check-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempBase);

        string official = Settings.DirectoryPath;
        bool officialExisted = Directory.Exists(official);
        if (!officialExisted)
        {
            Directory.CreateDirectory(official);
        }

        try
        {
            string testDir = Path.Combine(tempBase, "isolated-data");
            Directory.CreateDirectory(testDir);

            // 1. Basic parsing with default port (8765, distinct from official 8768)
            var opt1 = UiTestOptions.Parse(new[] { "--ui-test-data", testDir });
            Check(opt1 is not null, "parsed --ui-test-data");
            Check(opt1!.DataDirectory == Path.GetFullPath(testDir), "data directory matches normalized path");
            Check(UiTestOptions.DefaultUiTestPort == 8765, "DefaultUiTestPort is 8765");
            Check(opt1.Port == 8765, "default port is 8765 (not official 8768)");
            Check(opt1.SettingsFilePath == Path.Combine(Path.GetFullPath(testDir), "settings.json"), "settings path has fixed name");
            Check(opt1.DatabaseDirectory == Path.Combine(Path.GetFullPath(testDir), "data"), "database directory has fixed name");
            Check(opt1.DatabaseFilePath == Path.Combine(Path.GetFullPath(testDir), "data", "nekochat.db"), "database file has fixed name");

            // 2. Custom port parsing in different argument orders
            var opt2 = UiTestOptions.Parse(new[] { "--ui-test-data", testDir, "--ui-test-port", "8799" });
            Check(opt2 is not null && opt2.Port == 8799, "parsed --ui-test-port 8799");

            var opt3 = UiTestOptions.Parse(new[] { "--ui-test-port", "8766", "--ui-test-data", testDir });
            Check(opt3 is not null && opt3.Port == 8766, "parsed reversed order arguments");

            // 3. Argument validation and error handling
            Expect<ArgumentException>(() => UiTestOptions.Parse(new[] { "--ui-test-port", "8766" }), "port without data dir rejected");
            Expect<ArgumentException>(() => UiTestOptions.Parse(new[] { "--ui-test-data" }), "missing data directory value rejected");
            Expect<ArgumentException>(() => UiTestOptions.Parse(new[] { "--ui-test-data", "   " }), "whitespace data directory rejected");
            Expect<ArgumentException>(() => UiTestOptions.Parse(new[] { "--ui-test-data", testDir, "--ui-test-port" }), "missing port value rejected");
            Expect<ArgumentException>(() => UiTestOptions.Parse(new[] { "--ui-test-data", testDir, "--ui-test-port", "not-a-number" }), "non-numeric port rejected");
            Expect<ArgumentException>(() => UiTestOptions.Parse(new[] { "--ui-test-data", testDir, "--ui-test-port", "0" }), "port 0 rejected");
            Expect<ArgumentException>(() => UiTestOptions.Parse(new[] { "--ui-test-data", testDir, "--ui-test-port", "65536" }), "port 65536 rejected");
            Expect<ArgumentException>(() => UiTestOptions.Parse(new[] { "--ui-test-data", testDir, "--ui-test-data", testDir }), "duplicate --ui-test-data rejected");
            Expect<ArgumentException>(() => UiTestOptions.Parse(new[] { "--ui-test-data", testDir, "--ui-test-port", "8765", "--ui-test-port", "8766" }), "duplicate --ui-test-port rejected");
            Expect<ArgumentException>(() => UiTestOptions.Parse(new[] { "--ui-test-data", testDir, "--unknown-flag" }), "unrecognized flag with ui-test rejected");

            // Ordinary args or smoke args should return null without throwing
            Check(UiTestOptions.Parse(Array.Empty<string>()) is null, "empty args returns null");
            Check(UiTestOptions.Parse(new[] { "--startup-smoke-test", testDir }) is null, "smoke args return null");

            // 4. Extended prefix rejection and '~' directory permission
            Expect<InvalidOperationException>(() => UiTestOptions.Parse(new[] { "--ui-test-data", @"\\?\C:\some\dir" }), "extended-length prefix \\\\?\\ rejected");
            Expect<InvalidOperationException>(() => UiTestOptions.Parse(new[] { "--ui-test-data", @"//?/C:/some/dir" }), "extended-length prefix //?/ rejected");
            string tildeDir = Path.Combine(tempBase, "data~with~tilde");
            Directory.CreateDirectory(tildeDir);
            var optTilde = UiTestOptions.Parse(new[] { "--ui-test-data", tildeDir });
            Check(optTilde is not null, "directory containing '~' is permitted (global reject removed)");

            if (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows))
            {
                string shortPath = @"C:\PROGRA~1";
                if (Directory.Exists(shortPath))
                {
                    string expanded = UiTestOptions.ResolveRealPath(shortPath);
                    Check(!expanded.Contains('~'), "Windows 8.3 short name PROGRA~1 expanded without tilde");
                }
            }

            // Dangling symlink rejection (fail-closed)
            string nonExistentTarget = Path.Combine(tempBase, "does-not-exist-" + Guid.NewGuid().ToString("N"));
            string danglingSymlink = Path.Combine(tempBase, "dangling-link");
            Directory.CreateSymbolicLink(danglingSymlink, nonExistentTarget);
            Expect<InvalidOperationException>(() => UiTestOptions.Parse(new[] { "--ui-test-data", danglingSymlink }), "dangling symlink rejected");

            // Symlink loop rejection (fail-closed)
            string loopA = Path.Combine(tempBase, "loop-a");
            string loopB = Path.Combine(tempBase, "loop-b");
            Directory.CreateSymbolicLink(loopA, loopB);
            Directory.CreateSymbolicLink(loopB, loopA);
            Expect<InvalidOperationException>(() => UiTestOptions.Parse(new[] { "--ui-test-data", loopA }), "symlink loop rejected");

            // 5. Safety guard: rejection if located inside official directory
            Expect<InvalidOperationException>(() => UiTestOptions.Parse(new[] { "--ui-test-data", official }), "exact official directory rejected");
            Expect<InvalidOperationException>(() => UiTestOptions.Parse(new[] { "--ui-test-data", Path.Combine(official, "subfolder") }), "subfolder of official directory rejected");
            Expect<InvalidOperationException>(() => UiTestOptions.Parse(new[] { "--ui-test-data", Path.Combine(official, "a", "b", "c") }), "nested subfolder of official directory rejected");
            Expect<InvalidOperationException>(() => UiTestOptions.Parse(new[] { "--ui-test-data", official.ToLowerInvariant() }), "case-insensitive official directory rejected");
            Expect<InvalidOperationException>(() => UiTestOptions.Parse(new[] { "--ui-test-data", Path.Combine(official, "..", Path.GetFileName(official)) }), "relative traversal reaching official directory rejected");

            // Similar prefix but NOT inside official directory must be permitted
            string prefixSibling = official.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + "-custom";
            Check(!UiTestOptions.IsInsideOrEqual(prefixSibling, official), "sibling with common prefix is not inside official");
            var optSibling = UiTestOptions.Parse(new[] { "--ui-test-data", prefixSibling });
            Check(optSibling is not null, "sibling directory permitted");

            // 6. Symlink bypass prevention (all three cases requested by Ben)
            // Case 1: Symlink itself points directly to official directory
            string symlinkToOfficial = Path.Combine(tempBase, "symlink-to-official");
            Directory.CreateSymbolicLink(symlinkToOfficial, official);
            Expect<InvalidOperationException>(() => UiTestOptions.Parse(new[] { "--ui-test-data", symlinkToOfficial }), "symlink to official directory rejected");

            // Case 2: Subfolder under symlink to official directory
            string subUnderSymlink = Path.Combine(symlinkToOfficial, "sub-data");
            Expect<InvalidOperationException>(() => UiTestOptions.Parse(new[] { "--ui-test-data", subUnderSymlink }), "subfolder under symlink to official directory rejected");

            // Case 3: Parent directory is a symlink pointing to parent of official directory
            string officialParent = Path.GetDirectoryName(official)!;
            string symlinkToParent = Path.Combine(tempBase, "symlink-to-parent");
            Directory.CreateSymbolicLink(symlinkToParent, officialParent);
            string viaParent = Path.Combine(symlinkToParent, Path.GetFileName(official));
            Expect<InvalidOperationException>(() => UiTestOptions.Parse(new[] { "--ui-test-data", viaParent }), "path traversing through parent symlink to official rejected");
            Expect<InvalidOperationException>(() => UiTestOptions.Parse(new[] { "--ui-test-data", Path.Combine(viaParent, "deep", "nested") }), "subfolder traversing through parent symlink to official rejected");

            // Case 4: Chained symlink (symlinkChain1 -> symlinkChain2 -> official)
            string chain2 = Path.Combine(tempBase, "symlink-chain-2");
            Directory.CreateSymbolicLink(chain2, official);
            string chain1 = Path.Combine(tempBase, "symlink-chain-1");
            Directory.CreateSymbolicLink(chain1, chain2);
            Expect<InvalidOperationException>(() => UiTestOptions.Parse(new[] { "--ui-test-data", chain1 }), "chained symlink to official directory rejected");
            Expect<InvalidOperationException>(() => UiTestOptions.Parse(new[] { "--ui-test-data", Path.Combine(chain1, "nested-sub") }), "subfolder under chained symlink to official directory rejected");

            // Permitted safe symlink pointing outside official directory
            string safeTarget = Path.Combine(tempBase, "safe-target");
            Directory.CreateDirectory(safeTarget);
            string safeLink = Path.Combine(tempBase, "safe-link");
            Directory.CreateSymbolicLink(safeLink, safeTarget);
            var optSafe = UiTestOptions.Parse(new[] { "--ui-test-data", safeLink });
            Check(optSafe is not null, "safe symlink permitted");

            // Note on Windows junction:
            Console.WriteLine("Windows junction checks: not supported on macOS host, marked UNEXECUTED.");

            // 7. Mutex derivation and isolation
            string mutex1 = UiTestOptions.DeriveMutexName(testDir);
            Check(mutex1.StartsWith("Local\\NekoChat.WindowsLite.UiTest.", StringComparison.Ordinal), "mutex format starts with expected prefix");
            Check(mutex1 != "Local\\NekoChat.WindowsLite.App", "mutex does not collide with official app mutex");

            // Deterministic and case/separator invariant
            string mutexCase = UiTestOptions.DeriveMutexName(testDir.ToUpperInvariant());
            string mutexSlash = UiTestOptions.DeriveMutexName(testDir + Path.DirectorySeparatorChar);
            Check(mutex1 == mutexCase, "mutex name is case invariant");
            Check(mutex1 == mutexSlash, "mutex name is trailing separator invariant");

            // Distinct directory produces distinct mutex
            string testDir2 = Path.Combine(tempBase, "isolated-data-2");
            string mutex2 = UiTestOptions.DeriveMutexName(testDir2);
            Check(mutex1 != mutex2, "different directories produce distinct mutexes");

            // 8. Fixed filenames without Guid
            var uiSettings = Settings.ForUiTest(testDir);
            Check(uiSettings.PersistencePath == Path.Combine(Path.GetFullPath(testDir), "settings.json"), "persistence path is settings.json");
            Check(!uiSettings.AutoStartOnLogin, "AutoStartOnLogin is disabled for ui-test");
            Check(uiSettings.PersistencePath is not null && !uiSettings.PersistencePath.Contains("fixture", StringComparison.OrdinalIgnoreCase), "no fixture prefix");
            Check(uiSettings.PersistencePath is not null && !Guid.TryParse(Path.GetFileNameWithoutExtension(uiSettings.PersistencePath), out _), "no Guid in filename");

            // Verify settings load and save to fixed filename
            Check(uiSettings.Save(), "settings saved to fixed path");
            Check(uiSettings.PersistencePath is not null && File.Exists(uiSettings.PersistencePath), "settings.json exists on disk");
            var loaded = Settings.LoadWithStatus(uiSettings.PersistencePath);
            Check(loaded.Status == SettingsSourceStatus.Loaded, "settings loaded from fixed path");

            // Verify RootDirectory resolution for fixed settings.json
            string root = UiTestOptions.ResolveRootDirectory(uiSettings, isolated: true);
            Check(root == Path.GetFullPath(testDir), "RootDirectory resolves directly to testDir without extra settings-data level");
            Check(Path.Combine(root, "data", "nekochat.db") == Path.Combine(Path.GetFullPath(testDir), "data", "nekochat.db"), "data file path is fixed");

            Console.WriteLine($"UI test data entry checks: {checks} checks passed.");
        }
        finally
        {
            try { Directory.Delete(tempBase, recursive: true); } catch { }
            if (!officialExisted)
            {
                try { Directory.Delete(official, recursive: true); } catch { }
            }
        }
    }
}
