using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using NekoChat.Core.Identity;
using NekoChat.Core.Storage;
using NekoChat.Core.V2;
using NekoChat.Windows.Identity;

namespace NekoChat.Tests;

internal static class IdentityVaultChecks
{
    public static async Task<int> Run()
    {
        int checks = 0;
        void Check(bool ok, string name) { checks++; if (!ok) throw new Exception("Identity vault: " + name); }
        bool Reject(Action action) { try { action(); return false; } catch (Exception e) when (e is IOException or ArgumentException or CryptographicException or IdentityUnavailableException) { return true; } }
        using (var f = new Fixture())
        {
            var credential = f.Vault.CreateCredential(); string path = f.PathFor(credential); byte[] protectedFile = File.ReadAllBytes(path);
            Check(credential.Reference.StartsWith("dpapi-v1:", StringComparison.Ordinal) && credential.PeerId == V2Crypto.PeerId(Convert.FromBase64String(credential.PublicSpkiBase64)) && Directory.GetFiles(f.IdentityPath).Length == 1,
                "unique opaque reference publishes one bounded credential with derived public identity");
            Check(f.Protector.LastInput!.All(b => b == 0) && f.Protector.LastOutput!.All(b => b == 0), "private export and protected temporary buffers are zeroed after create");
            Check(protectedFile.AsSpan().IndexOf(f.Protector.PrivateCopy!) < 0 && !Encoding.UTF8.GetString(protectedFile).Contains("PRIVATE KEY"), "file contains no exported PKCS8 or PEM plaintext");
            using var key = f.Vault.OpenSigningKey(credential.Reference);
            Check(Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()) == credential.PublicSpkiBase64 && f.Vault.CanSign(credential.Reference) && f.Vault.TryGetPublicKey(credential.Reference, out var spki) &&
                Convert.ToBase64String(spki) == credential.PublicSpkiBase64, "same file reader actually signs and derives canonical namedP256 public key");
            Check(f.Protector.LastDecrypted!.All(b => b == 0), "decrypted PKCS8 is zeroed after importing private key");
            var other = f.Vault.CreateCredential();
            Check(other.Reference != credential.Reference && other.PeerId != credential.PeerId && File.ReadAllBytes(path).AsSpan().SequenceEqual(protectedFile), "creating another key never overwrites existing credentials");
            File.Copy(f.PathFor(other), path, overwrite: true);
            Check(Reject(() => f.Vault.OpenSigningKey(credential.Reference).Dispose()), "copied ciphertext from another reference fails authenticated entropy binding");
            File.WriteAllBytes(path, protectedFile);
            using var otherProtector = new Protector(); var wrongUser = new IdentityFileVault(f.IdentityPath, otherProtector);
            Check(Reject(() => wrongUser.OpenSigningKey(credential.Reference).Dispose()), "fixture protector isolation cannot read another vault key (not a DPAPI profile test)");
        }
        using (var f = new Fixture())
        {
            var credential = f.Vault.CreateCredential(); string path = f.PathFor(credential); byte[] original = File.ReadAllBytes(path);
            foreach (string error in new[] { "empty", "truncated", "magic", "ciphertext", "oversize" })
            {
                byte[] broken = error switch { "empty" => [], "truncated" => original[..^1], "oversize" => new byte[16393], _ => original.ToArray() };
                if (error == "magic") broken[0] ^= 1; if (error == "ciphertext") broken[^1] ^= 1;
                File.WriteAllBytes(path, broken);
                Check(Reject(() => f.Vault.OpenSigningKey(credential.Reference).Dispose()) && !f.Vault.CanSign(credential.Reference) && !f.Vault.TryGetPublicKey(credential.Reference, out _), error + " never loads or replaces identity");
            }
            File.WriteAllBytes(path, original);
            foreach (string reference in new[] { "", "../x", "dpapi-v1:../x", credential.Reference + "\n", credential.Reference.ToUpperInvariant(), "dpapi-v2:" + Guid.NewGuid().ToString("D"), null! })
                Check(Reject(() => f.Vault.OpenSigningKey(reference).Dispose()) && !f.Vault.KeyExists(reference), "reference rejects path/version/encoding ambiguity");
            string linkRoot = Path.Combine(f.Root, "link"); Directory.CreateSymbolicLink(linkRoot, f.IdentityPath); var linked = new IdentityFileVault(linkRoot, f.Protector);
            Check(Reject(() => linked.CreateCredential()) && Reject(() => linked.OpenSigningKey(credential.Reference).Dispose()), "linked root and ancestors are rejected");
            string otherFile = Path.Combine(f.Root, "unrelated.bin"); File.WriteAllBytes(otherFile, original); File.Delete(path); File.CreateSymbolicLink(path, otherFile);
            Check(!f.Vault.KeyExists(credential.Reference) && Reject(() => f.Vault.OpenSigningKey(credential.Reference).Dispose()) && File.ReadAllBytes(otherFile).AsSpan().SequenceEqual(original), "credential symlink cannot read or overwrite outside root");
        }
        using (var f = new Fixture())
        {
            f.Protector.FailProtect = true;
            Check(Reject(() => f.Vault.CreateCredential()) && Directory.GetFiles(f.IdentityPath).Length == 0 && f.Protector.LastInput!.All(b => b == 0), "protection failure writes no credential and clears exported private bytes");
            f.Protector.FailProtect = false; var c = f.Vault.CreateCredential(); f.Protector.RewriteDecrypted = p => p.Concat(new byte[] { 0 }).ToArray();
            Check(Reject(() => f.Vault.OpenSigningKey(c.Reference).Dispose()) && f.Protector.LastDecrypted!.All(b => b == 0), "PKCS8 trailing bytes rejected with decrypted buffer zeroed");
            f.Protector.RewriteDecrypted = _ => { using var key = ECDsa.Create(ECCurve.NamedCurves.nistP384); return key.ExportPkcs8PrivateKey(); };
            Check(Reject(() => f.Vault.OpenSigningKey(c.Reference).Dispose()), "decrypted key on another curve is rejected");
        }
        using (var f = new Fixture())
        {
            var before = f.Store.GetLocalIdentitySnapshot();
            Check(before.Id is null && f.Coordinator.Inspect() is IdentityLost { Reason: "no-row" } && Reject(() => f.Coordinator.OpenCurrentKey(() => true).Dispose()) && !Directory.Exists(f.IdentityPath),
                "inspecting or opening an empty store does not generate an identity");
            Check(f.Coordinator.Provision(before, () => true) && f.Coordinator.Inspect() is IdentityConsistent, "explicit provision stores only public identity and protected file reference");
            var identity = f.Store.GetLocalIdentitySnapshot(); using var key = f.Coordinator.OpenCurrentKey(() => true);
            Check(identity.Id == V2Crypto.PeerId(key.ExportSubjectPublicKeyInfo()) && identity.Revision > before.Revision && !f.Coordinator.Provision(before, () => true), "provision captured revision cannot overwrite a newer identity");
            int files = Directory.GetFiles(f.IdentityPath).Length;
            Check(!f.Coordinator.Provision(identity, () => true) && f.Coordinator.Replace(identity, () => false) is IdentityChangeDiscarded && Directory.GetFiles(f.IdentityPath).Length == files,
                "ineligible or cancelled explicit action does not even generate a new key");
            File.Delete(f.PathFor(identity.CredentialRef!));
            Check(f.Coordinator.Inspect() is IdentityLost { Reason: "key-missing" } && Reject(() => f.Coordinator.OpenCurrentKey(() => true).Dispose()) && f.Store.GetLocalIdentitySnapshot() == identity && Directory.GetFiles(f.IdentityPath).Length == 0,
                "lost key stays lost, never silently regenerates or changes database identity");
            Check(f.Coordinator.Replace(identity, () => true) is IdentityReplaced && f.Coordinator.Inspect() is IdentityConsistent && f.Store.GetLocalIdentitySnapshot().Id != identity.Id,
                "explicit replacement of a lost identity creates a different verified public identity");
        }
        using (var f = new Fixture())
        {
            f.Coordinator.Provision(f.Store.GetLocalIdentitySnapshot(), () => true); var identity = f.Store.GetLocalIdentitySnapshot(); byte[] original = File.ReadAllBytes(f.PathFor(identity.CredentialRef!));
            var other = f.Vault.CreateCredential();
            using var otherKey = f.Vault.OpenSigningKey(other.Reference); byte[] otherRaw = otherKey.ExportPkcs8PrivateKey(); f.Protector.RewriteDecrypted = _ => otherRaw.ToArray();
            Check(f.Coordinator.Inspect() is IdentityMismatch { Reason: "public-key-differs" } && Reject(() => f.Coordinator.OpenCurrentKey(() => true).Dispose()) && f.Store.GetLocalIdentitySnapshot() == identity,
                "a valid different signing key cannot impersonate database identity or cause automatic repair");
            CryptographicOperations.ZeroMemory(otherRaw); f.Protector.RewriteDecrypted = null;
            Check(File.ReadAllBytes(f.PathFor(identity.CredentialRef!)).AsSpan().SequenceEqual(original), "identity inspection is read-only even on mismatch");
        }
        foreach (string operation in new[] { "provision-identity", "replace-identity" })
        {
            using var f = new Fixture();
            if (operation == "replace-identity") f.Coordinator.Provision(f.Store.GetLocalIdentitySnapshot(), () => true);
            var before = f.Store.GetLocalIdentitySnapshot(); bool live = true; f.Faults.Before = op => { if (op == operation) live = false; };
            bool changed = operation == "provision-identity" ? f.Coordinator.Provision(before, () => live) : f.Coordinator.Replace(before, () => live) is IdentityReplaced;
            Check(!changed && f.Store.GetLocalIdentitySnapshot() == before, operation + " final cancellation rolls back identity/revision transaction");
            Check(Directory.GetFiles(f.IdentityPath).Length == (before.Id is null ? 1 : 2), operation + " rollback preserves encrypted orphan and previous credential instead of deleting keys");
        }
        using (var f = new Fixture())
        {
            f.Coordinator.Provision(f.Store.GetLocalIdentitySnapshot(), () => true); var first = f.Store.GetLocalIdentitySnapshot();
            f.Faults.Before = op => { if (op == "replace-identity") throw new IOException("fixture disk failure"); };
            Check(Reject(() => f.Coordinator.Replace(first, () => true)) && f.Store.GetLocalIdentitySnapshot() == first && f.Vault.CanSign(first.CredentialRef!), "replacement IO failure preserves original public and private identity");
            f.Faults.Before = null; Check(f.Coordinator.Replace(first, () => true) is IdentityReplaced && f.Vault.CanSign(first.CredentialRef!), "successful replacement preserves old encrypted credential for explicit later policy");
            var current = f.Store.GetLocalIdentitySnapshot();
            Check(f.Coordinator.Replace(first, () => true) is IdentityChangeDiscarded && f.Store.GetLocalIdentitySnapshot() == current, "old replacement confirmation cannot overwrite a later identity epoch");
            int calls = 0;
            Check(Reject(() => f.Coordinator.OpenCurrentKey(() => ++calls < 2).Dispose()) && f.Store.GetLocalIdentitySnapshot() == current, "lifecycle ending during key open disposes result without modifying identity");
        }
        using (var f = new Fixture())
        {
            var before = f.Store.GetLocalIdentitySnapshot(); using var second = ChatStore.Open(f.Options); var other = new LocalIdentityCoordinator(second, f.Vault);
            using var gate = new ManualResetEventSlim();
            var tasks = new[] { f.Coordinator, other }.Select(c => Task.Run(() => { gate.Wait(); return c.Provision(before, () => true); })).ToArray(); gate.Set();
            var results = await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(8));
            Check(results.Count(b => b) == 1 && f.Coordinator.Inspect() is IdentityConsistent, "two real SQLite handles atomically provision exactly one identity");
        }
        using (var f = new Fixture())
        {
            f.Coordinator.Provision(f.Store.GetLocalIdentitySnapshot(), () => true); var old = f.Store.GetLocalIdentitySnapshot();
            using (var db = new SqliteConnection("Data Source=" + f.Store.DatabasePath + ";Pooling=False"))
            {
                db.Open(); using var cmd = db.CreateCommand(); cmd.CommandText = "UPDATE LocalIdentity SET credentialRef='fixture-temporary'; UPDATE LocalIdentity SET credentialRef=@ref"; cmd.Parameters.AddWithValue("@ref", old.CredentialRef); cmd.ExecuteNonQuery();
            }
            Check(f.Store.GetLocalIdentitySnapshot().Id == old.Id && f.Store.GetLocalIdentitySnapshot().CredentialRef == old.CredentialRef && f.Store.GetLocalIdentitySnapshot().Revision > old.Revision &&
                f.Coordinator.Replace(old, () => true) is IdentityChangeDiscarded, "credential ABA cannot reuse an older human confirmation snapshot");
            var newer = f.Vault.CreateCredential(); bool changed = false;
            f.Protector.RewriteDecrypted = raw => {
                if (!changed) { changed = true; f.Store.ReplaceLocalIdentity(newer.PeerId, Convert.FromBase64String(newer.PublicSpkiBase64), newer.Reference); }
                return raw.ToArray();
            };
            Check(Reject(() => f.Coordinator.OpenCurrentKey(() => true).Dispose()) && f.Store.GetLocalIdentitySnapshot().Id == newer.PeerId,
                "identity changing during decryption cannot publish an obsolete signing key");
        }
        Console.WriteLine($"Identity vault: {checks} encrypted-file, real signature and SQLite lifecycle checks; AES-GCM fixture, Windows DPAPI not executed."); return checks;
    }
    private sealed class Fixture : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "neko-identity-vault-" + Guid.NewGuid().ToString("N"));
        internal string IdentityPath => Path.Combine(Root, "identity");
        internal Protector Protector { get; } = new();
        internal Faults Faults { get; } = new();
        internal ChatStoreOptions Options => new(Path.Combine(Root, "data")) { Faults = Faults };
        internal IdentityFileVault Vault { get; }
        internal ChatStore Store { get; }
        internal LocalIdentityCoordinator Coordinator { get; }
        internal Fixture() { Directory.CreateDirectory(Root); Vault = new(IdentityPath, Protector); Store = ChatStore.Open(Options); Coordinator = new(Store, Vault); }
        internal string PathFor(IdentityCredential c) => PathFor(c.Reference);
        internal string PathFor(string reference) => Path.Combine(IdentityPath, reference["dpapi-v1:".Length..] + ".dpapi");
        public void Dispose() { Store.Dispose(); Protector.Dispose(); Directory.Delete(Root, true); }
    }
    private sealed class Protector : IIdentityProtector, IDisposable
    {
        private readonly byte[] _key = RandomNumberGenerator.GetBytes(32);
        internal byte[]? LastInput, LastOutput, LastDecrypted, PrivateCopy;
        internal bool FailProtect;
        internal Func<byte[], byte[]>? RewriteDecrypted;
        public byte[] Protect(byte[] privateKey, byte[] entropy)
        {
            LastInput = privateKey; PrivateCopy = privateKey.ToArray(); if (FailProtect) throw new CryptographicException("fixture protector failure");
            byte[] result = new byte[28 + privateKey.Length]; RandomNumberGenerator.Fill(result.AsSpan(0, 12));
            using var aes = new AesGcm(_key, 16); aes.Encrypt(result.AsSpan(0, 12), privateKey, result.AsSpan(28), result.AsSpan(12, 16), entropy);
            LastOutput = result; return result;
        }
        public byte[] Unprotect(byte[] protectedKey, byte[] entropy)
        {
            if (protectedKey.Length < 28) throw new CryptographicException("fixture truncated ciphertext");
            byte[] raw = new byte[protectedKey.Length - 28]; using var aes = new AesGcm(_key, 16);
            try { aes.Decrypt(protectedKey.AsSpan(0, 12), protectedKey.AsSpan(28), protectedKey.AsSpan(12, 16), raw, entropy); }
            catch { CryptographicOperations.ZeroMemory(raw); throw; }
            if (RewriteDecrypted is { } rewrite) { byte[] next = rewrite(raw); CryptographicOperations.ZeroMemory(raw); raw = next; }
            LastDecrypted = raw; return raw;
        }
        public void Dispose() { CryptographicOperations.ZeroMemory(_key); if (PrivateCopy is not null) CryptographicOperations.ZeroMemory(PrivateCopy); }
    }
    private sealed class Faults : IStoreFaults
    {
        internal Action<string>? Before;
        public void AfterBegin(string op) { }
        public void BeforeCommit(string op) => Before?.Invoke(op);
        public void AfterCommit(string op) { }
    }
}
