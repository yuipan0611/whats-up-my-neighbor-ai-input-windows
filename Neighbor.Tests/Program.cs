using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace Neighbor.Tests
{
    internal static class Program
    {
        private static int passed, failed;
        private static readonly object gate = new object();
        [STAThread]
        private static int Main(string[] args)
        {
            if (PlatformTests.TryRunTarget(args)) return 0;
            bool interactive = Array.IndexOf(args, "--interactive") >= 0;
            string directory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "test-results");
            Directory.CreateDirectory(directory);
            var original = Console.Out;
            using (var report = new StreamWriter(Path.Combine(directory, interactive ? "ui-tests.txt" : "offline-tests.txt"), false, new UTF8Encoding(false)))
            {
                report.AutoFlush = true;
                Console.SetOut(new TeeWriter(original, report));
                try
                {
                    Console.WriteLine("Neighbor Windows verification · " + DateTime.UtcNow.ToString("u"));
                    Console.WriteLine("OS: " + Environment.OSVersion + "; CLR: " + Environment.Version + "; x64: " + Environment.Is64BitProcess);
                    Console.WriteLine("Synthetic fixtures only. No real microphone, user key, or Groq API request.");
                    if (interactive)
                    {
                        Application.EnableVisualStyles();
                        RunSuite("Platform integration", () => PlatformTests.RunInteractive(Check));
                        RunSuite("UI", () => UiTests.Run(Check));
                        RunSuite("UI screenshots", () => UiTests.SaveScreenshots(Path.Combine(directory, "ui-screenshots")));
                    }
                    else
                    {
                        RunSuite("Text and vocabulary", () => TextVocabularyTests.Run(Check));
                        RunSuite("Coordinator", () => CoordinatorTests.RunAsync(Check).GetAwaiter().GetResult());
                        RunSuite("Groq", () => GroqTests.RunAsync(Check).GetAwaiter().GetResult());
                        RunSuite("Platform", () => PlatformTests.Run(Check));
                        RunSuite("Settings", SettingsChecks);
                        RunSuite("Desktop pet", () => DesktopPetTests.Run(Check));
                        RunSuite("Layered window", () => LayeredWindowTests.Run(Check));
                        RunSuite("Recording HUD interaction", () => RecordingHudInteractionTests.Run(Check));
                        RunSuite("Recording HUD accessibility", () => RecordingHudAccessibilityTests.Run(Check));
                        RunSuite("Diagnostic log", () => DiagnosticLogTests.Run(Check));
                        RunSuite("Desktop pet controller", () => DesktopPetControllerTests.Run(Check));
                        RunSuite("Scaled cat cache", () => ScaledCatCacheTests.Run(Check));
                    }
                }
                catch (Exception error) { Check(false, error.ToString()); }
                finally { Console.WriteLine("RESULT: " + passed + " passed; " + failed + " failed."); Console.SetOut(original); }
            }
            return failed == 0 ? 0 : 1;
        }
        private static void RunSuite(string name, Action run)
        {
            try { run(); } catch (Exception error) { Check(false, name + ": " + error); }
        }
        private static void Check(bool success, string name)
        {
            lock (gate) { if (success) passed++; else failed++; Console.WriteLine((success ? "PASS " : "FAIL ") + name); }
        }
        private static void SettingsChecks()
        {
            string directory = Path.Combine(Path.GetTempPath(), "Neighbor.settings." + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                string path = Path.Combine(directory, "settings.json");
                var store = new Core.SettingsStore(path); var settings = store.Load();
                Check(settings.PrimaryLanguage == "zh" && !settings.LaunchAtLogin && settings.SilenceSeconds == 0, "first-run defaults do not enable startup or silence cutoff");
                settings.AlternateShortcut = new Core.Shortcut { Modifiers = 3, Key = 0x44 }; settings.SilenceSeconds = 5;
                store.Save(settings); var reloaded = new Core.SettingsStore(path).Load();
                Check(reloaded.AlternateShortcut.Key == 0x44 && reloaded.SilenceSeconds == 5, "settings persist across relaunch");
                File.WriteAllText(path, "{broken"); var corrupt = new Core.SettingsStore(path); corrupt.Load();
                bool rejected = false; try { corrupt.Save(settings); } catch (InvalidOperationException) { rejected = true; }
                Check(rejected && File.ReadAllText(path) == "{broken", "corrupt settings are preserved instead of overwritten");
                settings.PrimaryShortcut = new Core.Shortcut { Modifiers = 4, Key = 0x41 };
                bool invalid = false; try { Core.SettingsStore.Validate(settings); } catch (ArgumentException) { invalid = true; }
                Check(invalid, "shift-only shortcut cannot hijack ordinary text selection");
            }
            finally { Directory.Delete(directory, true); }
        }
        private sealed class TeeWriter : TextWriter
        {
            private readonly TextWriter first, second;
            public TeeWriter(TextWriter first, TextWriter second) { this.first = first; this.second = second; }
            public override Encoding Encoding => Encoding.UTF8;
            public override void Write(char c) { first.Write(c); second.Write(c); }
            public override void WriteLine(string s) { first.WriteLine(s); second.WriteLine(s); }
        }
    }
}
