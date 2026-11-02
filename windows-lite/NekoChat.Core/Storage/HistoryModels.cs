namespace NekoChat.Core.Storage;

public sealed record StoredMessage(long LocalSeq, string ConversationId, string Direction, string SenderId, string RecipientId,
    string MessageId, string RawText, string Kind, string? Reaction, string FingerprintHex, string CreatedUtc,
    string? LocalReadAt, string? RemoteStoredAt, string? RemoteReadAt);
public sealed record MessagePage(IReadOnlyList<StoredMessage> Items, long? NextBeforeLocalSeq);
public sealed record ClearResult(int Messages, int Drafts, int Attempts, int Receipts, bool WalTruncated, long NewDraftEpoch);
