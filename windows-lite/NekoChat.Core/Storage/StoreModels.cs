namespace NekoChat.Core.Storage;

/// <summary>Persisted as lowercase strings. Only <see cref="Paired"/> may receive messages. Unknown stored strings are treated as untrusted.</summary>
public enum PairingState { Unpaired, Pending, Paired, Suspended, Revoked }

public static class PairingStates
{
    public static string ToWire(PairingState s) => s switch
    {
        PairingState.Unpaired => "unpaired",
        PairingState.Pending => "pending",
        PairingState.Paired => "paired",
        PairingState.Suspended => "suspended",
        PairingState.Revoked => "revoked",
        _ => throw new ArgumentOutOfRangeException(nameof(s)),
    };

    public static bool TryParse(string? wire, out PairingState state)
    {
        switch (wire)
        {
            case "unpaired": state = PairingState.Unpaired; return true;
            case "pending": state = PairingState.Pending; return true;
            case "paired": state = PairingState.Paired; return true;
            case "suspended": state = PairingState.Suspended; return true;
            case "revoked": state = PairingState.Revoked; return true;
            default: state = default; return false;
        }
    }
}

public sealed record ConversationRef(string ConversationId, string PeerId, long DraftEpoch, long DraftRevision);

public abstract record RegisterPeerResult;
public sealed record PeerRegistered(ConversationRef Conversation, bool Created) : RegisterPeerResult;
/// <summary>The pinned public key of an existing peer differs from the one supplied. Nothing was changed.</summary>
public sealed record PeerKeyMismatch(string PeerId) : RegisterPeerResult;

public sealed record PeerSeenResult(bool Written, string Reason, bool EndpointChanged, string? PreviousEndpoint);

public abstract record ReplaceIdentityResult;
public sealed record IdentityReplaced(string OldId, string NewId, int SupersededAttempts, int DroppedReceipts) : ReplaceIdentityResult;
public sealed record IdentityUnchanged : ReplaceIdentityResult;
public sealed record IdentityReused : ReplaceIdentityResult;
public sealed record NoIdentityToReplace : ReplaceIdentityResult;

/// <summary>Implemented by the Windows layer (private key lives in a DPAPI file, never in the database).</summary>
public interface IIdentityKeyProbe
{
    bool KeyExists(string credentialRef);
    bool TryGetPublicKey(string credentialRef, out byte[] spki);
    bool CanSign(string credentialRef);
}

public abstract record IdentityCheckResult;
public sealed record IdentityConsistent : IdentityCheckResult;
/// <summary>Both publicKey and credentialRef are unset (before D-04). Not an error.</summary>
public sealed record IdentityNotProvisioned : IdentityCheckResult;
/// <summary>Reason: no-row | incomplete-identity | key-missing | public-key-unreadable | cannot-sign | probe-failed.</summary>
public sealed record IdentityLost(string Reason) : IdentityCheckResult;
/// <summary>Reason: public-key-differs | id-not-derived.</summary>
public sealed record IdentityMismatch(string Reason) : IdentityCheckResult;

public sealed record StoreDiagnostics(
    long WalBytes, long DbBytes, bool LastCheckpointBusy, int LastCheckpointLogFrames, DateTime? LastCheckpointUtc,
    int RecoveredAttempts, int UnknownPairingStatesSeen);
