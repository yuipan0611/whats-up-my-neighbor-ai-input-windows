namespace NekoChat.Core.Storage;

/// <summary>Local labels and candidate endpoint only; no identity or trust fields.</summary>
public sealed record RosterEdit(string Name, string? StudentNumber = null, bool IsTeacher = false, string? Endpoint = null);
public sealed record RosterMutationResult(bool Changed, string Reason, string? EntryId, string SnapshotToken);
public sealed record RosterApplyResult(bool Applied, string Reason, int Added, int Updated, int Unchanged, int Skipped, string SnapshotToken)
{ public int MergedMessages { get; init; } }
public sealed record LegacyRosterContact(string Name, string Ip);
public sealed record LegacyRosterImportResult(bool AlreadyCompleted, int Added, int Skipped);
