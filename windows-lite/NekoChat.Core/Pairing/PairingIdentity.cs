using System.Security.Cryptography;
using NekoChat.Core.Storage;
using NekoChat.Core.V2;

namespace NekoChat.Core.Pairing;

/// <summary>Owns the supplied signing key after successful construction; borrows the store. Drain all pairing actors before disposing it.</summary>
public sealed class PairingIdentity : IDisposable
{
    private readonly object _keyGate = new();
    private readonly ECDsa _key;
    private readonly LocalIdentitySnapshot _original;
    private bool _disposed;
    internal IChatStore Store { get; }
    public string PeerId { get; }
    public string PublicSpkiBase64 { get; }
    internal byte[] PublicKey => Convert.FromBase64String(PublicSpkiBase64);
    public PairingIdentity(IChatStore store, ECDsa signingKey)
    {
        ArgumentNullException.ThrowIfNull(store); ArgumentNullException.ThrowIfNull(signingKey);
        var original = store.GetLocalIdentitySnapshot(); byte[] spki = signingKey.ExportSubjectPublicKeyInfo();
        using var canonical = V2Crypto.OpenCanonicalSigningKey(spki);
        if (canonical is null || original.Id != V2Crypto.PeerId(spki) || original.PublicSpkiBase64 != Convert.ToBase64String(spki) || original.CredentialRef is null ||
            !V2Crypto.Verify(canonical, "nekochat-p1-key-check"u8, V2Crypto.Sign(signingKey, "nekochat-p1-key-check"u8)) || store.GetLocalIdentitySnapshot() != original)
            throw new Identity.IdentityUnavailableException("pairing-identity-not-ready");
        Store = store; _key = signingKey; _original = original; PeerId = original.Id; PublicSpkiBase64 = original.PublicSpkiBase64;
    }
    internal bool IsCurrent()
    {
        if (Volatile.Read(ref _disposed)) return false;
        return Store.GetLocalIdentitySnapshot() == _original;
    }
    internal PairingCandidateResult Candidate(byte[] publicKey, Func<bool> live)
    {
        string peer = V2Crypto.PeerId(publicKey); var context = Store.GetPeerVerificationContext(peer);
        if (!live() || !IsCurrent() || context.Security.LocalIdentityId != PeerId || context.Security.LocalRevision != _original.Revision || context.LocalSpkiBase64 != PublicSpkiBase64)
            return new(false, "stale-security", context.Security);
        return Store.BeginPairingCandidateIfCurrent(context.Security, publicKey, live);
    }
    internal bool Finalize(PeerSecuritySnapshot expected, Func<bool> live)
    {
        if (!live() || !IsCurrent() || expected.LocalIdentityId != PeerId || expected.LocalRevision != _original.Revision ||
            expected.Pairing is not (PairingState.Unpaired or PairingState.Pending or PairingState.Paired)) return false;
        var result = Store.SetPairingStateIfCurrent(expected, PairingState.Paired, live);
        return result.Changed || result is { Reason: "unchanged", Current.Pairing: PairingState.Paired } && result.Current == expected;
    }
    internal void Cleanup(PeerSecuritySnapshot expected)
    {
        // A failed new session must never downgrade an established pairing or overwrite a newer revocation/rebinding epoch.
        if (expected.Pairing == PairingState.Pending) Store.SetPairingStateIfCurrent(expected, PairingState.Unpaired, () => true);
    }
    internal byte[] Confirmation(string session, PairingRole role, byte[] transcript)
    { lock (_keyGate) { ObjectDisposedException.ThrowIf(_disposed, this); return PairingWire.Confirmation(session, role, transcript, _key); } }
    public void Dispose() { lock (_keyGate) { if (_disposed) return; Volatile.Write(ref _disposed, true); _key.Dispose(); } }
}
