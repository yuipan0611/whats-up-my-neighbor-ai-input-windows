namespace NekoChat.Core.Storage;

public sealed record DraftState(string? RawText, long Epoch, long Revision);
public sealed record DraftSaveResult(bool Saved, string Reason, long CurrentEpoch, long CurrentRevision);
public abstract record BeginSendResult;
public sealed record AttemptStarted(string MessageId, bool ReusedId, bool PreviousAttemptMayHaveDelivered, int TryCount, string RawSnapshot) : BeginSendResult;
public sealed record AlreadySending(string MessageId) : BeginSendResult;
public sealed record NothingToRetry : BeginSendResult;
public sealed record AttemptSuperseded(string MessageId) : BeginSendResult;
public sealed record StaleDraft(long CurrentEpoch, long CurrentRevision) : BeginSendResult;
public sealed record InvalidSend(string Reason) : BeginSendResult;
public sealed record PreparedSend(string MessageId, string SenderId, string RecipientId, string RawText, string Kind,
    string? Reaction, string FingerprintHex, int TryCount);
public abstract record AttemptOutcome;
/// <summary>Transport has verified the ACK signature and all bindings. Remote time is informational; persistence uses local time.</summary>
public sealed record RemoteStoredOutcome(DateTime RemoteStoredUtc, byte[] AckContentFingerprint) : AttemptOutcome;
public sealed record UnconfirmedOutcome : AttemptOutcome;
public sealed record FailedOutcome(string Code) : AttemptOutcome;
public abstract record CompleteResult;
public sealed record Confirmed(long LocalSeq, bool DraftCleared) : CompleteResult;
public sealed record AlreadyConfirmed(long LocalSeq) : CompleteResult;
public sealed record StateUpdated(string NewState) : CompleteResult;
public sealed record AckMismatch : CompleteResult;
public sealed record Discarded(string Reason) : CompleteResult;
public sealed record CancelResult(bool Cancelled, string Reason);
public enum RemoteReceiptKind { Stored, Read }
public sealed record ReceiptApplyResult(bool Changed, string Reason, long? LocalSeq = null);
