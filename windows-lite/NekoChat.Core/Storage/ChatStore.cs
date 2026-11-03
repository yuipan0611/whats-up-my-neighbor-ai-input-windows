using System.Globalization;
using Microsoft.Data.Sqlite;

namespace NekoChat.Core.Storage;

public sealed partial class ChatStore : IChatStore
{
    private const string DbFileName = "nekochat.db";
    private static readonly TimeSpan MessagePathThrottle = TimeSpan.FromSeconds(30);

    private readonly ChatStoreOptions _options;
    private readonly IReadOnlyList<Migration> _migrations;
    private readonly string _dbPath;
    private readonly object _gate = new();
    private SqliteConnection _write = null!;
    private bool _disposed;
    private bool _lastCheckpointBusy;
    private int _lastCheckpointLog;
    private DateTime? _lastCheckpointUtc;
    private int _recoveredAttempts;
    private int _unknownPairingStates;

    private ChatStore(ChatStoreOptions options)
    {
        _options = options;
        _migrations = options.Migrations ?? Migration.Default;
        _dbPath = Path.Combine(options.DataDirectory, DbFileName);
        for (int i = 0; i < _migrations.Count; i++)
            if (_migrations[i].ToVersion != i + 1) throw new InvalidOperationException("migrations must be contiguous starting at 1");
    }

    internal int LatestVersion => _migrations.Count;
    public string DatabasePath => _dbPath;

    // ───────────────────────── open ─────────────────────────

