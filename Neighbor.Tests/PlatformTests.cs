using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Shortcut = Neighbor.Core.Shortcut;
using Neighbor.Core;
using Neighbor.Windows.Platform;

namespace Neighbor.Tests
{
    public static class PlatformTests
    {
        private sealed class FakeRegistrar : IHotKeyRegistrar
        {
            public readonly Dictionary<int, Shortcut> Registered = new Dictionary<int, Shortcut>();
            public int RejectKey;
            public event Action<int> Pressed;
            public bool Register(int id, int modifiers, int key)
            {
                if (key == RejectKey || Registered.Values.Any(x => x.Modifiers == modifiers && x.Key == key)) return false;
                Registered.Add(id, new Shortcut { Modifiers = modifiers, Key = key }); return true;
            }
            public void Unregister(int id) { Registered.Remove(id); }
            public void Fire(int id) { Pressed?.Invoke(id); }
            public void Dispose() { }
        }
        public static void Run(Action<bool, string> check)
        {
            FixtureStartupTests.Run(check);
            var backend = new FakeRegistrar();
            using (var service = new HotKeyService(backend))
            {
                var first = new Shortcut();
                check(service.TryApply(first, null, out _), "primary hotkey registration");
                int oldId = backend.Registered.Keys.Single();
                backend.RejectKey = 0x42;
                check(!service.TryApply(new Shortcut { Key = 0x41 }, new Shortcut { Key = 0x42 }, out _), "second hotkey conflict rejects full update");
                check(backend.Registered.Count == 1 && backend.Registered.ContainsKey(oldId), "conflict rollback preserves original and removes staged key");
                backend.RejectKey = 0;
                check(service.TryApply(first, new Shortcut { Key = 0x41 }, out _), "alternate hotkey registration");
                check(service.TryApply(new Shortcut { Key = 0x41 }, first, out _), "existing hotkeys can swap without registration collision");
                int logical = 0; service.Pressed += id => logical = id; backend.Fire(oldId);
                check(logical == 2, "swapped key dispatches alternate logical id");
                check(!service.TryApply(first, first.Clone(), out _), "duplicate shortcuts rejected");
                check(!service.TryApply(new Shortcut { Modifiers = 0 }, null, out _), "unmodified shortcut rejected");
            }
            check(backend.Registered.Count == 0, "hotkeys released on dispose");
            RecorderCompletionChecks(check);
            CaptureConverterChecks(check);
            if (Environment.OSVersion.Platform != PlatformID.Win32NT) return;

            var normalizer = new WindowsTextNormalizer();
            check(normalizer.ToTraditional("头发干了，在里面的软件") == "頭髮乾了，在裡面的軟件", "NLS contextual traditional fixture");
            check(normalizer.ToTraditional("API 日本語 🐈‍⬛ 😀") == "API 日本語 🐈‍⬛ 😀", "NLS preserves other scripts and emoji");
            check(normalizer.ToTraditional("") == "", "NLS handles empty input");
            var segmenter = new WindowsWordSegmenter();
            string mixed = "違約責任 English 日本語 😀";
            var spans = segmenter.GetWords(mixed);
            check(spans.Count > 0 && spans.All(s => s.Start >= 0 && s.Length > 0 && s.Start + s.Length <= mixed.Length), "Windows word spans stay in source bounds");
            check(spans.All(s => !char.IsLowSurrogate(mixed[s.Start]) && (s.Start + s.Length == mixed.Length || !char.IsLowSurrogate(mixed[s.Start + s.Length]))), "Windows word spans preserve surrogate pairs");

            var credentials = new WindowsCredentialStore("WhatsUpMyNeighbor.Tests/" + Guid.NewGuid().ToString("N"));
            try
            {
                check(credentials.Read() == "", "isolated credential starts empty");
                credentials.Write("test-only-not-an-api-key-測試");
                check(credentials.Read() == "test-only-not-an-api-key-測試", "isolated credential unicode roundtrip");
                credentials.Write("replacement-test-only");
                check(credentials.Read() == "replacement-test-only", "credential update");
                credentials.Delete();
                check(credentials.Read() == "", "credential delete");
            }
            finally { credentials.Delete(); }
            using (var delivery = new WindowsTextDelivery())
            {
                var result = delivery.DeliverAsync("fixture-only", new FocusSnapshot { CanPaste = false }, CancellationToken.None).GetAwaiter().GetResult();
                check(result.Copied && !result.PasteSent && !result.PasteVerified, "unknown target copies without injecting keys");
                check(!delivery.RevalidateCorrectionAsync().GetAwaiter().GetResult(), "unverified paste never enables correction learning");
                var canceled = new CancellationToken(true);
                bool canceledCorrectly = false;
                try { delivery.DeliverAsync("must-not-copy", null, canceled).GetAwaiter().GetResult(); }
                catch (OperationCanceledException) { canceledCorrectly = true; }
                check(canceledCorrectly, "cancelled delivery exits before clipboard write");
            }
        }

