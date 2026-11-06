using NekoChat.Core.Diagnostics;
using System.Net;
using System.Text.Json;
using NekoChat.Core.Classroom.Wire;

namespace NekoChat.Core.Classroom.Transport;

/// <summary>
/// Hook for classroom message storage persistence. When null, the adapter acknowledges valid packets without saving.
/// </summary>
public interface IClassroomTransportSink
{
    Task<ClassroomTransportResponse?> OnMessageAsync(string sourceIp, string localIp, ClassroomRequest request, Func<bool> stillCurrent, CancellationToken cancellation);
    Task<ClassroomTransportResponse?> OnReceiptAsync(string sourceIp, string localIp, ClassroomRequest request, Func<bool> stillCurrent, CancellationToken cancellation);
    Task<ClassroomTransportResponse?> OnLegacyDirectAsync(string sourceIp, DirectMessage message, Func<bool> stillCurrent, CancellationToken cancellation);
}

/// <summary>
/// NCM3: Implements <see cref="IClassroomRequestEndpoint"/> by connecting the Transport stream listener
/// to Tom's frozen ClassroomWire codec and session table (NCMW-v2).
/// Contract guarantees:
/// 1. signed.v2.* paths return unsupported_version at head validation.
/// 2. legacy.v1 enforces its own 65,536-byte bound (LegacyDirectProtocol.MaxBodyBytes).
/// 3. Classroom routes enforce their respective bounds (ControlBodyMaxBytes or MessageBodyMaxBytes).
/// 4. Transport depends only on Wire's public types.
/// </summary>
public sealed class ClassroomEndpointAdapter : IClassroomRequestEndpoint
{
    private readonly ClassroomSessionTable _sessions;
    private readonly DiagnosticJournal? _diagnostics;
    private readonly IClassroomTransportSink? _sink;
    private readonly IReadOnlyList<string> _capabilities;
    private int _ready;

    public bool Ready => Volatile.Read(ref _ready) == 1;
    public ClassroomSessionTable SessionTable => _sessions;

    public ClassroomEndpointAdapter(IClassroomTransportSink? sink = null, ClassroomSessionTable? sessionTable = null, IReadOnlyList<string>? capabilities = null, DiagnosticJournal? diagnostics = null)
    {
        _diagnostics = diagnostics; _sink = sink;
        _sessions = sessionTable ?? new ClassroomSessionTable();
        _capabilities = capabilities ?? new[] { "receipts" };
    }

    public void SetReady(bool ready)
    {
        Volatile.Write(ref _ready, ready ? 1 : 0);
        if (!ready)
        {
            _sessions.Clear(); // Contract §6.8: Fast, clears RAM sessions immediately, never waits for storage
        }
    }

    public ClassroomHeadDecision ValidateHead(ReadOnlyMemory<byte> header)
    {
        var result = ClassroomHead.Check(header.Span);
        if (!result.Ok)
        {
            _diagnostics?.Record(DiagnosticStage.ReceiveValidate, DiagnosticOutcome.Rejected, DiagnosticFault.FromReason(result.Code).Code);
            return new ClassroomHeadDecision(result.Code);
        }

        switch (result.Route)
        {
            case ClassroomRoute.SignedV2Hello:
            case ClassroomRoute.SignedV2Message:
            case ClassroomRoute.SignedV2Receipt:
            case ClassroomRoute.SignedV2Pair:
                // Mandatory requirement: signed.v2.* returns unsupported_version
                return new ClassroomHeadDecision(ClassroomCodes.UnsupportedVersion);

            case ClassroomRoute.LegacyV1:
                int legacyLimit = LegacyDirectProtocol.MaxBodyBytes; // 65,536 bytes
                if (result.Length > legacyLimit)
                {
                    return new ClassroomHeadDecision(ClassroomCodes.PayloadTooLarge);
                }
                return new ClassroomHeadDecision(ClassroomCodes.Ok, new ClassroomFrameHead("legacy.v1", result.Length, legacyLimit));

            case ClassroomRoute.Hello:
                return new ClassroomHeadDecision(ClassroomCodes.Ok, new ClassroomFrameHead("classroom.hello", result.Length, ClassroomWire.ControlBodyMaxBytes));

            case ClassroomRoute.Message:
                return new ClassroomHeadDecision(ClassroomCodes.Ok, new ClassroomFrameHead("classroom.message", result.Length, ClassroomWire.MessageBodyMaxBytes));

            case ClassroomRoute.Receipt:
                return new ClassroomHeadDecision(ClassroomCodes.Ok, new ClassroomFrameHead("classroom.receipt", result.Length, ClassroomWire.ControlBodyMaxBytes));

            default:
                return new ClassroomHeadDecision(ClassroomCodes.UnsupportedVersion);
        }
    }

