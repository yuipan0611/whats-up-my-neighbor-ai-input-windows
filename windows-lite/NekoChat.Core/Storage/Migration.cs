using System.Reflection;
using Microsoft.Data.Sqlite;

namespace NekoChat.Core.Storage;

/// <summary>Executes SQL inside the migration's own transaction (BeginTransaction(deferred:false)).</summary>
internal sealed class WriteContext
{
    internal SqliteConnection Conn { get; }
    internal SqliteTransaction Tx { get; }
    internal string NowUtc { get; }
    internal string AppVersion { get; }

    internal WriteContext(SqliteConnection conn, SqliteTransaction tx, string nowUtc, string appVersion)
    {
        Conn = conn; Tx = tx; NowUtc = nowUtc; AppVersion = appVersion;
    }

    internal SqliteCommand Make(string sql, params (string Name, object? Value)[] parameters)
    {
        var cmd = Conn.CreateCommand();
        cmd.Transaction = Tx;
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return cmd;
    }

    internal int Exec(string sql, params (string Name, object? Value)[] parameters)
    {
        using var cmd = Make(sql, parameters);
        return cmd.ExecuteNonQuery();
    }

    internal object? Scalar(string sql, params (string Name, object? Value)[] parameters)
    {
        using var cmd = Make(sql, parameters);
        return cmd.ExecuteScalar();
    }

    internal List<T> Query<T>(string sql, Func<SqliteDataReader, T> map, params (string Name, object? Value)[] parameters)
    {
        using var cmd = Make(sql, parameters);
        using var reader = cmd.ExecuteReader();
        var rows = new List<T>();
        while (reader.Read()) rows.Add(map(reader));
        return rows;
    }
}

internal sealed record Migration(int ToVersion, Action<WriteContext> Apply, bool ForeignKeysOff = false)
{
    /// <summary>Migration 1 = the frozen v4 DDL, embedded byte-for-byte (SchemaV1.sql).</summary>
    internal static Migration V1 { get; } = new(1, ctx =>
    {
        ctx.Exec(ReadSchemaV1(), ("@now", ctx.NowUtc), ("@appVersion", ctx.AppVersion));
    });

    // Q-D12-TOMB: an additive migration keeps the frozen v1 schema byte-for-byte intact.
    internal const string TombstoneSchema = """
        CREATE TABLE ClearedMessage(
          senderId TEXT NOT NULL CHECK (length(senderId) BETWEEN 1 AND 64),
          messageId TEXT NOT NULL CHECK (length(messageId) BETWEEN 1 AND 64),
          clearedUtc TEXT NOT NULL,
          PRIMARY KEY(senderId,messageId)
        ) STRICT;
        """;
    internal static Migration V2 { get; } = new(2, ctx => ctx.Exec(TombstoneSchema));
    internal static IReadOnlyList<Migration> Default { get; } = new[] { V1, V2 };

    internal static string ReadSchemaV1()
    {
        var asm = typeof(Migration).Assembly;
        using var stream = asm.GetManifestResourceStream("NekoChat.Core.Storage.SchemaV1.sql")
            ?? throw new InvalidOperationException("embedded schema resource missing");
        using var reader = new StreamReader(stream, new System.Text.UTF8Encoding(false));
        return reader.ReadToEnd();
    }
}

internal static class StoreIds
{
    internal static bool IsUuid(string? id) => id is not null && Guid.TryParseExact(id, "D", out var value) &&
        string.Equals(id, value.ToString("D"), StringComparison.Ordinal);

    /// <summary>^[A-Za-z0-9_-]{1,64}$ — ASCII only, case-sensitive.</summary>
    internal static bool IsValid(string? id)
    {
        if (string.IsNullOrEmpty(id) || id.Length > 64) return false;
        foreach (var c in id)
        {
            bool ok = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '_' || c == '-';
            if (!ok) return false;
        }
        return true;
    }
}
