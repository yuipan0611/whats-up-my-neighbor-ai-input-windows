namespace NekoChat.Core.Roster;

/// <summary>Private distribution sidecar. Only seeds an empty roster; never merges or replaces user contacts.</summary>
public static class ClassroomRosterSeed
{
    public static RosterImportPreview? Preview(byte[] bytes, IReadOnlyList<RosterSnapshotRow> current,
        IReadOnlyCollection<string> localAddresses)
    {
        if (current.Count != 0) return null;
        var csv = RosterCsv.Parse(bytes);
        if (csv.FatalError is not null || csv.Errors.Count != 0 || csv.Rows.Count == 0 || csv.Rows.Any(r => r.Endpoint is null))
            throw new ArgumentException("invalid_classroom_roster");
        // Every classroom package includes everyone. Do not add this machine to its own contacts.
        var peers = csv.Rows.Where(r => !localAddresses.Contains(r.Endpoint!, StringComparer.Ordinal)).ToArray();
        var filtered = new RosterCsvResult(null, peers);
        return RosterImportPreview.Create(current, filtered);
    }
}
