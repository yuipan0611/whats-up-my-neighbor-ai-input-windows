using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NekoChat.Core.Classroom.Wire;
using NekoChat.Core.Storage;

namespace NekoChat.Core.Classroom.Transport;

public sealed class ClassroomStoreSink : IClassroomTransportSink
{
    private readonly IChatStore _store;

    public ClassroomStoreSink(IChatStore store)
    {
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
        var result = _store.ReceiveClassroomMessage(sourceIp, incoming, stillCurrent);

        if (result is ClassroomStored stored)
        {
            byte[] ack = ClassroomPacketWriter.MessageAck("stored", request.MessageId!, request.Session!, sourceIp, localIp, request.Reaction, request.Text!);
            return Task.FromResult<ClassroomTransportResponse?>(new ClassroomTransportResponse(200, ack));
        }

        if (result is ClassroomDuplicate or ClassroomClearedDuplicate)
        {
            byte[] ack = ClassroomPacketWriter.MessageAck("duplicate", request.MessageId!, request.Session!, sourceIp, localIp, request.Reaction, request.Text!);
            return Task.FromResult<ClassroomTransportResponse?>(new ClassroomTransportResponse(200, ack));
        }

        if (result is ClassroomConflict)
        {
            var (status, body) = ClassroomPacketWriter.Error(ClassroomCodes.Conflict);
            return Task.FromResult<ClassroomTransportResponse?>(new ClassroomTransportResponse(status, body));
        }

        if (result is ClassroomRejected rejected)
        {
            string code = rejected.Reason switch
            {
                "offroster-limit" => ClassroomCodes.RateLimited,
                "receiver-not-ready" => ClassroomCodes.ReceiverNotReady,
                "invalid-source" => ClassroomCodes.SourceNotAllowed,
                _ => ClassroomCodes.InvalidPayload
            };
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
        string status = result.Changed ? "ok" : result.Reason == "unchanged" ? "duplicate" : "discarded";
        byte[] ack = ClassroomPacketWriter.ReceiptAck(status, request.MessageId!, request.Session!, sourceIp, localIp);
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
        if (result.Stored)
        {
            byte[] response = "{\"status\":\"delivered\"}"u8.ToArray();
            return Task.FromResult<ClassroomTransportResponse?>(new ClassroomTransportResponse(200, response));
        }
        var (status, body) = ClassroomPacketWriter.Error(ClassroomCodes.ReceiverNotReady);
        return Task.FromResult<ClassroomTransportResponse?>(new ClassroomTransportResponse(status, body));
    }
}
