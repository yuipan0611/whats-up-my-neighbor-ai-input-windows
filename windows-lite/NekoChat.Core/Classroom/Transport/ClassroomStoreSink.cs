using System;
using NekoChat.Core.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NekoChat.Core.Classroom.Wire;
using NekoChat.Core.Storage;

namespace NekoChat.Core.Classroom.Transport;

public sealed class ClassroomStoreSink : IClassroomTransportSink
{
    private readonly IChatStore _store;
    private readonly DiagnosticJournal? _diagnostics;

    public void OnCommunicationSucceeded(string sourceIp) => _store.RecordClassroomCommunicationSuccess(sourceIp);

    public event Action<string, string, string?>? MessageStored;
    public event Action<string, string>? ReceiptStored;
    public event Action<LegacyDeliveryNotice>? LegacyMessageStored;

    public ClassroomStoreSink(IChatStore store, DiagnosticJournal? diagnostics = null)
    {
        _diagnostics = diagnostics;
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public Task<ClassroomTransportResponse?> OnMessageAsync(
        string sourceIp,
        string localIp,
        ClassroomRequest request,
        Func<bool> stillCurrent,
        CancellationToken cancellation)
    {
        var incoming = new ClassroomIncoming(request.MessageId!, request.Text!, request.Reaction);
        _diagnostics?.Record(DiagnosticStage.ReceiveCommit, DiagnosticOutcome.Started);
        ClassroomReceiveResult result;
        try { result = _store.ReceiveClassroomMessage(sourceIp, incoming, stillCurrent); }
        catch (Exception error) when (DiagnosticFault.Catchable(error))
        {
            // Preserve normal root-cause correlation, but never expose identifiers from a
            // blocked source or when storage cannot safely determine its block state.
            string? diagnosticId = null;
            try { if (!_store.IsSourceBlocked(sourceIp)) diagnosticId = request.MessageId; }
            catch (Exception checkError) when (DiagnosticFault.Catchable(checkError)) { }
            _diagnostics?.Record(DiagnosticStage.ReceiveCommit, DiagnosticOutcome.Failed, error: error, messageId: diagnosticId);
            throw;
        }

        // D-30: the writer transaction rejected local persistence; acknowledge normal delivery
        // without recording request identifiers, notifying observers, or rearming receipts.
        if (result is ClassroomRejected { Reason: "source-not-allowed" })
        {
            _diagnostics?.Record(DiagnosticStage.ReceiveCommit, DiagnosticOutcome.Rejected, DiagnosticFault.FromReason(ClassroomCodes.SourceNotAllowed).Code);
            return Task.FromResult<ClassroomTransportResponse?>(new(200, ClassroomPacketWriter.MessageAck("stored", request.MessageId!, request.Session!, sourceIp, localIp, request.Reaction, request.Text!)));
        }

        if (result is ClassroomStored stored)
        {
            OnCommunicationSucceeded(sourceIp);
            _diagnostics?.Record(DiagnosticStage.ReceiveCommit, DiagnosticOutcome.Succeeded, messageId: request.MessageId);
            byte[] ack = ClassroomPacketWriter.MessageAck("stored", request.MessageId!, request.Session!, sourceIp, localIp, request.Reaction, request.Text!);
            try { MessageStored?.Invoke(sourceIp, request.Text!, request.Reaction); }
            catch (Exception error) when (DiagnosticFault.Catchable(error)) { _diagnostics?.Record(DiagnosticStage.ReceiveNotify, DiagnosticOutcome.Failed, DiagnosticCode.NotificationFailed, error, request.MessageId); }
            return Task.FromResult<ClassroomTransportResponse?>(new ClassroomTransportResponse(200, ack));
        }

        if (result is ClassroomDuplicate or ClassroomClearedDuplicate)
        {
            OnCommunicationSucceeded(sourceIp);
            _diagnostics?.Record(DiagnosticStage.ReceiveCommit, DiagnosticOutcome.Succeeded, DiagnosticCode.Duplicate, messageId: request.MessageId);
            byte[] ack = ClassroomPacketWriter.MessageAck("duplicate", request.MessageId!, request.Session!, sourceIp, localIp, request.Reaction, request.Text!);
            return Task.FromResult<ClassroomTransportResponse?>(new ClassroomTransportResponse(200, ack));
        }

        if (result is ClassroomConflict)
        {
            _diagnostics?.Record(DiagnosticStage.ReceiveCommit, DiagnosticOutcome.Rejected, DiagnosticCode.IntegrityMismatch, messageId: request.MessageId);
            var (status, body) = ClassroomPacketWriter.Error(ClassroomCodes.Conflict);
            return Task.FromResult<ClassroomTransportResponse?>(new ClassroomTransportResponse(status, body));
        }

        if (result is ClassroomRejected rejected)
        {
            string code = rejected.Reason switch
            {
                "source-not-allowed" => ClassroomCodes.SourceNotAllowed,
                "offroster-limit" => ClassroomCodes.RateLimited,
                "receiver-not-ready" => ClassroomCodes.ReceiverNotReady,
                "invalid-source" => ClassroomCodes.SourceNotAllowed,
                _ => ClassroomCodes.InvalidPayload
            };
            _diagnostics?.Record(DiagnosticStage.ReceiveCommit, DiagnosticOutcome.Rejected, DiagnosticFault.FromReason(code).Code);
            var (status, body) = ClassroomPacketWriter.Error(code);
            return Task.FromResult<ClassroomTransportResponse?>(new ClassroomTransportResponse(status, body));
        }

        return Task.FromResult<ClassroomTransportResponse?>(null);
    }

    public Task<ClassroomTransportResponse?> OnReceiptAsync(
        string sourceIp,
        string localIp,
        ClassroomRequest request,
        Func<bool> stillCurrent,
        CancellationToken cancellation)
    {
        var result = _store.ApplyClassroomReceipt(sourceIp, request.MessageId!, RemoteReceiptKind.Read);
        if (result.Reason == "source-not-allowed")
            return Task.FromResult<ClassroomTransportResponse?>(new(200, ClassroomPacketWriter.ReceiptAck("discarded", request.MessageId!, request.Session!, sourceIp, localIp)));
        if (!result.Changed && result.Reason == "pending-local-confirmation")
        {
            var (errStatus, body) = ClassroomPacketWriter.Error(ClassroomCodes.Transient);
            return Task.FromResult<ClassroomTransportResponse?>(new ClassroomTransportResponse(errStatus, body));
        }

        string status = result.Changed ? "ok" : result.Reason == "unchanged" ? "duplicate" : "discarded";
        byte[] ack = ClassroomPacketWriter.ReceiptAck(status, request.MessageId!, request.Session!, sourceIp, localIp);
        _diagnostics?.Record(DiagnosticStage.ReceiveReceipt, DiagnosticOutcome.Succeeded, messageId: request.MessageId);
        try { ReceiptStored?.Invoke(sourceIp, request.MessageId!); } catch (Exception error) when (DiagnosticFault.Catchable(error)) { _diagnostics?.Record(DiagnosticStage.ReceiveNotify, DiagnosticOutcome.Failed, DiagnosticCode.NotificationFailed, error, request.MessageId); }
        return Task.FromResult<ClassroomTransportResponse?>(new ClassroomTransportResponse(200, ack));
    }

    public Task<ClassroomTransportResponse?> OnLegacyDirectAsync(
        string sourceIp,
        DirectMessage message,
        Func<bool> stillCurrent,
        CancellationToken cancellation)
    {
        var captured = _store.GetLegacyReceiveContext(sourceIp);
        var result = _store.ReceiveLegacyMessage(message, captured, stillCurrent);
        if (result.Reason == "source-not-allowed")
            return Task.FromResult<ClassroomTransportResponse?>(new(200, "{\"status\":\"delivered\"}"u8.ToArray())); // frozen legacy v1 success
        if (result.Stored)
        {
            OnCommunicationSucceeded(sourceIp);
            _diagnostics?.Record(DiagnosticStage.ReceiveCommit, DiagnosticOutcome.Succeeded);
            if (result.LegacySeq is { } seq)
            {
                try { LegacyMessageStored?.Invoke(new(sourceIp, _store.GetLegacyReceiveContext(sourceIp).Scope, seq, message.Reaction ?? ClassroomReactions.MatchText(message.Text)?.Id)); } catch (Exception error) when (DiagnosticFault.Catchable(error)) { _diagnostics?.Record(DiagnosticStage.ReceiveNotify, DiagnosticOutcome.Failed, DiagnosticCode.NotificationFailed, error); }
            }
            byte[] response = "{\"status\":\"delivered\"}"u8.ToArray();
            return Task.FromResult<ClassroomTransportResponse?>(new ClassroomTransportResponse(200, response));
        }
        string errorCode = result.Reason switch
        {
            "source-not-allowed" => ClassroomCodes.SourceNotAllowed,
                "offroster-limit" => ClassroomCodes.RateLimited,
            "stale-legacy-scope" => ClassroomCodes.ReceiverNotReady,
            "invalid-text" or "invalid-reaction" or "invalid-unicode" => ClassroomCodes.InvalidPayload,
            _ => ClassroomCodes.ReceiverNotReady // fail closed
        };
        var (status, body) = ClassroomPacketWriter.Error(errorCode);
        return Task.FromResult<ClassroomTransportResponse?>(new ClassroomTransportResponse(status, body));
    }
}