        private static void CaptureConverterChecks(Action<bool, string> check)
        {
            // Synthetic packets only: the converter must accept the mix format that a
            // 48 kHz stereo microphone reports and still produce 16 kHz mono PCM.
            const int seconds = 1;
            var stereoFloat = NAudio.Wave.WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);
            var packets = Synthetic(stereoFloat, 48000, seconds, 1000, 0.5, 2400);
            var converter = new CaptureFormatConverter(stereoFloat);
            var output = Drain(converter, packets);
            int samples = output.Length / 2;
            check(Math.Abs(samples - 16000) <= 160, "stereo 48 kHz float converts to about one second of 16 kHz samples (" + samples + ")");
            int crossings = 0; short peak = 0; short previous = 0;
            for (int i = 0; i < samples; i++)
            {
                short value = (short)(output[i * 2] | output[i * 2 + 1] << 8);
                if (i > 0 && (previous < 0) != (value < 0)) crossings++;
                peak = (short)Math.Max(peak, Math.Abs((int)value));
                previous = value;
            }
            check(Math.Abs(crossings - 2000) <= 60, "1 kHz tone keeps its frequency after resampling (" + crossings + " crossings)");
            check(peak > 14000 && peak < 18000, "channel averaging keeps the amplitude of identical channels (peak " + peak + ")");

            // 44.1 kHz is a fractional ratio to 16 kHz and must keep the same output length and pitch.
            var mono441 = NAudio.Wave.WaveFormat.CreateIeeeFloatWaveFormat(44100, 1);
            var fractional = Drain(new CaptureFormatConverter(mono441), Synthetic(mono441, 44100, seconds, 1000, 0.5, 1000));
            int fractionalSamples = fractional.Length / 2;
            int fractionalCrossings = 0; short fractionalPrevious = 0;
            for (int i = 0; i < fractionalSamples; i++)
            {
                short value = (short)(fractional[i * 2] | fractional[i * 2 + 1] << 8);
                if (i > 0 && (fractionalPrevious < 0) != (value < 0)) fractionalCrossings++;
                fractionalPrevious = value;
            }
            check(Math.Abs(fractionalSamples - 16000) <= 160, "44.1 kHz mono converts to about one second of 16 kHz samples (" + fractionalSamples + ")");
            check(Math.Abs(fractionalCrossings - 2000) <= 60, "44.1 kHz tone keeps its frequency after resampling (" + fractionalCrossings + " crossings)");

            var monoTarget = new NAudio.Wave.WaveFormat(16000, 16, 1);
            var passthrough = new CaptureFormatConverter(monoTarget);
            var pcm = new byte[16000 * 2];
            for (int i = 0; i < pcm.Length; i++) pcm[i] = (byte)(i % 251);
            var passed = Drain(passthrough, new[] { pcm });
            check(Math.Abs(passed.Length - pcm.Length) <= 4 * 32, "16 kHz mono 16-bit input keeps its length within the filter delay (" + passed.Length + " bytes)");
        }

