using System.Text.Json;
using NekoChat.Core.Storage;

namespace NekoChat.Windows;

internal static class OfflineRestoreCommand
{
    internal static int Run(string[] args)
    {
        try
        {
            if (args.Length != 3) throw new ArgumentException("Use --restore-backup <standalone-backup.db> <new-directory>.");
            using var gate = new Mutex(true, "Local\\NekoChat.WindowsLite.App", out bool firstInstance);
            if (!firstInstance) throw new InvalidOperationException("Close NekoChat before restoring a backup.");
            var result = OfflineBackupRestore.Prepare(args[1], args[2]);
            Console.WriteLine(JsonSerializer.Serialize(result));
            return 0;
        }
        catch (Exception error)
        {
            // Fixed metadata only. Never print raw SQL, message bodies, credential references or key material.
            Console.Error.WriteLine(JsonSerializer.Serialize(new { Error = error is StoreException store ? store.Kind.ToString() : error.GetType().Name }));
            return 1;
        }
    }
}
