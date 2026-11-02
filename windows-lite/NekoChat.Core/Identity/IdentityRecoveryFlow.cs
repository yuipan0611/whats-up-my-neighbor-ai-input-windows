using NekoChat.Core.Storage;

namespace NekoChat.Core.Identity;

/// <summary>One-use inspection ticket. It contains no key material and cannot be constructed by a UI.</summary>
public sealed class IdentityRecoveryPrompt
{
    internal IdentityRecoveryPrompt(object owner, long generation, LocalIdentitySnapshot expected, bool mismatch)
    { Owner = owner; Generation = generation; Expected = expected; IsMismatch = mismatch; }
    internal object Owner { get; }
    internal long Generation { get; }
    internal LocalIdentitySnapshot Expected { get; }
    internal int Consumed;
    public bool IsMismatch { get; }
    public string PreviousIdPrefix => Expected.Id![..Math.Min(8, Expected.Id!.Length)];
}

/// <summary>Inspection is read-only. Only explicit confirmation of a current lost-identity ticket can generate a replacement.</summary>
public sealed class IdentityRecoveryFlow(IChatStore store, IIdentityKeyVault vault)
{
    private readonly LocalIdentityCoordinator _identity = new(store, vault);
    private long _generation;
    public IdentityRecoveryPrompt? Inspect()
    {
        long generation = Interlocked.Increment(ref _generation);
        var expected = store.GetLocalIdentitySnapshot();
        if (expected.Id is null || expected.PublicSpkiBase64 is null && expected.CredentialRef is null) return null;
        var check = _identity.Inspect();
        if (check is not (IdentityLost or IdentityMismatch) || store.GetLocalIdentitySnapshot() != expected || generation != Volatile.Read(ref _generation)) return null;
        return new(this, generation, expected, check is IdentityMismatch);
    }
    public void Cancel() => Interlocked.Increment(ref _generation);
    public ReplaceIdentityResult Confirm(IdentityRecoveryPrompt prompt, Func<bool> stillCurrent)
    {
        ArgumentNullException.ThrowIfNull(prompt); ArgumentNullException.ThrowIfNull(stillCurrent);
        if (!ReferenceEquals(prompt.Owner, this) || Interlocked.Exchange(ref prompt.Consumed, 1) != 0) return new IdentityChangeDiscarded("not-current-prompt");
        bool Current() => prompt.Generation == Volatile.Read(ref _generation) && stillCurrent();
        if (!Current() || store.GetLocalIdentitySnapshot() != prompt.Expected) return new IdentityChangeDiscarded("stale-identity");
        // A user may have put the original protected key back while this dialog was open.
        if (_identity.Inspect() is not (IdentityLost or IdentityMismatch)) return new IdentityChangeDiscarded("identity-no-longer-lost");
        if (!Current() || store.GetLocalIdentitySnapshot() != prompt.Expected) return new IdentityChangeDiscarded("stale-identity");
        var replacement = vault.CreateCredential();
        return store.RecoverLocalIdentityIfCurrent(prompt.Expected, replacement.PeerId,
            Convert.FromBase64String(replacement.PublicSpkiBase64), replacement.Reference, Current);
    }
}
