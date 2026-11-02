namespace NekoChat.Core.Storage;

public sealed record ChatStoreOptions(string DataDirectory)
{
    /// <summary>The ONLY source of lock-wait time (connection-string Default Timeout). Do not add PRAGMA busy_timeout.</summary>
    public int CommandTimeoutSeconds { get; init; } = 5;
    public Func<string, bool> TextValidator { get; init; } = Protocol.ValidText;
    public Func<DateTime> UtcNow { get; init; } = () => DateTime.UtcNow;
    public Func<string> NewId { get; init; } = () => Guid.NewGuid().ToString("D");
    public string AppVersion { get; init; } = "";
    /// <summary>Receives size-only diagnostics (never message content).</summary>
    public Action<string>? DiagnosticSink { get; init; }
    /// <summary>Current local interface addresses and directed broadcasts, supplied by the Windows adapter.</summary>
    public Func<IReadOnlyCollection<string>> RosterExcludedEndpoints { get; init; } = () => Array.Empty<string>();
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
}
