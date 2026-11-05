using System.Net.Http;
using System.Net.Sockets;
using NekoChat.Core.Attachments;
using NekoChat.Core.Storage;

namespace NekoChat.Core.Diagnostics;

public enum DiagnosticStage { Startup, SendPrepare, SendHello, SendTransmit, SendConfirm, ReceiveValidate, ReceiveRespond, ReceiveCommit, ReceiveNotify, ReceiveReceipt, StorageRead, StorageWrite, StorageQueue, Attachment, UiRefresh, UiRead, UiAction, Shutdown, Journal }
public enum DiagnosticOrigin { Local, RemoteResponse }
public enum DiagnosticOutcome { Started, Succeeded, Failed, Unconfirmed, Cancelled, Rejected, Stale, Deferred, Recovered }
public enum DiagnosticCategory { None, Network, Timeout, Cancellation, Validation, Remote, DeliveryUnconfirmed, StorageBusy, StorageFull, Permission, StorageIo, DataIntegrity, Capacity, State, Unexpected }
public enum DiagnosticCode { None, NetworkUnavailable, Timeout, Cancelled, InvalidPayload, RemoteRejected, Unconfirmed, StorageBusy, DiskFull, ReadOnly, StorageIo, CorruptData, UnsupportedData, IntegrityMismatch, CapacityExceeded, QueueFull, StaleView, Duplicate, NotificationFailed, Unexpected, LoggerUnavailable }
public enum DiagnosticSeverity { Information, Warning, Error, Critical }

