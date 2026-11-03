namespace NekoChat.Core.Storage;

/// <summary>The newest attempt of a conversation that a person can still act on (sending, unconfirmed, failed, cancelled). Route is the latest recorded one, for the retry warning only.</summary>
public sealed record ClassroomAttemptSummary(string MessageId, string State, string RawSnapshot, int TryCount, long DraftEpoch, long DraftRevision, ClassroomRoute? Route);

/// <summary>
/// One row of the classroom inbox. Names come from the owning roster principal (or its deletion snapshot), never from message content.
/// Unread counts the classroom and the v1 history of this principal once each; the two sets never overlap.
/// </summary>
public sealed record ClassroomInboxEntry(ClassroomPrincipalKey Principal, string? RosterEntryId, string? DisplayName, string? StudentNumber, bool IsTeacher, bool Removed,
    string? Endpoint, bool CanSend, long ClassroomUnread, long LegacyUnread, string? LatestText, string? LatestUtc, bool LatestIsLegacy,
    DraftState Draft, ClassroomAttemptSummary? Attempt, LegacyHistoryScope? LegacyScope)
{
    public long TotalUnread => checked(ClassroomUnread + LegacyUnread);
    /// <summary>CM-02 / CM-17: the roster name, "name（已從名單移除）" for a deleted row, or 名單外・IP.</summary>
    public string Label => Principal.IsRoster ? Removed ? (DisplayName ?? "") + "（已從名單移除）" : DisplayName ?? "" : "名單外・" + Principal.SourceIp;
}

public sealed class ClassroomInboxSnapshot
{
    public IReadOnlyList<ClassroomInboxEntry> Entries { get; }
    public long TotalUnread { get; }
    internal ClassroomInboxSnapshot(IEnumerable<ClassroomInboxEntry> entries)
    {
        Entries = Array.AsReadOnly(entries.ToArray());
        TotalUnread = checked(Entries.Sum(e => e.TotalUnread));   // entries are distinct principals and their legacy scopes are disjoint
    }
}
