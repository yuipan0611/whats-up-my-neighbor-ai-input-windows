namespace NekoChat.Core.Storage;

public sealed record ClassroomSubnetMigrationResult(int Updated, int Conflicts);

public sealed partial class ChatStore
{
    /// Runs before networking starts. Stable roster IDs keep conversations, messages and drafts attached.
    public ClassroomSubnetMigrationResult MigrateClassroomSubnet(IReadOnlyCollection<string> localAddresses)
    {
        ArgumentNullException.ThrowIfNull(localAddresses);
        if (!ClassroomSubnet.IsCurrentNetwork(localAddresses)) return new(0, 0);
        return Write("classroom-subnet-21", ctx => {
            var rows = ReadRoster(ctx);
            var occupied = rows.Select(row => row.Entry.Endpoint).ToHashSet(StringComparer.Ordinal);
            int updated = 0, conflicts = 0, auxiliary = 0;
            foreach (var row in rows)
            {
                var entry = row.Entry;
                string? next = ClassroomSubnet.UpgradeAddress(entry.Endpoint);
                if (next == entry.Endpoint) continue;
                if (occupied.Contains(next)) { conflicts++; continue; }
                ctx.Exec("UPDATE RosterEntry SET endpoint=@next, displayName=CASE WHEN displayName=@old THEN @next ELSE displayName END WHERE id=@id",
                    ("@next", next), ("@old", entry.Endpoint), ("@id", entry.Id));
                if (_migrations.Count >= 14)
                    ctx.Exec("UPDATE ClassroomAutoSource SET ip=@next WHERE ip=@old AND rosterEntryId=@id AND NOT EXISTS(SELECT 1 FROM ClassroomAutoSource WHERE ip=@next)",
                        ("@next", next), ("@old", entry.Endpoint), ("@id", entry.Id));
                occupied.Add(next); updated++;
            }
            // Keep removal/block decisions effective after moving; never rewrite historical message/source addresses.
            if (_migrations.Count >= 14)
                auxiliary += ctx.Exec("UPDATE ClassroomArchivedContact SET endpoint='192.168.21.' || substr(endpoint,12) WHERE endpoint GLOB '192.168.24.*'");
            if (_migrations.Count >= 15)
            {
                // A collision left its active contact at the old IP: its block must stay there too.
                auxiliary += ctx.Exec("INSERT OR IGNORE INTO BlockedSource(ip,rosterEntryId,displayName,blockedUtc) SELECT '192.168.21.' || substr(ip,12),rosterEntryId,displayName,blockedUtc FROM BlockedSource WHERE ip GLOB '192.168.24.*' AND NOT EXISTS(SELECT 1 FROM RosterEntry WHERE endpoint=BlockedSource.ip)");
                auxiliary += ctx.Exec("DELETE FROM BlockedSource WHERE ip GLOB '192.168.24.*' AND NOT EXISTS(SELECT 1 FROM RosterEntry WHERE endpoint=BlockedSource.ip)");
            }
            return (new ClassroomSubnetMigrationResult(updated, conflicts), updated + auxiliary > 0);
        });
    }
}
