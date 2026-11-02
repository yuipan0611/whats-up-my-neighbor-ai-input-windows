namespace NekoChat.Core.Storage;

public sealed record PendingSendSummary(string MessageId, string State, string RawSnapshot, int TryCount, long DraftEpoch, long DraftRevision);
public sealed record InboxEntry(string Id, string? RosterId, string DisplayName, string? StudentNumber, bool IsTeacher,
    string? Endpoint, string? PeerId, string? ConversationId, PairingState? Pairing, long Unread, long LegacyUnread,
    string? LastText, string? LastUtc, bool LastIsLegacy, DraftState? Draft, PendingSendSummary? Attempt, LegacyHistoryScope? LegacyScope)
{
    public long TotalUnread => checked(Unread + LegacyUnread);
    public string NotificationName => RosterId is not null ? DisplayName : PeerId is not null ? "未命名同學" : "未驗證來源";
}
public sealed class InboxSnapshot
{
    public IReadOnlyList<InboxEntry> Entries { get; }
    public long TotalUnread { get; }
    internal InboxSnapshot(IEnumerable<InboxEntry> entries)
    {
        Entries = Array.AsReadOnly(entries.ToArray());
        TotalUnread = checked(Entries.Where(e => e.ConversationId is not null).GroupBy(e => e.ConversationId, StringComparer.Ordinal).Sum(g => g.First().Unread) +
            Entries.Where(e => e.LegacyScope is not null).GroupBy(e => e.LegacyScope!.ContextId, StringComparer.Ordinal).Sum(g => g.First().LegacyUnread));
    }
}
