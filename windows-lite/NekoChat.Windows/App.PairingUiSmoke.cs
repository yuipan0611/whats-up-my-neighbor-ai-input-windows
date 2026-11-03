using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using NekoChat.Core.Identity;
using NekoChat.Core.Roster;
using NekoChat.Core.Storage;
using NekoChat.Windows.Identity;

namespace NekoChat.Windows;

public partial class App
{
    /// <summary>Explicit synthetic automation. Isolated DPAPI profiles, routed control actions, real local TCP. Never human SAS acceptance.</summary>
    private async Task<int> RunPairingUiSmokeAsync(string outputDirectory)
    {
        string output = Path.GetFullPath(outputDirectory), fixture = Path.Combine(output, "pairing-ui-fixture-" + Guid.NewGuid().ToString("N"));
        var checks = new List<string>(); string? failure = null; bool cleaned = false;
        ChatRuntime? a = null, b = null; PairingWindow? aw = null, bw = null;
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        void Check(bool ok, string label) { if (!ok) throw new InvalidOperationException(label); checks.Add(label); }
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        async Task Until(Func<bool> condition) { while (!condition()) await Task.Delay(25, deadline.Token); }
        async Task<ChatRuntime> Node(string name)
        {
            var source = new SettingsLoadResult(Settings.ForSmoke(Path.Combine(fixture, name)), SettingsSourceStatus.Missing);
            string root = ChatRuntime.RootDirectory(source.Value, true);
            await Task.Run(() => {
                using var store = ChatStore.Open(new(Path.Combine(root, "data")));
                var identity = new LocalIdentityCoordinator(store, WindowsIdentityVault.Create(Path.Combine(root, "identity")));
                if (!identity.Provision(store.GetLocalIdentitySnapshot(), () => true)) throw new InvalidOperationException("Synthetic provision failed.");
                store.SaveManualRosterEntry(null, new("合成同學 " + name, name == "A" ? "001" : "002"), RosterImportPreview.ComputeSnapshotToken(store.GetRosterSnapshot()));
            });
            // P1 only; no L is selected and private messaging stays disabled.
            var runtime = await ChatRuntime.OpenAsync(source, true, 0, pairingConfiguration: new()); runtime.Start(); return runtime;
        }
        void Click(Button control) { Check(control.IsEnabled, "synthetic routed control is enabled: " + control.Name); control.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); }
        async Task CloseWindow(PairingWindow? window)
        {
            if (window is null || !window.IsLoaded) return;
            window.CloseSafely(); await Until(() => !window.IsVisible);
        }
        try
        {
            Directory.CreateDirectory(output); Directory.CreateDirectory(fixture);
            a = await Node("A"); b = await Node("B");
            var at = await a.Worker.Run(s => s.CapturePairingRosterTarget(s.GetRosterSnapshot().Single().Entry.Id)!);
            var bt = await b.Worker.Run(s => s.CapturePairingRosterTarget(s.GetRosterSnapshot().Single().Entry.Id)!);
            aw = new(a, at, b.Listener.Port); bw = new(b, bt, a.Listener.Port);
            bw.Show(); bw.Activate(); await Until(() => bw.IsActive && bw.ListenButton.IsEnabled);
            Check(!b.CanSend && b.MaximumTextBytes is null && bw.Compared.IsChecked != true && !bw.ConfirmButton.IsEnabled && !bw.ConfirmButton.IsDefault, "P1 fixture leaves L unset and confirmation unchecked/no default");
            Click(bw.ListenButton);
            aw.Show(); aw.Activate(); await Until(() => aw.IsActive && aw.BeginButton.IsEnabled); aw.Endpoint.Text = "127.0.0.1"; Click(aw.BeginButton);
            await Until(() => aw.Compared.IsEnabled && aw.SasNumber.Text.Length == 7 && aw.SasNumber.Text == bw.SasNumber.Text);
            Check(await a.Worker.Run(s => s.GetRosterSnapshot().Single().Entry.PeerId is null) && await b.Worker.Run(s => s.GetRosterSnapshot().Single().Entry.PeerId is null), "real TCP SAS exchange has no automatic binding");
            Capture(aw, Path.Combine(output, "pairing-sas-520.png"));
            aw.Compared.IsChecked = true; Check(aw.ConfirmButton.IsEnabled, "explicit synthetic checkbox enables current ticket");
            bw.Activate(); await Until(() => bw.IsActive); Check(aw.Compared.IsChecked != true && !aw.ConfirmButton.IsEnabled, "deactivation clears checkbox and disables confirmation");
            bw.Compared.IsChecked = true; await Until(() => bw.ConfirmButton.IsEnabled); Click(bw.ConfirmButton);
            aw.Activate(); await Until(() => aw.IsActive && aw.Compared.IsEnabled); aw.Compared.IsChecked = true; Click(aw.ConfirmButton);
            await Until(() => aw.StateTitle.Text == "本機已完成配對與綁定" && bw.StateTitle.Text == "本機已完成配對與綁定");
            Check(await a.Worker.Run(s => s.GetRosterSnapshot().Single().Entry.PeerId is not null) && await b.Worker.Run(s => s.GetRosterSnapshot().Single().Entry.PeerId is not null), "two explicit routed fixture confirmations bind each original row");
            Check(!aw.ConfirmButton.IsEnabled && !aw.BeginButton.IsEnabled && !a.CanSend && a.MaximumTextBytes is null, "completed dialog is one-use and does not enable private messages/L");
            aw.Width = 440; aw.Height = 360; await Task.Delay(100, deadline.Token); Capture(aw, Path.Combine(output, "pairing-complete-440x360.png"));
            Check(aw.CloseButton.IsVisible && aw.CloseButton.ActualHeight >= 28, "short native window retains close footer");
            a.ResetAfterResume(); await Task.Delay(300, deadline.Token);
            Check(!aw.ConfirmButton.IsEnabled && !aw.BeginButton.IsEnabled && aw.Compared.IsChecked != true, "runtime resume disables old native dialog");
        }
        catch (Exception ex) { failure = ex.GetType().Name; }
        finally
        {
            try { await CloseWindow(aw); await CloseWindow(bw); if (a is not null) await a.DisposeAsync(); if (b is not null) await b.DisposeAsync(); }
            catch (Exception ex) { failure ??= ex.GetType().Name; }
            try { if (Directory.Exists(fixture)) Directory.Delete(fixture, true); cleaned = true; } catch { failure ??= "FixtureCleanupFailed"; }
        }
        Directory.CreateDirectory(output);
        await File.WriteAllTextAsync(Path.Combine(output, "pairing-ui-result.json"), JsonSerializer.Serialize(new {
            passed = failure is null, checks, failure, cleaned, syntheticRoutedConsent = true, messageLimitSelected = false,
            pending = new[] { "real human face-to-face SAS comparison", "native keyboard/IME and assistive technology", "100/125/150/200 percent DPI, work area and multiple monitors", "real sleep/resume and network interruption", "two Windows machines and classroom acceptance" }
        }, new JsonSerializerOptions { WriteIndented = true }));
        return failure is null ? 0 : 1;
    }
}
