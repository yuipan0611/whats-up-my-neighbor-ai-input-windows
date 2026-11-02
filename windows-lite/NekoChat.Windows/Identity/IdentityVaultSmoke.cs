using System.IO;
using System.Text.Json;
using NekoChat.Core.Identity;
using NekoChat.Core.Storage;
using NekoChat.Core.V2;

namespace NekoChat.Windows.Identity;

/// <summary>Windows-only fixture. Never reads Settings.DirectoryPath or an existing user identity.</summary>
internal static class IdentityVaultSmoke
{
    internal static int Run(string outputDirectory)
    {
        var checks = new List<string>(); string output = Path.GetFullPath(outputDirectory);
        string fixture = Path.Combine(output, "identity-fixture-" + Guid.NewGuid().ToString("N"));
        string? failure = null; bool cleaned = false;
        void Check(bool ok, string label) { if (!ok) throw new InvalidOperationException("Identity fixture check failed."); checks.Add(label); }
        try
        {
            Directory.CreateDirectory(output); Directory.CreateDirectory(fixture);
            string keyDirectory = Path.Combine(fixture, "identity"); var vault = WindowsIdentityVault.Create(keyDirectory);
            using (var store = ChatStore.Open(new(Path.Combine(fixture, "data"))))
            {
                var coordinator = new LocalIdentityCoordinator(store, vault); var empty = store.GetLocalIdentitySnapshot();
                Check(empty.Id is null && coordinator.Inspect() is IdentityLost && !Directory.Exists(keyDirectory), "inspection never provisions");
                Check(coordinator.Provision(empty, () => true) && coordinator.Inspect() is IdentityConsistent, "CurrentUser DPAPI provision and real key probe");
                var original = store.GetLocalIdentitySnapshot();
                using (var key = coordinator.OpenCurrentKey(() => true))
                    Check(V2Crypto.Verify(key, "nekochat-windows-native-identity-smoke"u8, V2Crypto.Sign(key, "nekochat-windows-native-identity-smoke"u8)), "actual Windows P256 sign and verify");
                var reopenedVault = WindowsIdentityVault.Create(keyDirectory);
                Check(reopenedVault.CanSign(original.CredentialRef!), "same-user DPAPI reopen");
                string file = Directory.GetFiles(keyDirectory, "*.dpapi").Single(); byte[] ciphertext = File.ReadAllBytes(file), broken = ciphertext.ToArray(); broken[^1] ^= 1;
                File.WriteAllBytes(file, broken);
                Check(!reopenedVault.CanSign(original.CredentialRef!) && coordinator.Inspect() is IdentityLost && store.GetLocalIdentitySnapshot() == original, "tamper rejects without replacement");
                File.WriteAllBytes(file, ciphertext);
                Check(coordinator.Inspect() is IdentityConsistent, "exact protected original restores probe");
                File.Move(file, file + ".held");
                Check(coordinator.Inspect() is IdentityLost { Reason: "key-missing" } && store.GetLocalIdentitySnapshot() == original && Directory.GetFiles(keyDirectory, "*.dpapi").Length == 0, "missing key is not automatically recreated");
                File.Move(file + ".held", file);
                Check(coordinator.Replace(original, () => true) is IdentityReplaced && coordinator.Inspect() is IdentityConsistent && reopenedVault.CanSign(original.CredentialRef!), "explicit replacement keeps old encrypted credential");
            }
            using (var store = ChatStore.Open(new(Path.Combine(fixture, "data"))))
            {
                var reopenedVault = WindowsIdentityVault.Create(keyDirectory);
                Check(new LocalIdentityCoordinator(store, reopenedVault).Inspect() is IdentityConsistent, "store and vault reopen agree");
                var recovery = new IdentityRecoveryFlow(store, reopenedVault);
                Check(recovery.Inspect() is null, "healthy key cannot produce a recovery confirmation");
                store.RegisterPeer("recovery-peer", PairingState.Paired); store.SetReadReceiptPolicy(true);
                var original = store.GetLocalIdentitySnapshot();
                string currentFile = Path.Combine(keyDirectory, original.CredentialRef!["dpapi-v1:".Length..] + ".dpapi");
                File.Move(currentFile, currentFile + ".held");
                var canceled = recovery.Inspect()!; recovery.Cancel();
                Check(recovery.Confirm(canceled, () => true) is IdentityChangeDiscarded && store.GetLocalIdentitySnapshot() == original, "one-use recovery cancellation leaves identity unchanged");
                var confirmed = recovery.Inspect()!;
                Check(recovery.Confirm(confirmed, () => true) is IdentityReplaced && store.GetPeerSecuritySnapshot("recovery-peer").Pairing == PairingState.Suspended && !store.GetReadReceiptPolicy().Enabled,
                    "explicit lost-key recovery creates a new DPAPI identity and atomically suspends pairing and resets privacy");
                Check(new LocalIdentityCoordinator(store, reopenedVault).Inspect() is IdentityConsistent && File.Exists(currentFile + ".held"), "recovery keeps old encrypted file and new key is usable");
            }
        }
        catch (Exception ex) { failure = ex.GetType().Name; }
        finally
        {
            try { if (Directory.Exists(fixture)) Directory.Delete(fixture, true); cleaned = true; }
            catch { failure ??= "FixtureCleanupFailed"; }
        }
        Directory.CreateDirectory(output);
        File.WriteAllText(Path.Combine(output, "identity-vault-result.json"), JsonSerializer.Serialize(new {
            passed = failure is null, checks, failure, cleaned,
            pending = new[] { "different Windows user/profile and machine", "real logoff/reboot", "restore with unrelated real identity", "WPF lost-identity confirmation" }
        }, new JsonSerializerOptions { WriteIndented = true }));
        return failure is null ? 0 : 1;
    }
}
