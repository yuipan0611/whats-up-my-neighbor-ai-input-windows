using Microsoft.Data.Sqlite;

namespace NekoChat.Core.Storage;

/// <summary>Why a storage operation failed. Business outcomes (Duplicate, Conflict...) are result records, not exceptions.</summary>
public enum StoreErrorKind
{
    StorageBusy,
    DiskFull,
    ReadOnly,
    IoError,
    Corrupt,
    CannotOpen,
    ConstraintViolation,
    NativeLibraryMissing,
    SchemaTooNew,
    UnrecognizedDatabase,
    DataLocationUnsupported,
    MigrationFailed,
    Unknown,
}

/// <summary>
/// Typed storage failure. Messages are fixed strings: they never carry message text, SQL parameters or file contents.
/// </summary>
public sealed class StoreException : Exception
{
    public StoreErrorKind Kind { get; }
    public int? SqliteCode { get; }
    public int? SqliteExtendedCode { get; }
    public int? FromVersion { get; }
    public int? ToVersion { get; }

    public StoreException(StoreErrorKind kind, string message, Exception? inner = null,
        int? sqliteCode = null, int? sqliteExtendedCode = null, int? fromVersion = null, int? toVersion = null)
        : base(message, inner)
    {
        Kind = kind;
        SqliteCode = sqliteCode;
        SqliteExtendedCode = sqliteExtendedCode;
        FromVersion = fromVersion;
        ToVersion = toVersion;
    }
}

internal static class StoreErrors
{
    internal static StoreException Map(SqliteException e)
    {
        int primary = e.SqliteErrorCode & 0xFF;
        var kind = primary switch
        {
            5 or 6 => StoreErrorKind.StorageBusy,
            13 => StoreErrorKind.DiskFull,
            8 => StoreErrorKind.ReadOnly,
            10 => StoreErrorKind.IoError,
            11 or 26 => StoreErrorKind.Corrupt,
            14 => StoreErrorKind.CannotOpen,
            19 => StoreErrorKind.ConstraintViolation,
            _ => StoreErrorKind.Unknown,
        };
        return new StoreException(kind, Describe(kind), e, e.SqliteErrorCode, e.SqliteExtendedErrorCode);
    }

    internal static StoreException NativeMissing(Exception e) =>
        new(StoreErrorKind.NativeLibraryMissing, Describe(StoreErrorKind.NativeLibraryMissing), e);

    /// <summary>True for a UNIQUE / PRIMARY KEY violation (extended codes 2067 / 1555).</summary>
    internal static bool IsUniqueViolation(SqliteException e) =>
        (e.SqliteErrorCode & 0xFF) == 19 && (e.SqliteExtendedErrorCode == 2067 || e.SqliteExtendedErrorCode == 1555);

    internal static string Describe(StoreErrorKind kind) => kind switch
    {
        StoreErrorKind.StorageBusy => "The local database is busy.",
        StoreErrorKind.DiskFull => "The disk is full; nothing was saved.",
        StoreErrorKind.ReadOnly => "The local data area is read-only.",
        StoreErrorKind.IoError => "A disk read/write error occurred.",
        StoreErrorKind.Corrupt => "The local database is damaged or not a database.",
        StoreErrorKind.CannotOpen => "The local data area cannot be opened.",
        StoreErrorKind.ConstraintViolation => "A database constraint rejected the change.",
        StoreErrorKind.NativeLibraryMissing => "The database component could not be loaded.",
        StoreErrorKind.SchemaTooNew => "The database was written by a newer version.",
        StoreErrorKind.UnrecognizedDatabase => "The file is not a NekoChat database.",
        StoreErrorKind.DataLocationUnsupported => "The data location does not support the required journal mode.",
        StoreErrorKind.MigrationFailed => "A schema migration failed and was rolled back.",
        _ => "An unexpected storage error occurred.",
    };
}
