using System.Reflection;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using NekoChat.Core.Storage;

namespace NekoChat.Tests;

/// <summary>
/// Runs every internal static class named StorageFix_* (each has `static int Run()`), so a new fix check is a new file
/// and never touches Program.cs again. Ben's review fixes for e03bd0f.
/// </summary>
internal static class StorageFixChecks
{
    internal static int Run()
    {
        int total = 0;
        foreach (var type in typeof(StorageFixChecks).Assembly.GetTypes().Where(t => t.Name.StartsWith("StorageFix_", StringComparison.Ordinal)).OrderBy(t => t.Name, StringComparer.Ordinal))
        {
            var run = type.GetMethod("Run", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public, Type.EmptyTypes)
                ?? throw new InvalidOperationException(type.Name + " has no static Run()");
            int count;
            try { count = (int)run.Invoke(null, null)!; }
            catch (TargetInvocationException e) when (e.InnerException is not null) { throw new Exception(type.Name + ": " + e.InnerException.Message, e.InnerException); }
            Console.WriteLine($"Storage fix {type.Name["StorageFix_".Length..]}: {count} checks passed.");
            total += count;
        }
        return total;
    }
}

/// <summary>Shared helpers for the StorageFix_* checks (the existing Storage*Checks helpers are private).</summary>
internal static class StorageFixKit
{
    internal sealed class Area : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "neko-fix-" + Guid.NewGuid().ToString("N"));
        internal string Data => Path.Combine(Root, "data");
        internal Area() => Directory.CreateDirectory(Root);
        public void Dispose() => Directory.Delete(Root, true);
    }

    internal sealed class Counter
    {
        internal int Count;
        internal void Check(bool value, string name) { Count++; if (!value) throw new Exception(name); }
    }

    internal static string Hash(string path)
    {
        using var s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return Convert.ToHexString(SHA256.HashData(s)).ToLowerInvariant();
    }

    internal static SqliteConnection Connect(string db)
    {
        var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = db, Mode = SqliteOpenMode.ReadWrite, Pooling = false, DefaultTimeout = 5 }.ToString());
        c.Open();
        using var fk = c.CreateCommand(); fk.CommandText = "PRAGMA foreign_keys=ON"; fk.ExecuteNonQuery();
        return c;
    }

    internal static long Number(string db, string sql, params (string, object?)[] p)
    {
        using var c = Connect(db); using var cmd = c.CreateCommand(); cmd.CommandText = sql;
        foreach (var (n, v) in p) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
        return Convert.ToInt64(cmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    internal static string Text(string db, string sql, params (string, object?)[] p)
    {
        using var c = Connect(db); using var cmd = c.CreateCommand(); cmd.CommandText = sql;
        foreach (var (n, v) in p) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
        return Convert.ToString(cmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) ?? "";
    }

    internal static void Execute(string db, string sql, params (string, object?)[] p)
    {
        using var c = Connect(db); using var cmd = c.CreateCommand(); cmd.CommandText = sql;
        foreach (var (n, v) in p) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    internal static StoreErrorKind? KindOf(Action action)
    {
        try { action(); return null; }
        catch (StoreException e) { return e.Kind; }
    }
}