    public ClassroomTransportResponse Error(string code)
    {
        _diagnostics?.Record(DiagnosticStage.ReceiveRespond, DiagnosticOutcome.Rejected, DiagnosticFault.FromReason(code).Code);
        if (ClassroomWire.TryErrorStatus(code, out int status))
        {
            var (_, body) = ClassroomPacketWriter.Error(code);
            return new ClassroomTransportResponse(status, body);
        }

        // Fallback for non-standard error codes
        int fallbackStatus = code switch
        {
            ClassroomCodes.RateLimited => 429,
            ClassroomCodes.PayloadTooLarge => 413,
            ClassroomCodes.Conflict => 409,
            ClassroomCodes.SourceNotAllowed => 403,
            _ => 503
        };
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(new
        {
            protocol = ClassroomWire.Protocol,
            version = ClassroomWire.Version,
            kind = "error",
            code
        });
        return new ClassroomTransportResponse(fallbackStatus, payload);
    }

    public async Task<ClassroomTransportResponse> HandleAsync(
        ClassroomRequestFrame frame,
        ClassroomConnectionContext connection,
        Func<bool> stillCurrent,
        CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(stillCurrent);

        if (!Ready || !stillCurrent())
        {
            return Error(ClassroomCodes.ReceiverNotReady);
        }

        string? canonicalSource = ClassroomWire.CanonicalSource(connection.SourceIp);
        string? canonicalLocal = ClassroomWire.CanonicalSource(connection.LocalIp);
        if (canonicalSource is null || canonicalLocal is null || !ClassroomWire.IsPrivateIPv4(canonicalSource))
        {
            return Error(ClassroomCodes.SourceNotAllowed);
        }

        switch (frame.Route)
        {
            case "classroom.hello":
                return HandleHello(frame.Body.Span, canonicalLocal, canonicalSource);

            case "classroom.message":
                return await HandleMessageAsync(frame.Body, canonicalLocal, canonicalSource, stillCurrent, cancellation).ConfigureAwait(false);

            case "classroom.receipt":
                return await HandleReceiptAsync(frame.Body, canonicalLocal, canonicalSource, stillCurrent, cancellation).ConfigureAwait(false);

            case "legacy.v1":
                return await HandleLegacyV1Async(frame.Body, canonicalSource, stillCurrent, cancellation).ConfigureAwait(false);

            default:
                return Error(ClassroomCodes.UnsupportedVersion);
        }
    }

    private ClassroomTransportResponse HandleHello(ReadOnlySpan<byte> body, string localIp, string sourceIp)
    {
        var v = ClassroomRequestValidator.Validate(ClassroomRequestKind.Hello, body, localIp, sourceIp, _sessions);
        if (!v.Ok)
        {
            _diagnostics?.Record(DiagnosticStage.ReceiveValidate, DiagnosticOutcome.Rejected, DiagnosticFault.FromReason(v.Code).Code);
            return Error(v.Code);
        }

        string session = _sessions.Issue(sourceIp);
        byte[] ack = ClassroomPacketWriter.HelloOk(v.Request!.ClientNonce!, session, localIp, sourceIp, _capabilities);
        return new ClassroomTransportResponse(200, ack);
    }

