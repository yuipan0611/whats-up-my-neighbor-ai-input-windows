using NekoChat.Core.Roster;

namespace NekoChat.Core.Storage;

/// <summary>
/// Read-only answer to "what happens if I save this roster row" (classroom mode, CM-02/03/04/05).
/// Nothing is written; <see cref="SnapshotToken"/> and the merge count are rechecked when the user confirms.
/// </summary>
public sealed record RosterEditPreview(bool CanSave, string Reason, string? ConflictEntryId, string? ConflictName,
    RosterAddressChange AddressChange, string? Endpoint, int UnknownMessagesToMerge, string SnapshotToken)
{
    /// <summary>Shown to the user before they confirm; null when there is nothing to merge.</summary>
    public string? MergeNotice => UnknownMessagesToMerge > 0 ? $"此 IP 的 {UnknownMessagesToMerge} 則名單外訊息會併入" : null;
    public string? AddressChangeText => AddressChange == RosterAddressChange.Cleared ? RosterImportItem.ClearAddressText : null;
    /// <summary>The consent a UI passes back after the user confirmed the merge notice; null when nothing is to be merged.</summary>
    public LegacyMergeConsent? ToMergeConsent() => CanSave && Endpoint is not null && UnknownMessagesToMerge > 0 ? new(Endpoint, UnknownMessagesToMerge) : null;
}

/// <summary>The user saw "N 則名單外訊息會併入" and agreed. If the count changed since, the save is refused so nothing moves silently.</summary>
public sealed record LegacyMergeConsent(string Endpoint, int ExpectedMessages);

public sealed record RosterSaveResult(RosterMutationResult Mutation, int MergedMessages);

/// <summary>Whether a message may be sent to this roster row. A row without an address cannot be sent to (CM-04).</summary>
public sealed record RosterSendTarget(bool CanSend, string Reason, string? Endpoint);