public sealed record DiagnosticFault(DiagnosticCode Code, DiagnosticCategory Category, DiagnosticSeverity Severity, bool StopDataOperations = false)
{
    public string DisplayCode => "NCD-" + Code;
    public static bool Catchable(Exception error) => error is not OutOfMemoryException and not AccessViolationException;
    public static DiagnosticFault FromException(Exception error, bool cancellationRequested = false, DiagnosticStage? stage = null)
    {
        if (error is AggregateException { InnerExceptions.Count: 1 } aggregate) return FromException(aggregate.InnerExceptions[0], cancellationRequested, stage);
        if (error is StoreException store) return For(store.Kind switch {
            StoreErrorKind.StorageBusy => DiagnosticCode.StorageBusy,
            StoreErrorKind.DiskFull => DiagnosticCode.DiskFull,
            StoreErrorKind.ReadOnly => DiagnosticCode.ReadOnly,
            StoreErrorKind.Corrupt or StoreErrorKind.UnrecognizedDatabase => DiagnosticCode.CorruptData,
            StoreErrorKind.SchemaTooNew or StoreErrorKind.MigrationFailed or StoreErrorKind.NativeLibraryMissing or StoreErrorKind.DataLocationUnsupported => DiagnosticCode.UnsupportedData,
            StoreErrorKind.ConstraintViolation => DiagnosticCode.InvalidPayload,
            _ => DiagnosticCode.StorageIo
        });
        if (error is IOException { InnerException: SocketException } networkIo) return FromException(networkIo.InnerException!, cancellationRequested, stage);
        if (error is HttpRequestException { InnerException: TimeoutException }) return For(DiagnosticCode.Timeout);
        if (error is SocketException { SocketErrorCode: SocketError.TimedOut }) return For(DiagnosticCode.Timeout);
        if (error is StorageWorkQueueFullException) return For(DiagnosticCode.QueueFull);
        if (error is AttachmentException attachment) return FromReason(attachment.Code);
        return For(error switch {
            OperationCanceledException => cancellationRequested ? DiagnosticCode.Cancelled : DiagnosticCode.Timeout,
            TimeoutException => DiagnosticCode.Timeout,
            SocketException or HttpRequestException => DiagnosticCode.NetworkUnavailable,
            UnauthorizedAccessException => DiagnosticCode.ReadOnly,
            IOException io when (io.HResult & 0xffff) is 112 or 39 => DiagnosticCode.DiskFull,
            IOException when stage is DiagnosticStage.SendHello or DiagnosticStage.SendTransmit or DiagnosticStage.SendConfirm or DiagnosticStage.ReceiveValidate => DiagnosticCode.NetworkUnavailable,
            IOException => DiagnosticCode.StorageIo,
            ArgumentException or FormatException or System.Text.Json.JsonException => DiagnosticCode.InvalidPayload,
            _ => DiagnosticCode.Unexpected
        });
    }
    public static DiagnosticFault FromReason(string? reason) => For(reason switch {
        null or "ok" or "stored" or "unchanged" or "marked" or "saved" => DiagnosticCode.None,
        "duplicate" => DiagnosticCode.Duplicate,
        "timeout" => DiagnosticCode.Timeout,
        "cancelled" => DiagnosticCode.Cancelled,
        "network_error" or "listener_failed" or "connection_failed" or "accept_retry" or "receipt_send_failed" or "local_endpoint_unknown" => DiagnosticCode.NetworkUnavailable,
        "network_unconfirmed" or "unconfirmed" or "confirmation_pending" => DiagnosticCode.Unconfirmed,
        "storage_busy" => DiagnosticCode.StorageBusy,
        "storage_unavailable" or "receipt_storage_unavailable" => DiagnosticCode.StorageIo,
        "unavailable" or "not_owner" or "receiver_not_ready" or "wrong_recipient" or "source_not_allowed" or "unsupported_version" or "missing_endpoint" => DiagnosticCode.RemoteRejected,
        "payload_too_large" or "invalid_payload" or "too_large" or "unsafe_path" or "invalid_text" or "invalid_response" or "invalid_name" or "invalid_endpoint" or "directory_not_supported" => DiagnosticCode.InvalidPayload,
        "hash_mismatch" or "source_changed" or "conflict" => DiagnosticCode.IntegrityMismatch,
        "quota" or "storage_full" or "catalog_full" or "capacity_exceeded" or "rate_limited" or "offroster-limit" => DiagnosticCode.CapacityExceeded,
        "stale-view" or "stale_view" or "stale" or "stale_attempt" or "cleared" or "withdrawn" or "stale-session" or "stale_session" => DiagnosticCode.StaleView,
        "request_failed" or "receipt_service_unavailable" => DiagnosticCode.Unexpected,
        "notification_failed" => DiagnosticCode.NotificationFailed,
        _ => DiagnosticCode.Unexpected
    });
    public static DiagnosticFault For(DiagnosticCode code) => new(code, code switch {
        DiagnosticCode.None or DiagnosticCode.Duplicate => DiagnosticCategory.None,
        DiagnosticCode.NetworkUnavailable => DiagnosticCategory.Network,
        DiagnosticCode.Timeout => DiagnosticCategory.Timeout,
        DiagnosticCode.Cancelled => DiagnosticCategory.Cancellation,
        DiagnosticCode.InvalidPayload => DiagnosticCategory.Validation,
        DiagnosticCode.RemoteRejected => DiagnosticCategory.Remote,
        DiagnosticCode.Unconfirmed => DiagnosticCategory.DeliveryUnconfirmed,
        DiagnosticCode.StorageBusy => DiagnosticCategory.StorageBusy,
        DiagnosticCode.DiskFull => DiagnosticCategory.StorageFull,
        DiagnosticCode.ReadOnly => DiagnosticCategory.Permission,
        DiagnosticCode.StorageIo or DiagnosticCode.LoggerUnavailable => DiagnosticCategory.StorageIo,
        DiagnosticCode.CorruptData or DiagnosticCode.UnsupportedData or DiagnosticCode.IntegrityMismatch => DiagnosticCategory.DataIntegrity,
        DiagnosticCode.CapacityExceeded or DiagnosticCode.QueueFull => DiagnosticCategory.Capacity,
        DiagnosticCode.StaleView => DiagnosticCategory.State,
        _ => DiagnosticCategory.Unexpected
    }, code switch {
        DiagnosticCode.None or DiagnosticCode.Duplicate or DiagnosticCode.Cancelled or DiagnosticCode.StaleView => DiagnosticSeverity.Information,
        DiagnosticCode.CorruptData or DiagnosticCode.UnsupportedData => DiagnosticSeverity.Critical,
        DiagnosticCode.Unexpected or DiagnosticCode.StorageIo or DiagnosticCode.NotificationFailed or DiagnosticCode.IntegrityMismatch => DiagnosticSeverity.Error,
        _ => DiagnosticSeverity.Warning
    }, code is DiagnosticCode.CorruptData or DiagnosticCode.UnsupportedData);
}

public sealed class StorageWorkQueueFullException() : InvalidOperationException("Storage work queue is full.");
