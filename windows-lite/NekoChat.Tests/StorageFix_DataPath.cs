using NekoChat.Core.Storage;
using static NekoChat.Tests.StorageFixKit;

namespace NekoChat.Tests;

/// <summary>Review item C: UNC/network data paths are refused, local extended-length paths are not.</summary>
internal static class StorageFix_DataPath
{
    internal static int Run()
    {
        var k = new Counter();
        foreach (var network in new[]
        {
            @"\\server\share\data", "//server/share/data", @"\\server\share", @"\\?\UNC\server\share\data", @"\\.\UNC\server\share\data",
            @"\\?\unc\server\share", "//?/UNC/server/share/data", @"\\?\GLOBALROOT\Device\Mup\server\share", @"\\.\pipe\x", @"\\?\",
        })
        {
            k.Check(ChatStore.IsNetworkDataPath(network), "network/unsupported: " + network);
            k.Check(KindOf(() => ChatStore.Open(new(network)).Dispose()) == StoreErrorKind.DataLocationUnsupported, "Open refuses before touching anything: " + network);
        }
        foreach (var local in new[]
        {
            @"C:\Users\a\AppData\Local\NekoChat.WindowsLite\data", "C:/Users/a/data", @"\\?\C:\Users\a\data", @"\\?\c:\data", @"\\.\D:\data",
            @"\\?\Volume{01234567-89ab-cdef-0123-456789abcdef}\data", "//?/C:/Users/a/data", "/Users/a/data", "relative/data", @"data",
        })
            k.Check(!ChatStore.IsNetworkDataPath(local), "local path is not refused: " + local);

        // The refusal happens before any directory or database is created.
        using var area = new Area();
        string probe = Path.Combine(area.Root, "should-not-exist");
        k.Check(KindOf(() => ChatStore.Open(new(@"\\server\share\" + probe)).Dispose()) == StoreErrorKind.DataLocationUnsupported && !Directory.Exists(probe), "refused path creates nothing");
        // A normal local folder still opens (regression for the predicate change).
        using (var store = ChatStore.Open(new(area.Data))) k.Check(File.Exists(store.DatabasePath), "ordinary local data folder still opens");
        return k.Count;
    }
}