        private static byte[][] Synthetic(NAudio.Wave.WaveFormat format, int rate, int seconds, double hz, double amplitude, int framesPerPacket)
        {
            int frames = rate * seconds;
            var packets = new byte[(frames + framesPerPacket - 1) / framesPerPacket][];
            for (int p = 0; p < packets.Length; p++)
            {
                int count = Math.Min(framesPerPacket, frames - p * framesPerPacket);
                packets[p] = new byte[count * format.BlockAlign];
                for (int f = 0; f < count; f++)
                {
                    float value = (float)(amplitude * Math.Sin(2 * Math.PI * hz * (p * framesPerPacket + f) / rate));
                    var bytes = BitConverter.GetBytes(value);
                    for (int c = 0; c < format.Channels; c++) Array.Copy(bytes, 0, packets[p], f * format.BlockAlign + c * 4, 4);
                }
            }
            return packets;
        }

        private static byte[] Drain(CaptureFormatConverter converter, byte[][] packets)
        {
            var scratch = new byte[16000];
            var result = new System.IO.MemoryStream();
            foreach (var packet in packets)
            {
                converter.Write(packet, 0, packet.Length);
                int read;
                while ((read = converter.Read(scratch, 0, scratch.Length)) > 0) result.Write(scratch, 0, read);
            }
            return result.ToArray();
        }

        private static void RecorderCompletionChecks(Action<bool, string> check)
        {
            // Exercise the real completion path without opening a device or creating
            // audio. Regression: an old failure event used to arrive after StopAsync
            // succeeded, allowing it to interfere with a later recording job.
            var recorderType = typeof(WasapiRecorder);
            var sessionType = recorderType.GetNestedType("Session", BindingFlags.NonPublic);
            var current = recorderType.GetField("current", BindingFlags.Instance | BindingFlags.NonPublic);
            var finish = recorderType.GetMethod("Finish", BindingFlags.Instance | BindingFlags.NonPublic);
            for (int scenario = 0; scenario < 3; scenario++)
            {
                using (var recorder = new WasapiRecorder())
                {
                    var session = Activator.CreateInstance(sessionType, true);
                    sessionType.GetField("StopRequested").SetValue(session, scenario < 2);
                    current.SetValue(recorder, session);
                    var stopped = (System.Threading.Tasks.TaskCompletionSource<bool>)sessionType.GetField("Stopped").GetValue(session);
                    int notifications = 0, notifiedBeforeCompletion = 0;
                    recorder.StoppedUnexpectedly += (sender, e) =>
                    {
                        Interlocked.Increment(ref notifications);
                        if (!stopped.Task.IsCompleted) Interlocked.Exchange(ref notifiedBeforeCompletion, 1);
                    };
                    Exception original = scenario == 1 ? new IOException("synthetic capture failure") : null;
                    finish.Invoke(recorder, new object[] { session, original });
                    Exception returned = null;
                    try { recorder.StopAsync().GetAwaiter().GetResult(); } catch (Exception error) { returned = error; }
                    if (scenario == 0)
                        check(returned == null && notifications == 0, "normal recorder completion succeeds without interruption event");
                    else
                    {
                        check(notifications == 1 && notifiedBeforeCompletion == 1, "recorder interruption is published before StopAsync completes (" + scenario + ")");
                        check(scenario == 1 ? ReferenceEquals(returned, original) : returned is InvalidOperationException,
                            "StopAsync preserves capture failure or unexpected-stop error (" + scenario + ")");
                    }
                }
            }
        }

