using NekoChat.Core.Roster;
using NekoChat.Core.Storage;
using static NekoChat.Tests.StorageFixKit;

namespace NekoChat.Tests;

/// <summary>Review item G: legacy-settings roster rows use the frozen N001A-v4 §15 source name.</summary>
internal static class StorageFix_SourceName
{
    internal static int Run()
    {
        var k = new Counter();
        using var area = new Area();
        using var store = ChatStore.Open(new(area.Data));
        var result = store.ImportLegacyRoster(new[] { new LegacyRosterContact("Alice", "10.0.0.1"), new LegacyRosterContact("Bob", "10.0.0.2") });
        k.Check(result.Added == 2, "two legacy contacts imported");
        k.Check(Number(store.DatabasePath, "SELECT count(*) FROM RosterEntry WHERE source='legacySettings'") == 2 && Number(store.DatabasePath, "SELECT count(*) FROM RosterEntry WHERE source='legacy'") == 0, "source is legacySettings (v4 §15), not 'legacy'");
        k.Check(store.GetRosterSnapshot().All(r => r.Source == "legacySettings" && r.Entry.PeerId is null), "snapshot reports the same source; labels never bind a peer");
        return k.Count;
    }
}