    public static ChatStore Open(ChatStoreOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.DataDirectory)) throw new ArgumentException("DataDirectory is required", nameof(options));
        if (options.DataDirectory.StartsWith("\\\\", StringComparison.Ordinal) || options.DataDirectory.StartsWith("//", StringComparison.Ordinal))
            throw new StoreException(StoreErrorKind.DataLocationUnsupported, StoreErrors.Describe(StoreErrorKind.DataLocationUnsupported));
        if (options.CommandTimeoutSeconds < 1) throw new ArgumentOutOfRangeException(nameof(options));
        var store = new ChatStore(options);
        try
        {
            store.Initialize();
            return store;
        }
        catch
        {
            store.AbortInitialize();
            throw;
        }
    }

    private void Initialize()
    {
        Guard(() =>
        {
            try { Directory.CreateDirectory(_options.DataDirectory); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                throw new StoreException(StoreErrorKind.CannotOpen, StoreErrors.Describe(StoreErrorKind.CannotOpen), e);
            }
            // Inspect existing files read-only, before opening a write handle or applying persistent PRAGMAs.
            if (File.Exists(_dbPath))
            {
                using var inspect = NewConnection(create: false, readOnly: true);
                SchemaGuard.Inspect(inspect, LatestVersion);
            }
            _write = NewConnection(create: true);
            long version = SchemaGuard.Inspect(_write, LatestVersion);
            int latest = LatestVersion;

            ApplyPragmas(_write);
            if (version == 0)
            {
                // Brand-new database: WAL is set once here (it is persistent).
                RequireWal(ScalarString(_write, "PRAGMA journal_mode=WAL"));
            }
            else
            {
                // Necessary, not sufficient: WAL working does not prove the folder is local.
                RequireWal(ScalarString(_write, "PRAGMA journal_mode"));
                CheckSchemaVersionTable(version);
                if (version < latest) BackupBeforeUpgrade((int)version);
            }

            RunMigrations((int)version);

            // 4. recovery: no network, no sending. sending → unconfirmed.
            _recoveredAttempts = Write("recover", ctx =>
                (ctx.Exec("UPDATE ManualSendAttempt SET attemptState='unconfirmed', updatedUtc=@now WHERE attemptState='sending'", ("@now", ctx.NowUtc)), true));
            CheckWalSize("open");
            _ = MaintainBackups();
            return 0;
        });
    }

    private void AbortInitialize()
    {
        lock (_gate)
        {
            _disposed = true;
            _write?.Dispose();
        }
    }

    private void RequireWal(string mode)
    {
        if (!string.Equals(mode, "wal", StringComparison.OrdinalIgnoreCase))
            throw new StoreException(StoreErrorKind.DataLocationUnsupported, StoreErrors.Describe(StoreErrorKind.DataLocationUnsupported));
    }

    private void CheckSchemaVersionTable(long version)
    {
        long tables = ScalarLong(_write, "SELECT count(*) FROM sqlite_master WHERE type='table' AND name='SchemaVersion'");
        if (tables != 1) throw new StoreException(StoreErrorKind.Corrupt, "SchemaVersion table is missing.");
        long max = ScalarLong(_write, "SELECT coalesce(max(version),0) FROM SchemaVersion");
        if (max != version) throw new StoreException(StoreErrorKind.Corrupt, "SchemaVersion does not match user_version.");
    }

    private void ApplyPragmas(SqliteConnection c)
    {
        ExecRaw(c, "PRAGMA foreign_keys=ON");
        ExecRaw(c, "PRAGMA synchronous=FULL");
        ExecRaw(c, "PRAGMA secure_delete=ON");
        ExecRaw(c, "PRAGMA trusted_schema=OFF");
        ExecRaw(c, "PRAGMA journal_size_limit=4194304");   // per connection, NOT persistent, NOT a WAL cap
        if (_options.MaxPageCount is int pages) ExecRaw(c, "PRAGMA max_page_count=" + pages.ToString(CultureInfo.InvariantCulture));
    }

    private SqliteConnection NewConnection(bool create, bool readOnly = false)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = _dbPath,
            Mode = readOnly ? SqliteOpenMode.ReadOnly : create ? SqliteOpenMode.ReadWriteCreate : SqliteOpenMode.ReadWrite,
            Pooling = false,
            DefaultTimeout = _options.CommandTimeoutSeconds,   // the single source of lock-wait time
        };
        var connection = new SqliteConnection(builder.ToString());
        try
        {
            connection.Open();
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    // ───────────────────────── migrations ─────────────────────────

    private void BackupBeforeUpgrade(int fromVersion)
    {
        CreateStoreBackup("v" + fromVersion.ToString(CultureInfo.InvariantCulture), _write);
    }

    private void RunMigrations(int fromVersion)
    {
        int current = fromVersion;
        foreach (var m in _migrations.Where(x => x.ToVersion > fromVersion).OrderBy(x => x.ToVersion))
        {
            try
            {
                if (m.ForeignKeysOff) ExecRaw(_write, "PRAGMA foreign_keys=OFF");   // must be outside the transaction
                using var tx = _write.BeginTransaction(deferred: false);
                var ctx = new WriteContext(_write, tx, Iso(_options.UtcNow()), _options.AppVersion);
                m.Apply(ctx);
                long recorded = Convert.ToInt64(ctx.Scalar("PRAGMA user_version"), CultureInfo.InvariantCulture);
                if (recorded != m.ToVersion)
                {
                    ctx.Exec("INSERT INTO SchemaVersion(version, appliedUtc, appVersion) VALUES (@v, @t, @a)",
                        ("@v", m.ToVersion), ("@t", ctx.NowUtc), ("@a", ctx.AppVersion));
                    ctx.Exec("PRAGMA user_version = " + m.ToVersion.ToString(CultureInfo.InvariantCulture));
                }
                bool violations;
                using (var check = ctx.Make("PRAGMA foreign_key_check"))
                using (var reader = check.ExecuteReader()) violations = reader.Read();
                if (violations) throw new InvalidOperationException("foreign key violations after migration");
                tx.Commit();
                current = m.ToVersion;
            }
            catch (Exception e)
            {
                throw new StoreException(StoreErrorKind.MigrationFailed, StoreErrors.Describe(StoreErrorKind.MigrationFailed), e, fromVersion: current, toVersion: m.ToVersion);
            }
            finally
            {
                if (m.ForeignKeysOff) ExecRaw(_write, "PRAGMA foreign_keys=ON");
            }
        }
    }

    // ───────────────────────── helpers ─────────────────────────

    private static string Iso(DateTime utc) => utc.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    private static bool TryParseIso(string? text, out DateTime utc) =>
        DateTime.TryParseExact(text, "yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out utc);

    private static void ExecRaw(SqliteConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static object? ScalarRaw(SqliteConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteScalar();
    }

    private static long ScalarLong(SqliteConnection c, string sql) => Convert.ToInt64(ScalarRaw(c, sql), CultureInfo.InvariantCulture);
    private static string ScalarString(SqliteConnection c, string sql) => Convert.ToString(ScalarRaw(c, sql), CultureInfo.InvariantCulture) ?? "";

    private static T Guard<T>(Func<T> body)
    {
        try { return body(); }
        catch (SqliteException e) { throw StoreErrors.Map(e); }
        catch (Exception e) when (e is DllNotFoundException or TypeInitializationException) { throw StoreErrors.NativeMissing(e); }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(ChatStore));
    }

    /// <summary>One write transaction: BeginTransaction(deferred:false) takes the write lock at BEGIN (verified, N001A §1.5). Non-success results roll back unless the body says otherwise.</summary>
    private T Write<T>(string op, Func<WriteContext, (T Value, bool Commit)> body, Func<bool>? commitGuard = null, Func<T>? guardRejected = null, Func<WriteContext, bool>? transactionGuard = null)
    {
        if ((commitGuard is not null || transactionGuard is not null) != (guardRejected is not null)) throw new ArgumentException("Both commit guard and rejection result are required.");
        lock (_gate)
        {
            ThrowIfDisposed();
            try
            {
                using var tx = _write.BeginTransaction(deferred: false);
                _options.Faults?.AfterBegin(op);
                var ctx = new WriteContext(_write, tx, Iso(_options.UtcNow()), _options.AppVersion);
                if (transactionGuard is not null && !transactionGuard(ctx)) { tx.Rollback(); return guardRejected!(); }
                var (value, commit) = body(ctx);
                if (commit) _options.Faults?.BeforeCommit(op);
                // Wire duplicates/discards also require a current trust/session fence before returning an ACK-able result.
                if (transactionGuard is not null && !transactionGuard(ctx)) { tx.Rollback(); return guardRejected!(); }
                if (commit)
                {
                    if (commitGuard is not null && !commitGuard())
                    {
                        tx.Rollback(); return guardRejected!();
                    }
                    tx.Commit();
                    _options.Faults?.AfterCommit(op);
                }
                else
                {
                    tx.Rollback();
                }
                return value;
            }
            catch (SqliteException e) { throw StoreErrors.Map(e); }
            catch (Exception e) when (e is DllNotFoundException or TypeInitializationException) { throw StoreErrors.NativeMissing(e); }
        }
    }

    /// <summary>Short-lived read connection: no transaction is ever kept across calls, so no long reader can pin the WAL.</summary>
    private T Read<T>(Func<SqliteConnection, T> body, [System.Runtime.CompilerServices.CallerMemberName] string operation = "")
    {
        ThrowIfDisposed();
        return Guard(() =>
        {
            using var c = NewConnection(create: false);
            ApplyPragmas(c);
            ExecRaw(c, "PRAGMA query_only=ON");
            T result = body(c);
            _options.Faults?.AfterRead(operation);
            return result;
        });
    }

    private void CheckWalSize(string where)
    {
        long wal = FileLength(_dbPath + "-wal");
        if (wal > 16L * 1024 * 1024) _options.DiagnosticSink?.Invoke($"wal-size bytes={wal} at={where}");
    }

    private static long FileLength(string path)
    {
        var info = new FileInfo(path);
        return info.Exists ? info.Length : 0;
    }

    // ───────────────────────── identity ─────────────────────────

    public string EnsureLocalIdentity(string? id = null, byte[]? publicKey = null, string? credentialRef = null)
    {
        if (id != null && !StoreIds.IsValid(id)) throw new ArgumentException("invalid identity id", nameof(id));
        if (publicKey is { Length: 0 }) throw new ArgumentException("publicKey must not be empty", nameof(publicKey));
        if (credentialRef is { Length: 0 }) throw new ArgumentException("credentialRef must not be empty", nameof(credentialRef));
        return Write("ensure-identity", ctx =>
        {
            var existing = ctx.Query("SELECT id FROM LocalIdentity WHERE singleton = 1", r => r.GetString(0));
            if (existing.Count > 0) return (existing[0], false);   // never overwrite
            string newId = id ?? _options.NewId();
            if (!StoreIds.IsValid(newId)) throw new ArgumentException("invalid identity id", nameof(id));
            ctx.Exec("INSERT INTO LocalIdentity(singleton, id, publicKey, credentialRef, createdUtc) VALUES (1, @id, @pk, @cr, @now)",
                ("@id", newId), ("@pk", publicKey), ("@cr", credentialRef), ("@now", ctx.NowUtc));
            return (newId, true);
        });
    }

    public ReplaceIdentityResult ReplaceLocalIdentity(string newId, byte[]? newPublicKey, string? newCredentialRef)
        => ReplaceLocalIdentityCore(newId, newPublicKey, newCredentialRef, null, null);

    private ReplaceIdentityResult ReplaceLocalIdentityCore(string newId, byte[]? newPublicKey, string? newCredentialRef, LocalIdentitySnapshot? expected, Func<bool>? stillCurrent, bool suspendPairings = false)
    {
        if (!StoreIds.IsValid(newId)) throw new ArgumentException("invalid identity id", nameof(newId));
        if (newPublicKey is { Length: 0 }) throw new ArgumentException("publicKey must not be empty", nameof(newPublicKey));
        if (newCredentialRef is { Length: 0 }) throw new ArgumentException("credentialRef must not be empty", nameof(newCredentialRef));
        return Write<ReplaceIdentityResult>("replace-identity", ctx =>
        {
            if (expected is not null && (!stillCurrent!() || !LocalIdentityMatches(ctx, expected))) return (new IdentityChangeDiscarded("stale-identity"), false);
            var current = ctx.Query("SELECT id, publicKey, createdUtc FROM LocalIdentity WHERE singleton = 1",
                r => (Id: r.GetString(0), Key: r.IsDBNull(1) ? null : (byte[])r.GetValue(1), Created: r.GetString(2)));
            if (current.Count == 0) return (new NoIdentityToReplace(), false);
            var old = current[0];
            if (string.Equals(old.Id, newId, StringComparison.Ordinal)) return (new IdentityUnchanged(), false);
            if (Convert.ToInt64(ctx.Scalar("SELECT count(*) FROM LocalIdentityHistory WHERE id = @id", ("@id", newId)), CultureInfo.InvariantCulture) > 0)
                return (new IdentityReused(), false);

            ctx.Exec("UPDATE LocalIdentity SET id = @id, publicKey = @pk, credentialRef = @cr, createdUtc = @now WHERE singleton = 1",
                ("@id", newId), ("@pk", newPublicKey), ("@cr", newCredentialRef), ("@now", ctx.NowUtc));
            ctx.Exec("INSERT INTO LocalIdentityHistory(id, publicKey, createdUtc, retiredUtc, reason) VALUES (@id, @pk, @c, @now, 'replaced')",
                ("@id", old.Id), ("@pk", old.Key), ("@c", old.Created), ("@now", ctx.NowUtc));
            int superseded = ctx.Exec("UPDATE ManualSendAttempt SET attemptState = 'superseded', updatedUtc = @now WHERE attemptState IN ('sending','unconfirmed','failed')", ("@now", ctx.NowUtc));
            int dropped = ctx.Exec("DELETE FROM ReceiptOutbox");
            if (suspendPairings)
            {
                ctx.Exec("UPDATE Peer SET pairingState='suspended' WHERE pairingState='paired'");
                ctx.Exec("UPDATE ReadReceiptPolicy SET enabled=0,generation=CASE WHEN enabled=1 AND generation<9223372036854775807 THEN generation+1 ELSE generation END WHERE singleton=1");
            }
            return (new IdentityReplaced(old.Id, newId, superseded, dropped), true);
        }, commitGuard: stillCurrent, guardRejected: stillCurrent is null ? null : () => new IdentityChangeDiscarded("stale-identity"));
    }

    public IdentityCheckResult CheckLocalIdentity(IIdentityKeyProbe probe, Func<byte[], string>? deriveId = null)
    {
        ArgumentNullException.ThrowIfNull(probe);
        var rows = Read(c =>
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT id, publicKey, credentialRef FROM LocalIdentity WHERE singleton = 1";
            using var r = cmd.ExecuteReader();
            var list = new List<(string Id, byte[]? Key, string? Ref)>();
            while (r.Read()) list.Add((r.GetString(0), r.IsDBNull(1) ? null : (byte[])r.GetValue(1), r.IsDBNull(2) ? null : r.GetString(2)));
            return list;
        });
        if (rows.Count == 0) return new IdentityLost("no-row");
        var (id, key, credentialRef) = rows[0];
        if (key == null && credentialRef == null) return new IdentityNotProvisioned();
        if (key == null || credentialRef == null) return new IdentityLost("incomplete-identity");
        try
        {
            if (!probe.KeyExists(credentialRef)) return new IdentityLost("key-missing");
            if (!probe.TryGetPublicKey(credentialRef, out var actual) || actual == null || actual.Length == 0) return new IdentityLost("public-key-unreadable");
            if (!actual.AsSpan().SequenceEqual(key)) return new IdentityMismatch("public-key-differs");
            if (deriveId != null && !string.Equals(deriveId(key), id, StringComparison.Ordinal)) return new IdentityMismatch("id-not-derived");
            if (!probe.CanSign(credentialRef)) return new IdentityLost("cannot-sign");
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            _options.DiagnosticSink?.Invoke("identity-probe-failed type=" + e.GetType().Name);
            return new IdentityLost("probe-failed");
        }
        return new IdentityConsistent();
    }

    // ───────────────────────── peers ─────────────────────────

    public RegisterPeerResult RegisterPeer(string peerId, PairingState pairingState, byte[]? publicKey = null)
    {
        if (!StoreIds.IsValid(peerId)) throw new ArgumentException("invalid peer id", nameof(peerId));
        if (!Enum.IsDefined(pairingState)) throw new ArgumentOutOfRangeException(nameof(pairingState));
        if (publicKey is { Length: 0 }) throw new ArgumentException("publicKey must not be empty", nameof(publicKey));
        return Write<RegisterPeerResult>("register-peer", ctx =>
        {
            var peer = ctx.Query("SELECT publicKey FROM Peer WHERE id = @id", r => r.IsDBNull(0) ? null : (byte[])r.GetValue(0), ("@id", peerId));
            bool created = peer.Count == 0;
            if (created)
            {
                ctx.Exec("INSERT INTO Peer(id, publicKey, pairingState, pairedUtc, createdUtc) VALUES (@id, @pk, @st, @paired, @now)",
                    ("@id", peerId), ("@pk", publicKey), ("@st", PairingStates.ToWire(pairingState)),
                    ("@paired", pairingState == PairingState.Paired ? ctx.NowUtc : null), ("@now", ctx.NowUtc));
            }
            else if (publicKey != null)
            {
                var pinned = peer[0];
                if (pinned == null) ctx.Exec("UPDATE Peer SET publicKey = @pk WHERE id = @id", ("@pk", publicKey), ("@id", peerId));   // first pin
                else if (!pinned.AsSpan().SequenceEqual(publicKey)) return (new PeerKeyMismatch(peerId), false);
            }
            var reference = ConversationFor(ctx, peerId);
            return (new PeerRegistered(reference, created), true);
        });
    }

    public void SetPairingState(string peerId, PairingState pairingState)
    {
        if (!StoreIds.IsValid(peerId)) throw new ArgumentException("invalid peer id", nameof(peerId));
        if (!Enum.IsDefined(pairingState)) throw new ArgumentOutOfRangeException(nameof(pairingState));
        Write("set-pairing-state", ctx =>
        {
            int rows = ctx.Exec(
                "UPDATE Peer SET pairingState = @st, pairedUtc = CASE WHEN @st = 'paired' AND pairedUtc IS NULL THEN @now ELSE pairedUtc END WHERE id = @id",
                ("@st", PairingStates.ToWire(pairingState)), ("@now", ctx.NowUtc), ("@id", peerId));
            if (rows == 0) throw new ArgumentException("unknown peer", nameof(peerId));
            return (0, true);
        });
    }

    public PeerSeenResult TouchPeerSeen(string peerId, string sourceEndpoint)
    {
        if (!StoreIds.IsValid(peerId)) throw new ArgumentException("invalid peer id", nameof(peerId));
        ValidateEndpoint(sourceEndpoint);
        return Write("touch-peer-seen", ctx => {
            var result = UpdateEvidence(ctx, peerId, sourceEndpoint, install: false, MessagePathThrottle);
            return (result, result.Written);
        });
    }

    // ───────────────────────── diagnostics / lifetime ─────────────────────────

    public StoreDiagnostics GetDiagnostics() =>
        new(FileLength(_dbPath + "-wal"), FileLength(_dbPath), _lastCheckpointBusy, _lastCheckpointLog, _lastCheckpointUtc,
            _recoveredAttempts, Volatile.Read(ref _unknownPairingStates));

    /// <summary>TRUNCATE checkpoint; records busy/log in diagnostics. Returns true when the WAL was fully checkpointed.</summary>
    internal bool Checkpoint()
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            return CheckpointLocked();
        }
    }

    private bool CheckpointLocked()
    {
        try
        {
            using var cmd = _write.CreateCommand();
            cmd.CommandText = "PRAGMA wal_checkpoint(TRUNCATE)";
            using var r = cmd.ExecuteReader();
            r.Read();
            _lastCheckpointBusy = r.GetInt32(0) != 0;
            _lastCheckpointLog = r.GetInt32(1);
            _lastCheckpointUtc = _options.UtcNow();
            return !_lastCheckpointBusy;
        }
        catch (SqliteException e)
        {
            _lastCheckpointBusy = true;
            _lastCheckpointUtc = _options.UtcNow();
            _options.DiagnosticSink?.Invoke("checkpoint-failed sqlite=" + e.SqliteErrorCode.ToString(CultureInfo.InvariantCulture));
            return false;
        }
    }

    internal object? DebugScalar(string sql)
    {
        lock (_gate) { ThrowIfDisposed(); return ScalarRaw(_write, sql); }
    }

    /// <summary>A connection configured exactly like the store's read connections (tests only).</summary>
    internal SqliteConnection OpenReadConnectionForTest()
    {
        var c = NewConnection(create: false);
        ApplyPragmas(c);
        ExecRaw(c, "PRAGMA query_only=ON");
        return c;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            CheckpointLocked();
            CheckWalSize("close");
            _disposed = true;
            _write.Dispose();
        }
    }
}
