using NekoChat.Core.Diagnostics;

namespace NekoChat.Core.Storage;

public sealed record ChatStoreOptions(string DataDirectory)
{
    /// <summary>The ONLY source of lock-wait time (connection-string Default Timeout). Do not add PRAGMA busy_timeout.</summary>
    public int CommandTimeoutSeconds { get; init; } = 5;
    public Func<string, bool> TextValidator { get; init; } = Protocol.ValidText;
    /// <summary>CM-12 (PM engineering default): a draft may hold up to 1 MiB of UTF-8, independent of the send limit (D-06, 65,536 bytes), so an over-limit draft is kept for editing.</summary>
    public const int DefaultMaxDraftUtf8Bytes = 1_048_576;
    public int MaxDraftUtf8Bytes { get; init; } = DefaultMaxDraftUtf8Bytes;
    /// <summary>Optional override of the draft size rule; when null the byte limit above applies.</summary>
    public Func<string, bool>? DraftValidator { get; init; }
    public Func<DateTime> UtcNow { get; init; } = () => DateTime.UtcNow;
    public Func<string> NewId { get; init; } = () => Guid.NewGuid().ToString("D");
    public string AppVersion { get; init; } = "";
    /// <summary>Receives size-only diagnostics (never message content).</summary>
    public Action<string>? DiagnosticSink { get; init; }
    public DiagnosticJournal? Diagnostics { get; init; }
    /// <summary>Current local interface addresses and directed broadcasts, supplied by the Windows adapter.</summary>
    public Func<IReadOnlyCollection<string>> RosterExcludedEndpoints { get; init; } = () => Array.Empty<string>();
    /// <summary>CM-11 bounds for classroom sources that are not on the roster.</summary>
    public ClassroomOffRosterLimits OffRoster { get; init; } = new();
    internal IStoreFaults? Faults { get; init; }
    internal int? MaxPageCount { get; init; }
    internal IReadOnlyList<Migration>? Migrations { get; init; }
}

internal interface IStoreFaults
{
    /// <summary>Called right after the write transaction began, before any statement.</summary>
    void AfterBegin(string op);
    void BeforeCommit(string op);
    void AfterCommit(string op);
    /// <summary>Fault injection after a real short-lived SQL read, before its value is returned. Default no-op; never configured by the app.</summary>
    void AfterRead(string op) { }
    /// <summary>Called just before a database backup copies data (no write transaction may be open at that point).</summary>
    void BeforeBackup(string purpose) { }
}
