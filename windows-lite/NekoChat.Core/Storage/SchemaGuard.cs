using Microsoft.Data.Sqlite;

namespace NekoChat.Core.Storage;

internal static class SchemaGuard
{
    private sealed record Definition(string Type, string Name, string Table, string Sql);
    private static readonly Lazy<IReadOnlyList<Definition>> ExpectedV1 = new(() => CreateExpected(1));
    private static readonly Lazy<IReadOnlyList<Definition>> ExpectedV2 = new(() => CreateExpected(2));
    private static readonly Lazy<IReadOnlyList<Definition>> ExpectedV3 = new(() => CreateExpected(3));
    private static readonly Lazy<IReadOnlyList<Definition>> ExpectedV4 = new(() => CreateExpected(4));
    private static readonly Lazy<IReadOnlyList<Definition>> ExpectedV5 = new(() => CreateExpected(5));
    private static readonly Lazy<IReadOnlyList<Definition>> ExpectedV6 = new(() => CreateExpected(6));

    internal static long Inspect(SqliteConnection connection, int latestVersion)
    {
        long version = Scalar(connection, "PRAGMA user_version");
        if (version > latestVersion)
            throw new StoreException(StoreErrorKind.SchemaTooNew, StoreErrors.Describe(StoreErrorKind.SchemaTooNew),
                fromVersion: checked((int)version), toVersion: latestVersion);
        var actual = Definitions(connection);
        if (version == 0 && Scalar(connection, "SELECT count(*) FROM sqlite_master") == 0) return 0;
        var expected = version >= 6 ? ExpectedV6.Value : version == 5 ? ExpectedV5.Value : version == 4 ? ExpectedV4.Value : version == 3 ? ExpectedV3.Value : version == 2 ? ExpectedV2.Value : ExpectedV1.Value;
        var byName = actual.ToDictionary(d => d.Name, StringComparer.Ordinal);
        if (version < 1 || expected.Any(d => !byName.TryGetValue(d.Name, out var found) || found != d) ||
            version <= 6 && actual.Count != expected.Count)
            throw new StoreException(StoreErrorKind.UnrecognizedDatabase, StoreErrors.Describe(StoreErrorKind.UnrecognizedDatabase));
        if (Scalar(connection, "SELECT count(*) FROM SchemaVersion") != version ||
            Scalar(connection, "SELECT coalesce(min(version),0) FROM SchemaVersion") != 1 ||
            Scalar(connection, "SELECT coalesce(max(version),0) FROM SchemaVersion") != version)
            throw new StoreException(StoreErrorKind.Corrupt, "Schema version records are inconsistent.");
        if (version >= 3 && Scalar(connection, "SELECT count(*) FROM ReadReceiptPolicy WHERE singleton=1") != 1)
            throw new StoreException(StoreErrorKind.Corrupt, "Read receipt policy is missing.");
        if (version >= 5 && (Scalar(connection, "SELECT count(*) FROM LocalSecurityRevision WHERE singleton=1") != 1 ||
            Scalar(connection, "SELECT count(*) FROM Peer p WHERE NOT EXISTS(SELECT 1 FROM PeerSecurityRevision s WHERE s.peerId=p.id)") != 0 ||
            Scalar(connection, "SELECT count(*) FROM RosterEntry r WHERE NOT EXISTS(SELECT 1 FROM RosterSecurityRevision s WHERE s.rosterId=r.id)") != 0))
            throw new StoreException(StoreErrorKind.Corrupt, "Security revision records are missing.");
        if (version >= 6 && Scalar(connection, "SELECT count(*) FROM LegacyMessage m WHERE NOT EXISTS(SELECT 1 FROM LegacyScopeRevision s WHERE s.scopeKey=m.scopeKey)") != 0)
            throw new StoreException(StoreErrorKind.Corrupt, "Legacy clear revisions are missing.");
        return version;
    }

    private static long Scalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand(); command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static List<Definition> Definitions(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT type,name,tbl_name,sql FROM sqlite_master WHERE name NOT GLOB 'sqlite_*' ORDER BY type,name";
        using var reader = command.ExecuteReader();
        var result = new List<Definition>();
        while (reader.Read()) result.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2),
            reader.IsDBNull(3) ? "" : reader.GetString(3).Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n')));
        return result;
    }

    private static IReadOnlyList<Definition> CreateExpected(int version)
    {
        // Ask the same SQLite engine to normalize the frozen schema; no disk file is opened or modified.
        using var memory = new SqliteConnection("Data Source=:memory:;Pooling=False");
        memory.Open();
        using var command = memory.CreateCommand(); command.CommandText = Migration.ReadSchemaV1();
        command.Parameters.AddWithValue("@now", "2000-01-01T00:00:00.000Z");
        command.Parameters.AddWithValue("@appVersion", "schema-reference");
        command.ExecuteNonQuery();
        if (version >= 2)
        {
            command.CommandText = Migration.TombstoneSchema;
            command.ExecuteNonQuery();
        }
        if (version >= 3)
        {
            command.CommandText = Migration.ReadPolicySchema;
            command.ExecuteNonQuery();
        }
        if (version >= 4)
        {
            command.CommandText = Migration.LegacyRosterSchema;
            command.ExecuteNonQuery();
        }
        if (version >= 5)
        {
            command.CommandText = SecurityRevisionSchema.Sql;
            command.ExecuteNonQuery();
        }
        if (version >= 6) { command.CommandText = LegacyMessageSchema.Sql; command.ExecuteNonQuery(); }
        return Definitions(memory).AsReadOnly();
    }
}
