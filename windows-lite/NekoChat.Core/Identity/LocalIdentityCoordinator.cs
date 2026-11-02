using System.Security.Cryptography;
using NekoChat.Core.Storage;
using NekoChat.Core.V2;

namespace NekoChat.Core.Identity;

/// <summary>Only explicit provisioning/replacement writes. Inspection/opening never regenerates a lost key.</summary>
public sealed class LocalIdentityCoordinator(IChatStore store, IIdentityKeyVault vault)
{
    public IdentityCheckResult Inspect() => store.CheckLocalIdentity(vault, key => V2Crypto.PeerId(key));

    public ECDsa OpenCurrentKey(Func<bool> stillCurrent)
    {
        ArgumentNullException.ThrowIfNull(stillCurrent);
        var expected = store.GetLocalIdentitySnapshot();
        if (expected.Id is null || expected.PublicSpkiBase64 is null || expected.CredentialRef is null || !stillCurrent()) throw new IdentityUnavailableException("identity_not_ready");
        ECDsa? key = null;
        try
        {
            key = vault.OpenSigningKey(expected.CredentialRef);
            byte[] spki = key.ExportSubjectPublicKeyInfo(); using var canonical = V2Crypto.OpenCanonicalSigningKey(spki);
            if (canonical is null || Convert.ToBase64String(spki) != expected.PublicSpkiBase64 || V2Crypto.PeerId(spki) != expected.Id ||
                !V2Crypto.Verify(canonical, "nekochat-identity-open-v1"u8, V2Crypto.Sign(key, "nekochat-identity-open-v1"u8))) throw new IdentityUnavailableException("identity_mismatch");
            if (!stillCurrent() || store.GetLocalIdentitySnapshot() != expected) throw new IdentityUnavailableException("stale_identity");
            var result = key; key = null; return result;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or CryptographicException or ArgumentException)
        { throw new IdentityUnavailableException("identity_lost"); }
        finally { key?.Dispose(); }
    }

    public bool Provision(LocalIdentitySnapshot expected, Func<bool> stillCurrent)
    {
        ArgumentNullException.ThrowIfNull(expected); ArgumentNullException.ThrowIfNull(stillCurrent);
        if (expected.Id is not null || !stillCurrent() || store.GetLocalIdentitySnapshot() != expected) return false;
        var credential = vault.CreateCredential();
        return store.ProvisionLocalIdentityIfCurrent(expected, credential.PeerId, Convert.FromBase64String(credential.PublicSpkiBase64), credential.Reference, stillCurrent);
    }

    public ReplaceIdentityResult Replace(LocalIdentitySnapshot expected, Func<bool> stillCurrent)
    {
        ArgumentNullException.ThrowIfNull(expected); ArgumentNullException.ThrowIfNull(stillCurrent);
        if (expected.Id is null || !stillCurrent() || store.GetLocalIdentitySnapshot() != expected) return new IdentityChangeDiscarded("stale-identity");
        var credential = vault.CreateCredential();
        return store.ReplaceLocalIdentityIfCurrent(expected, credential.PeerId, Convert.FromBase64String(credential.PublicSpkiBase64), credential.Reference, stillCurrent);
    }
}