    private async Task<ClassroomTransportResponse> HandleMessageAsync(
        ReadOnlyMemory<byte> body,
        string localIp,
        string sourceIp,
        Func<bool> stillCurrent,
        CancellationToken cancellation)
    {
        var v = ClassroomRequestValidator.Validate(ClassroomRequestKind.Message, body.Span, localIp, sourceIp, _sessions);
        if (!v.Ok)
        {
            _diagnostics?.Record(DiagnosticStage.ReceiveValidate, DiagnosticOutcome.Rejected, DiagnosticFault.FromReason(v.Code).Code);
            return Error(v.Code);
        }

        var req = v.Request!;
        // Contract §7.10: Consume the session atomically before touching storage
        if (!_sessions.TryConsume(req.Session!, sourceIp))
        {
            return Error(ClassroomCodes.StaleSession);
        }

        if (_sink is null)
        {
            return Error(ClassroomCodes.ReceiverNotReady);
        }

        try
        {
            var customResponse = await _sink.OnMessageAsync(sourceIp, localIp, req, stillCurrent, cancellation).ConfigureAwait(false);
            if (customResponse is null)
            {
                return Error(ClassroomCodes.ReceiverNotReady);
            }
            return customResponse;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _diagnostics?.Record(DiagnosticStage.ReceiveCommit, DiagnosticOutcome.Failed, error: ex, messageId: req.MessageId);
            return Error(ClassroomCodes.StorageUnavailable);
        }
    }

    private async Task<ClassroomTransportResponse> HandleReceiptAsync(
        ReadOnlyMemory<byte> body,
        string localIp,
        string sourceIp,
        Func<bool> stillCurrent,
        CancellationToken cancellation)
    {
        var v = ClassroomRequestValidator.Validate(ClassroomRequestKind.Receipt, body.Span, localIp, sourceIp, _sessions);
        if (!v.Ok)
        {
            _diagnostics?.Record(DiagnosticStage.ReceiveValidate, DiagnosticOutcome.Rejected, DiagnosticFault.FromReason(v.Code).Code);
            return Error(v.Code);
        }

        var req = v.Request!;
        // Contract §7.10: Consume the session atomically before touching storage
        if (!_sessions.TryConsume(req.Session!, sourceIp))
        {
            return Error(ClassroomCodes.StaleSession);
        }

        if (_sink is null)
        {
            return Error(ClassroomCodes.ReceiverNotReady);
        }

        try
        {
            var customResponse = await _sink.OnReceiptAsync(sourceIp, localIp, req, stillCurrent, cancellation).ConfigureAwait(false);
            if (customResponse is null)
            {
                return Error(ClassroomCodes.ReceiverNotReady);
            }
            return customResponse;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _diagnostics?.Record(DiagnosticStage.ReceiveReceipt, DiagnosticOutcome.Failed, error: ex, messageId: req.MessageId);
            return Error(ClassroomCodes.StorageUnavailable);
        }
    }

    private async Task<ClassroomTransportResponse> HandleLegacyV1Async(
        ReadOnlyMemory<byte> body,
        string sourceIp,
        Func<bool> stillCurrent,
        CancellationToken cancellation)
    {
        if (!LegacyDirectProtocol.TryParse(body.Span, out var message, out string error))
        {
            return Error(error);
        }

        if (_sink is null)
        {
            return Error(ClassroomCodes.ReceiverNotReady);
        }

        try
        {
            var customResponse = await _sink.OnLegacyDirectAsync(sourceIp, message!, stillCurrent, cancellation).ConfigureAwait(false);
            if (customResponse is null)
            {
                return Error(ClassroomCodes.ReceiverNotReady);
            }
            return customResponse;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _diagnostics?.Record(DiagnosticStage.ReceiveCommit, DiagnosticOutcome.Failed, error: ex);
            return Error(ClassroomCodes.StorageUnavailable);
        }
    }
}
