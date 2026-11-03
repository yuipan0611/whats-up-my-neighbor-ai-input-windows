namespace NekoChat.Core.Storage;

/// <summary>What the classroom transport hands to the store. The source address is NOT here: it comes from the accepted socket.</summary>
public sealed record ClassroomIncoming(string MessageId, string Text, string? Reaction = null);

public abstract record ClassroomReceiveResult;
public sealed record ClassroomStored(ClassroomPrincipalKey Principal, string MessageId, long LocalSeq, int UnreadCount) : ClassroomReceiveResult;
public sealed record ClassroomDuplicate(ClassroomPrincipalKey Principal, string MessageId, long LocalSeq) : ClassroomReceiveResult;
/// <summary>Cleared earlier by the user: nothing is recreated (no history, unread or notification), but the sender may still be ACKed by the transport.</summary>
public sealed record ClassroomClearedDuplicate(ClassroomPrincipalKey Principal, string MessageId) : ClassroomReceiveResult;
public sealed record ClassroomConflict(ClassroomPrincipalKey Principal, string MessageId) : ClassroomReceiveResult;
/// <summary>Reason: invalid-source | invalid-id | invalid-text | invalid-reaction | invalid-unicode | offroster-limit.</summary>
public sealed record ClassroomRejected(string Reason) : ClassroomReceiveResult;

/// <summary>CM-11: bounds for sources that are not on the roster. Hitting one refuses the NEW message; stored rows are never deleted.</summary>
public sealed record ClassroomOffRosterLimits(int MaxSources = 200, int MaxMessagesPerSource = 1000, int MaxMessagesTotal = 5000, long MaxTotalBytes = 32L * 1024 * 1024)
{
    internal void Validate()
    {
        if (MaxSources < 1 || MaxMessagesPerSource < 1 || MaxMessagesTotal < MaxMessagesPerSource || MaxTotalBytes < 1) throw new ArgumentOutOfRangeException(nameof(ChatStoreOptions.OffRoster));
    }
}

/// <summary>CM-12: a draft may be stored up to 1 MiB of UTF-8 so an over-limit text survives closing the window; the send limit is checked only when sending.</summary>
public static class ClassroomLimits { public const int MaxDraftBytes = 1_048_576; }

public sealed record ClassroomStoredMessage(long LocalSeq, ClassroomPrincipalKey Principal, string Direction, string MessageId, string RawText, string Kind,
    string? Reaction, string? SourceIp, string CreatedUtc, string? LocalReadAt, string? RemoteStoredAt, string? RemoteReadAt);
public sealed record ClassroomMessagePage(IReadOnlyList<ClassroomStoredMessage> Items, long? NextBeforeLocalSeq);
public sealed record ClassroomConversationSummary(ClassroomPrincipalKey Principal, long LastLocalSeq, long Messages, long Unread, string LastText, string LastCreatedUtc);
/// <summary>The current target is resolved when asked (a roster row without an address has none). Never persisted.</summary>
public sealed record ClassroomPreparedSend(string MessageId, ClassroomPrincipalKey Principal, string TargetIp, string RawText, string Kind,
    string? Reaction, string FingerprintHex, int TryCount);
public sealed record PendingClassroomReceipt(long OriginalLocalSeq, string MessageId, ClassroomPrincipalKey Principal, string TargetIp, long PolicyGeneration);
public sealed record ClassroomClearResult(int Messages, int Drafts, int Attempts, int Receipts, long NewEpoch, int LegacyMessages);
