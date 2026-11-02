using NekoChat.Core.Roster;

namespace NekoChat.Core.Storage;

public sealed partial class ChatStore
{
    /// <summary>Read-only. A completed empty import is still completed.</summary>
    public bool LegacyRosterImportCompleted() => Read(c => {
        using var cmd = c.CreateCommand(); cmd.CommandText = "SELECT count(*) FROM LegacyRosterImportState WHERE singleton=1";
        return Convert.ToInt64(cmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) == 1;
    });

    /// <summary>Local bootstrap only. Labels never create trust, and settings are not opened or modified here.</summary>
    public LegacyRosterImportResult ImportLegacyRoster(IReadOnlyList<LegacyRosterContact> contacts)
    {
        ArgumentNullException.ThrowIfNull(contacts);
        if (contacts.Count > RosterCsv.MaxRows) throw new ArgumentOutOfRangeException(nameof(contacts));
        var input = contacts.Select(c => (Name: c?.Name?.Trim(), Endpoint: c?.Ip?.Trim())).ToArray();
        var duplicates = input.Where(r => r.Endpoint is not null).GroupBy(r => r.Endpoint!, StringComparer.Ordinal)
            .Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet(StringComparer.Ordinal);
        return Write("legacy-roster", ctx => {
            if (Convert.ToInt64(ctx.Scalar("SELECT count(*) FROM LegacyRosterImportState WHERE singleton=1"), System.Globalization.CultureInfo.InvariantCulture) == 1)
                return (new LegacyRosterImportResult(true, 0, 0), false);
            var used = ReadRoster(ctx).Where(r => r.Entry.Endpoint is not null).Select(r => r.Entry.Endpoint!).ToHashSet(StringComparer.Ordinal);
            int added = 0, skipped = 0;
            foreach (var row in input)
            {
                if (!RosterFields.ValidName(row.Name) || row.Endpoint is null || !AllowedRosterEndpoint(row.Endpoint) || duplicates.Contains(row.Endpoint) || !used.Add(row.Endpoint))
                { skipped++; continue; }
                ctx.Exec("INSERT INTO RosterEntry(id,displayName,endpoint,source) VALUES(@id,@name,@endpoint,'legacySettings')",
                    ("@id", NewRosterId(ctx)), ("@name", row.Name), ("@endpoint", row.Endpoint));
                added++;
            }
            ctx.Exec("INSERT INTO LegacyRosterImportState(singleton,completedUtc) VALUES(1,@now)", ("@now", ctx.NowUtc));
            return (new LegacyRosterImportResult(false, added, skipped), true);
        });
    }
}