        /// <summary>Run only in the disposable Windows CI desktop, never against user documents.</summary>
        public static void RunInteractive(Action<bool, string> check)
        {
            Console.WriteLine("Desktop fixture: interactive=" + Environment.UserInteractive + "; session=" + Process.GetCurrentProcess().SessionId);
            if (!Environment.UserInteractive)
                throw new InvalidOperationException("No interactive Windows desktop: native focus and paste tests were not executed.");
            string directory = Path.Combine(Path.GetTempPath(), "Neighbor.UiFixture." + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            Process fixture = null;
            try
            {
                fixture = Process.Start(new ProcessStartInfo(Assembly.GetEntryAssembly().Location, "--platform-ui-target \"" + directory + "\"") { UseShellExecute = false });
                FixtureStartup.Wait(fixture, directory, "WinForms fixture");
                Action activate = () =>
                {
                    fixture.Refresh();
                    var handle = fixture.MainWindowHandle;
                    Wait(() =>
                    {
                        if (fixture.HasExited) throw new InvalidOperationException("Isolated UI fixture exited unexpectedly.");
                        if (GetForegroundWindow() == handle) return true;
                        SetForegroundWindow(handle);
                        return GetForegroundWindow() == handle;
                    }, "controlled window activation (Windows desktop/foreground permission required)");
                };
                Action<string> command = value =>
                {
                    activate();
                    File.WriteAllText(Path.Combine(directory, "command"), value);
                    Wait(() => Read(Path.Combine(directory, "ack")) == value, "fixture command " + value);
                    activate();
                };
                using (var delivery = new WindowsTextDelivery())
                {
                    delivery.Diagnostic += detail => Console.WriteLine("UIA diagnostic: " + detail);
                    command("edit-1");
                    PlatformRangeDiagnostics.Run(fixture.Id, "RichTextBox");
                    FocusSnapshot target = null;
                    // The first cross-process UIA request can pay provider startup costs.
                    // Retry capture only; never repeat an uncertain paste operation.
                    for (int attempt = 0; attempt < 3; attempt++)
                    {
                        target = delivery.CaptureAsync().GetAwaiter().GetResult();
                        if (target.CanPaste) break;
                        activate(); Thread.Sleep(150);
                    }
                    check(target.CanPaste, "controlled editable window captured");
                    if (!target.CanPaste) throw new InvalidOperationException("Controlled capture rejected: " + target.Reason);
                    target.CapturedAtUtc = DateTime.UtcNow.AddMinutes(-11);
                    var expired = delivery.DeliverAsync("must-not-paste", target, CancellationToken.None).GetAwaiter().GetResult();
                    check(expired.Copied && !expired.PasteSent, "expired target snapshot prevents paste");
                    target.CapturedAtUtc = DateTime.UtcNow;
                    command("password-before-paste");
                    command("return-edit");
                    var inserted = delivery.DeliverAsync("neko", target, CancellationToken.None).GetAwaiter().GetResult();
                    Console.WriteLine("Controlled delivery: copied=" + inserted.Copied + "; sent=" + inserted.PasteSent + "; verified=" + inserted.PasteVerified + "; " + inserted.Message);
                    check(inserted.PasteSent, "same field restored before delivery receives paste event");
                    if (!inserted.PasteSent) throw new InvalidOperationException("Controlled paste rejected: " + inserted.Message);
                    Wait(() => Read(Path.Combine(directory, "text")) == "prefix neko suffix", "controlled paste");
                    check(true, "controlled text matches exactly without Enter");
                    if (!inserted.PasteVerified)
                    {
                        check(!delivery.RevalidateCorrectionAsync().GetAwaiter().GetResult(), "provider without stable insertion range disables learning");
                        Console.WriteLine("LIMITATION: fixture provider did not preserve exact insertion range; correction integration unverified.");
                    }
                    else
                    {
                        int observed = 0;
                        using (var correction = new ManualResetEventSlim())
                        {
                            var signalGate = new object();
                            bool listening = true;
                            EventHandler<CorrectionEventArgs> handler = (sender, change) =>
                            {
                                lock (signalGate)
                                {
                                    if (!listening) return;
                                    if (change.IsValid && change.Corrected == "neighbor") { Interlocked.Exchange(ref observed, 1); correction.Set(); }
                                    else if (!change.IsValid) correction.Set();
                                }
                            };
                            delivery.CorrectionObserved += handler;
                            try
                            {
                                command("correct");
                                correction.Wait(TimeSpan.FromSeconds(3));
                                if (Volatile.Read(ref observed) == 1) check(delivery.RevalidateCorrectionAsync().GetAwaiter().GetResult(), "observed correction revalidates exact range");
                                else check(!delivery.RevalidateCorrectionAsync().GetAwaiter().GetResult(), "invalidated correction range fails closed");
                            }
                            finally { lock (signalGate) { listening = false; delivery.CorrectionObserved -= handler; } }
                        }
                    }
                    command("edit-2");
                    var remembered = delivery.CaptureAsync().GetAwaiter().GetResult();
                    command("password-1");
                    var wrongTarget = delivery.DeliverAsync("must-not-paste", remembered, CancellationToken.None).GetAwaiter().GetResult();
                    check(wrongTarget.Copied && !wrongTarget.PasteSent, "focus switch prevents key injection");
                    var secret = delivery.CaptureAsync().GetAwaiter().GetResult();
                    check(!secret.CanPaste, "password target rejected");
                    delivery.StopObserving();
                    check(!delivery.RevalidateCorrectionAsync().GetAwaiter().GetResult(), "stopping observation invalidates pending candidate");
                    Console.WriteLine("LIMITATION: real Chinese/Japanese/Korean IME composition requires a desktop with that input method; not simulated as a passing hardware test.");
                }
            }
            finally
            {
                if (fixture != null && !fixture.HasExited) { fixture.CloseMainWindow(); if (!fixture.WaitForExit(2000)) fixture.Kill(); }
                fixture?.Dispose();
                try { Directory.Delete(directory, true); } catch { }
            }
            PlatformRangeTests.Run(check);
        }
        public static bool TryRunTarget(string[] args)
        {
            if (PlatformRangeTests.TryRunTarget(args)) return true;
            if (args.Length < 2 || args[0] != "--platform-ui-target") return false;
            string directory = args[1];
            Application.EnableVisualStyles();
            using (var form = new Form { Text = "Neighbor isolated UI fixture", Width = 500, Height = 250, TopMost = true })
            using (var edit = new RichTextBox { Left = 20, Top = 20, Width = 440, Height = 100, Text = "prefix  suffix" })
            using (var password = new TextBox { Left = 20, Top = 140, Width = 440, UseSystemPasswordChar = true })
            using (var timer = new System.Windows.Forms.Timer { Interval = 40 })
            {
                form.Controls.Add(edit); form.Controls.Add(password);
                string previous = null;
                edit.TextChanged += (sender, e) => File.WriteAllText(Path.Combine(directory, "text"), edit.Text);
                form.Shown += (sender, e) => { edit.Focus(); edit.Select(7, 0); File.WriteAllText(Path.Combine(directory, "ready"), "ready"); };
                timer.Tick += (sender, e) =>
                {
                    string command = Read(Path.Combine(directory, "command"));
                    if (string.IsNullOrEmpty(command) || command == previous) return;
                    previous = command;
                    form.Activate();
                    if (command.StartsWith("edit", StringComparison.Ordinal)) { edit.Text = "prefix  suffix"; edit.Focus(); edit.Select(7, 0); }
                    else if (command.StartsWith("password", StringComparison.Ordinal)) password.Focus();
                    else if (command == "return-edit") edit.Focus();
                    else if (command == "correct") { edit.Focus(); edit.Select(7, 4); edit.SelectedText = "neighbor"; }
                    File.WriteAllText(Path.Combine(directory, "ack"), command);
                };
                timer.Start(); Application.Run(form);
            }
            return true;
        }
        private static string Read(string path)
        {
            try
            {
                // Sharing avoids a transient reader denying a fixture TextChanged write.
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var reader = new StreamReader(stream)) return reader.ReadToEnd();
            }
            catch (IOException) { return null; }
        }
        private static void Wait(Func<bool> condition, string stage)
        {
            var timeout = Stopwatch.StartNew();
            while (!condition()) { if (timeout.Elapsed > TimeSpan.FromSeconds(8)) throw new Exception("UI fixture timeout: " + stage); Thread.Sleep(50); }
        }
        [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    }
}
