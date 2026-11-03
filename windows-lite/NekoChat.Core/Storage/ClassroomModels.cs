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
    string? Reaction, string? SourceIp, string CreatedUtc, string? LocalReadAt, string? RemoteStoredAt, string? RemoteReadAt, string? LegacyDeliveredAt = null)
{
    /// <summary>CM-14b: "舊版・已送出" for a v1 peer's delivered. It means neither stored nor read; null for every other message.</summary>
    public string? DeliveryLabel => LegacyDeliveredAt is null ? null : "舊版・已送出";
}
public sealed record ClassroomMessagePage(IReadOnlyList<ClassroomStoredMessage> Items, long? NextBeforeLocalSeq);
public sealed record ClassroomConversationSummary(ClassroomPrincipalKey Principal, long LastLocalSeq, long Messages, long Unread, string LastText, string LastCreatedUtc,
    string? DisplayName = null, bool Removed = false)
{
    /// <summary>CM-02/CM-17: roster name; 名單外・IP for an unknown source; "name（已從名單移除）" (a removed row cannot be sent to) for a deleted row.</summary>
    public string Label => Principal.IsRoster ? Removed ? (DisplayName ?? "") + "（已從名單移除）" : DisplayName ?? "" : "名單外・" + Principal.SourceIp;
    public bool CanSend => !Removed && !(Principal.IsRoster && DisplayName is null);
}
/// <summary>The current target is resolved when asked (a roster row without an address has none). Never persisted.</summary>
public sealed record ClassroomPreparedSend(string MessageId, ClassroomPrincipalKey Principal, string TargetIp, string RawText, string Kind,
    string? Reaction, string FingerprintHex, int TryCount, ClassroomRoute? Route = null);
public enum ClassroomRoute { Classroom, Legacy }
/// <summary>Reason: recorded | already-recorded | not-found | not-sending | stale-attempt | hello-before-attempt | hello-in-future | route-conflict | classroom-may-have-delivered.</summary>
public sealed record ClassroomRouteResult(bool Recorded, string Reason);
public sealed record PendingClassroomReceipt(long OriginalLocalSeq, string MessageId, ClassroomPrincipalKey Principal, string TargetIp, long PolicyGeneration);
/// <summary>Shown before a roster row is deleted (CM-17). Passing it back confirms the delete; if the row's name changed meanwhile the delete is refused.</summary>
public sealed record RosterDeletePreview(bool CanDelete, string Reason, string EntryId, string DisplayName, int ClassroomMessages, int LegacyMessages, string SnapshotToken)
{
    public string Notice => $"刪除「{DisplayName}」後，對話會保留（{ClassroomMessages} 則訊息），顯示為「{DisplayName}（已從名單移除）」，且不能再發送。" +
        (LegacyMessages > 0 ? $"另有 {LegacyMessages} 則舊版訊息會改列在「名單外・IP」。" : "");
    public RosterDeleteConfirmation ToConfirmation() => new(EntryId, DisplayName);
}
public sealed record RosterDeleteConfirmation(string EntryId, string DisplayName);
public sealed record ClassroomClearResult(int Messages, int Drafts, int Attempts, int Receipts, long NewEpoch, int LegacyMessages);
