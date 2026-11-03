using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using NekoChat.Core.Identity;
using NekoChat.Core.Storage;
using NekoChat.Core.V2;
using NekoChat.Windows.Identity;

namespace NekoChat.Windows;

public partial class App
{
    /// <summary>Isolated native fixture: real Send/Cancel controls and ChatRuntime, with a deliberately held hello socket. Not P1 consent.</summary>
    private async Task<int> RunManualSendCancelSmokeAsync(string outputDirectory)
    {
        string output = Path.GetFullPath(outputDirectory), fixture = Path.Combine(output, "manual-cancel-fixture-" + Guid.NewGuid().ToString("N"));
        var checks = new List<string>(); string? failure = null; bool cleaned = false; ChatRuntime? runtime = null; ChatWindow? window = null;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var listener = new TcpListener(IPAddress.Loopback, 0); Task? heldSocket = null;
        var arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        const string text = "合成取消測試：不要自動重送";
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        void Check(bool ok, string label) { if (!ok) throw new InvalidOperationException(label); checks.Add(label); }
        async Task Until(Func<bool> condition) { while (!condition()) await Task.Delay(25, timeout.Token); }
        try
        {
            Directory.CreateDirectory(output);
            var source = new SettingsLoadResult(Settings.ForSmoke(fixture), SettingsSourceStatus.Missing);
            string root = ChatRuntime.RootDirectory(source.Value, true), peer;
            using (var remote = ECDsa.Create(ECCurve.NamedCurves.nistP256))
            {
                byte[] publicKey = remote.ExportSubjectPublicKeyInfo(); peer = V2Crypto.PeerId(publicKey);
                await Task.Run(() => {
                    using var store = ChatStore.Open(new(Path.Combine(root, "data")) { TextValidator = value => V2Text.Check(value, V2Text.ProductMaxUtf8Bytes) == V2TextProblem.None });
                    var identity = new LocalIdentityCoordinator(store, WindowsIdentityVault.Create(Path.Combine(root, "identity")));
                    if (!identity.Provision(store.GetLocalIdentitySnapshot(), () => true)) throw new InvalidOperationException("Synthetic provision failed.");
                    store.RegisterPeer(peer, PairingState.Paired, publicKey);
                    store.TouchPeerSeen(peer, "127.0.0.1"); // Fixture route only, not LAN/presence evidence.
                });
            }
            listener.Start(); int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            heldSocket = Task.Run(async () => {
                using var client = await listener.AcceptTcpClientAsync(timeout.Token); var stream = client.GetStream();
                using var header = new MemoryStream(); byte[] one = new byte[1];
                while (header.Length < 8192)
                {
                    await stream.ReadExactlyAsync(one, timeout.Token); header.WriteByte(one[0]);
                    if (header.Length >= 4 && header.GetBuffer().AsSpan((int)header.Length - 4, 4).SequenceEqual("\r\n\r\n"u8)) break;
                }
                string[] lines = Encoding.ASCII.GetString(header.ToArray()).Split("\r\n");
                if (lines[0] != "POST /nekochat/v2/hello HTTP/1.1") throw new InvalidDataException("Fixture expected only hello.");
                int length = int.Parse(lines.Single(line => line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)).Split(':')[1]);
                if (length is < 1 or > 1024) throw new InvalidDataException("Fixture request too large.");
                await stream.ReadExactlyAsync(new byte[length], timeout.Token); arrived.TrySetResult();
                await release.Task.WaitAsync(timeout.Token);
            });
            // Explicit fixture L only. Ordinary startup still supplies no wire/P1 configuration.
            runtime = await ChatRuntime.OpenAsync(source, true, 0, new(V2Text.ProductMaxUtf8Bytes)); runtime.Start();
            window = new(source.Value); window.SetReceiverState(true, runtime.Listener.Port, null);
            await window.AttachRuntimeAsync(runtime, port); window.Show(); window.Activate();
            await window.SmokeOpenEntryAsync(entry => entry.PeerId == peer);
            window.Draft.Text = text; await Until(() => window.SendButton.IsEnabled);
            window.SendButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await arrived.Task.WaitAsync(timeout.Token); await Until(() => window.CancelAttemptButton.IsEnabled && !window.SmokeSendIdle);
            Check(window.SmokeSelectedEntry?.Attempt?.State == "sending", "real Send control created exact pending manual attempt");
            var attempt = window.SmokeSelectedEntry!.Attempt!;
            Check(runtime.CanCancelAttempt(attempt.MessageId, attempt.TryCount), "runtime owns cancelable current network operation");
            window.CancelAttemptButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => window.SmokeSendIdle && window.SmokeSelectedEntry?.Attempt?.State == "cancelled");
            var stored = await runtime.Worker.Run(store => {
                var row = store.GetInboxSnapshot().Entries.Single(entry => entry.PeerId == peer);
                return (State: row.Attempt?.State, Draft: store.GetDraftState(row.ConversationId!)!.RawText, Count: store.ListMessages(row.ConversationId!).Items.Count);
            });
            Check(stored is { State: "cancelled", Draft: text, Count: 0 }, "actual Cancel control stops I/O and persists cancellation without history or draft loss");
            Check(!window.CancelAttemptButton.IsEnabled && !runtime.CanCancelAttempt(attempt.MessageId, attempt.TryCount), "finished operation cannot be canceled again");
            Capture(window, Path.Combine(output, "manual-send-cancelled.png"));
        }
        catch (Exception ex) { failure = ex.GetType().Name; }
        finally
        {
            release.TrySetResult(); listener.Stop(); await timeout.CancelAsync();
            try { if (heldSocket is not null) await heldSocket; } catch (Exception ex) when (ex is OperationCanceledException or SocketException or EndOfStreamException) { }
            catch (Exception ex) { failure ??= ex.GetType().Name; }
            try { if (window is not null) await window.PrepareForShutdownAsync(); if (runtime is not null) await runtime.DisposeAsync(); window?.DetachRuntime(); window?.Hide(); }
            catch (Exception ex) { failure ??= ex.GetType().Name; }
            try { if (Directory.Exists(fixture)) Directory.Delete(fixture, true); cleaned = true; } catch { failure ??= "FixtureCleanupFailed"; }
        }
        Directory.CreateDirectory(output);
        await File.WriteAllTextAsync(Path.Combine(output, "manual-send-cancel-result.json"), JsonSerializer.Serialize(new {
            passed = failure is null, checks, failure, cleaned, syntheticRoutedControls = true, fixtureMaximumTextBytes = V2Text.ProductMaxUtf8Bytes,
            productIdentityModeSelected = false, productMaximumTextBytesSelected = false, captureKind = "WPF RenderTargetBitmap96, not physical desktop DPI evidence",
            pending = new[] { "real native keyboard/IME", "DPI/accessibility and focus", "physical network after-receiver-COMMIT cancellation", "real Windows lifecycle" }
        }, new JsonSerializerOptions { WriteIndented = true }));
        return failure is null ? 0 : 1;
    }
}
