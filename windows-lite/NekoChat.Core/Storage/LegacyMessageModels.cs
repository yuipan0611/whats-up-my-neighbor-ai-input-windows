namespace NekoChat.Core.Storage;

/// <summary>Separate unverified history under a roster row, or under an unmatched source IP. Never a Peer/conversation identity.</summary>
public sealed record LegacyHistoryScope(string? RosterEntryId, string? UnknownSourceIp)
{
    public string ContextId => RosterEntryId is { } row ? "legacy-row:" + row : "legacy-ip:" + UnknownSourceIp;
}
public sealed record LegacyReceiveContext(string SourceIp, string? RosterEntryId, long? RosterRevision, long ClearRevision)
{
    public LegacyHistoryScope Scope => RosterEntryId is { } row ? new(row, null) : new(null, SourceIp);
}
public sealed record LegacyReceiveResult(bool Stored, string Reason, long? LegacySeq = null);
public sealed record LegacyStoredMessage(long LegacySeq, string SourceIp, string? RosterEntryId, string RawText, string? Reaction, string ReceivedUtc, string? LocalReadAt)
{ public string TrustLabel => "舊版・未驗證"; }
public sealed record LegacyMessagePage(IReadOnlyList<LegacyStoredMessage> Items, long? NextBeforeLegacySeq);
public sealed record LegacyHistorySummary(LegacyHistoryScope Scope, long LastLegacySeq, long Messages, long Unread, string LastText, string? LastReaction, string LastReceivedUtc);
public sealed record LegacyClearResult(int Messages, bool WalTruncated);
