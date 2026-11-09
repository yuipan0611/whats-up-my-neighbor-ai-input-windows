namespace NekoChat.Core.Roster;

/// <summary>IP-only classroom seed; sidecar overrides the embedded default. Automatic seeding never merges existing contacts.</summary>
public static class ClassroomRosterSeed
{
    public static byte[] Load(string? sidecarPath = null)
    {
        if (sidecarPath is null) return Embedded();
        try
        {
            using var input = new FileStream(sidecarPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (input.Length > RosterCsv.MaxBytes) throw new IOException("classroom_roster_too_large");
            byte[] bytes = new byte[checked((int)input.Length)]; input.ReadExactly(bytes);
            if (input.ReadByte() != -1) throw new IOException("classroom_roster_changed");
            return bytes;
        }
        catch (FileNotFoundException) { return Embedded(); }
        catch (DirectoryNotFoundException) { return Embedded(); }
    }

    private static byte[] Embedded()
    {
        using var input = typeof(ClassroomRosterSeed).Assembly.GetManifestResourceStream("NekoChat.Core.Roster.DefaultClassroomRoster.csv")
            ?? throw new InvalidDataException("default_classroom_roster_missing");
        using var bytes = new MemoryStream(); input.CopyTo(bytes); return bytes.ToArray();
    }

    private static RosterCsvRow[] Parse(byte[] bytes)
    {
        var csv = RosterCsv.Parse(bytes);
        if (csv.FatalError is not null || csv.Errors.Count != 0 || csv.Rows.Count == 0 || csv.Rows.Any(row => row.Endpoint is null))
            throw new ArgumentException("invalid_classroom_roster");
        return csv.Rows.ToArray();
    }

    /// Explicit add-only preview: even a matching nickname must never select an existing row for update.
    public static RosterImportPreview PreviewMissing(byte[] bytes, IReadOnlyList<RosterSnapshotRow> current,
        IReadOnlyCollection<string> localAddresses)
    {
        var occupied = current.Select(row => row.Entry.Endpoint).ToHashSet(StringComparer.Ordinal);
        var missing = Parse(bytes).Where(row => !localAddresses.Contains(row.Endpoint!, StringComparer.Ordinal) &&
            !occupied.Contains(row.Endpoint)).ToArray();
        return RosterImportPreview.Create(current, new RosterCsvResult(null, missing),
            missing.Select(row => new RosterImportChoice(row.Line, null)));
    }

    public static RosterImportPreview? Preview(byte[] bytes, IReadOnlyList<RosterSnapshotRow> current,
        IReadOnlyCollection<string> localAddresses)
    {
        if (current.Count != 0) return null;
        // Every classroom package includes everyone. Do not add this machine to its own contacts.
        var peers = Parse(bytes).Where(row => !localAddresses.Contains(row.Endpoint!, StringComparer.Ordinal)).ToArray();
        var filtered = new RosterCsvResult(null, peers);
        return RosterImportPreview.Create(current, filtered);
    }
}
