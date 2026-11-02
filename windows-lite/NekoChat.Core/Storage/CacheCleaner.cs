namespace NekoChat.Core.Storage;

/// <summary>
/// The only code allowed to delete cache content under the settings directory.
/// It touches ONLY the Updates and materials folders; data\ (database), identity\ and settings.json are never deleted.
/// </summary>
public static class CacheCleaner
{
    public static IReadOnlyList<string> CleanableFolders { get; } = Array.AsReadOnly(new[] { "Updates", "materials" });

    public static int Clean(string settingsDirectory)
    {
        int removed = 0;
        foreach (var name in CleanableFolders)
        {
            var dir = Path.Combine(settingsDirectory, name);
            if (!Directory.Exists(dir)) continue;
            if ((File.GetAttributes(dir) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("A cache folder cannot be a symbolic link or junction.");
            foreach (var entry in Directory.EnumerateFileSystemEntries(dir))
            {
                if (Directory.Exists(entry)) Directory.Delete(entry, true); else File.Delete(entry);
                removed++;
            }
        }
        return removed;
    }
}
