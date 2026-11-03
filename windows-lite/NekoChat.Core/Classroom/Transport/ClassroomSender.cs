using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NekoChat.Core.Classroom.Wire;
using NekoChat.Core.Roster;
using NekoChat.Core.Storage;
using NekoChat.Core.V2;
using ClassroomRoute = NekoChat.Core.Storage.ClassroomRoute;

namespace NekoChat.Core.Classroom.Transport;

public sealed record ClassroomSendResult(bool Success, string Code, CompleteResult? Completion, ClassroomRoute Route, string? RemoteCode = null);

public sealed record ClassroomReceiptSendResult(bool Completed, string Status, string? ErrorCode = null);

public sealed class ClassroomSender
{
    private readonly IChatStore _store;
    private readonly HttpClient _http;
    private readonly Func<DateTime> _utcNow;

    public static readonly HttpRequestOptionsKey<string> LocalEndpointOptionKey = new("NekoChat.Classroom.LocalEndpoint");

    public static SocketsHttpHandler CreateDefaultHandler(Func<SocketsHttpConnectionContext, CancellationToken, ValueTask<Stream>>? connectCallback = null)
    {
        return new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseProxy = false,
            ConnectTimeout = TimeSpan.FromSeconds(3),
            ConnectCallback = async (context, cancellationToken) =>
            {
                if (connectCallback is not null)
                {
                    var stream = await connectCallback(context, cancellationToken).ConfigureAwait(false);
                    if (stream is NetworkStream ns && ns.Socket.LocalEndPoint is IPEndPoint ep && context.InitialRequestMessage is { } req)
                    {
                        var addr = ep.Address;
                        req.Options.Set(LocalEndpointOptionKey, addr.IsIPv4MappedToIPv6 ? addr.MapToIPv4().ToString() : addr.ToString());
                    }
                    return stream;
                }

                var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                try
                {
                    await socket.ConnectAsync(context.DnsEndPoint, cancellationToken).ConfigureAwait(false);
                    if (socket.LocalEndPoint is IPEndPoint ep && context.InitialRequestMessage is { } request)
                    {
                        var addr = ep.Address;
                        request.Options.Set(LocalEndpointOptionKey, addr.IsIPv4MappedToIPv6 ? addr.MapToIPv4().ToString() : addr.ToString());
                    }
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            }
        };
    }

    public ClassroomSender(
        IChatStore store,
        HttpClient? httpClient = null,
        Func<DateTime>? utcNow = null,
        Func<SocketsHttpConnectionContext, CancellationToken, ValueTask<Stream>>? connectCallback = null,
        Func<SocketsHttpHandler>? handlerFactory = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
        if (httpClient is not null)
        {
            _http = httpClient;
        }
        else if (handlerFactory is not null)
        {
            _http = new HttpClient(handlerFactory()) { Timeout = TimeSpan.FromSeconds(10) };
        }
        else
        {
            _http = new HttpClient(CreateDefaultHandler(connectCallback)) { Timeout = TimeSpan.FromSeconds(10) };
        }
    }

    private static readonly byte[] ExactLegacyBody = "{\"error\":\"empty_text\"}"u8.ToArray();

    public async Task<ClassroomSendResult> SendPreparedAsync(
        ClassroomPreparedSend prepared,
        int port,
        string ownIp,
        Func<bool> stillCurrent,
        CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(prepared);
        ArgumentNullException.ThrowIfNull(stillCurrent);

        if (cancellation.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellation);
        }

        if (!stillCurrent())
        {
            return new ClassroomSendResult(false, "stale_attempt", new Discarded("stale-attempt"), ClassroomRoute.Classroom);
        }

        string targetIp = prepared.TargetIp;
        string? session = null;
        ClassroomRoute? route = null;
        DateTime helloUtc = _utcNow();
        string actualLocalIp = ownIp;

        // 1. Mandatory Hello Probe before routing and message send
        string clientNonce = ClassroomPacketWriter.NewToken();
        byte[] helloBytes = ClassroomPacketWriter.HelloRequest(clientNonce);

        try
        {
            using var helloRequest = new HttpRequestMessage(HttpMethod.Post, $"http://{targetIp}:{port}/nekochat/classroom/v1/hello");
            helloRequest.Content = new ByteArrayContent(helloBytes);
            helloRequest.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };

            using var helloResponse = await _http.SendAsync(helloRequest, HttpCompletionOption.ResponseHeadersRead, cancellation).ConfigureAwait(false);
            helloUtc = _utcNow();
            string? detectedIp = ExtractActualLocalIp(helloRequest);
            if (string.IsNullOrWhiteSpace(detectedIp))
            {
                var unconfirmed = _store.CompleteClassroomAttempt(prepared.MessageId, new UnconfirmedOutcome(), targetIp, prepared.TryCount);
                return new ClassroomSendResult(false, "local_endpoint_unknown", unconfirmed, ClassroomRoute.Classroom);
            }
            actualLocalIp = detectedIp;

            int helloStatus = (int)helloResponse.StatusCode;
            byte[] helloRespBytes = await helloResponse.Content.ReadAsByteArrayAsync(cancellation).ConfigureAwait(false);

            if (helloStatus == 200)
            {
                var helloContext = new ClassroomResponseContext(
                    targetIp,
                    actualLocalIp,
                    new ClassroomSentRequest(clientNonce, "", "", null, ""),
                    null);
                var helloResult = ClassroomResponseValidator.Validate(ClassroomResponseKind.HelloOk, 200, helloRespBytes, helloContext);
                if (helloResult.Ok && helloResult.Hello is not null)
                {
                    session = helloResult.Hello.Session;
                    route = ClassroomRoute.Classroom;
                }
            }
            else if (helloStatus == 400 && helloRespBytes.AsSpan().SequenceEqual(ExactLegacyBody))
            {
                // CM-14: 只有收到 400 {"error":"empty_text"} 才能走 legacy！
                route = ClassroomRoute.Legacy;
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            route = null;
        }

        // F1 / F5: hello 失敗或未支援時，不得走 legacy，不得記錄路由，不得發送使用者的文字
        if (route is null)
        {
            var unconfirmed = _store.CompleteClassroomAttempt(prepared.MessageId, new UnconfirmedOutcome(), targetIp, prepared.TryCount);
            return new ClassroomSendResult(false, "hello_failed", unconfirmed, ClassroomRoute.Classroom);
        }

        // F2: legacy 路徑套用 2,000 字上限，超過就不得發送，草稿保留
        if (route == ClassroomRoute.Legacy)
        {
            int scalarCount = prepared.RawText.EnumerateRunes().Count();
            if (scalarCount > MessageLimitPolicy.LegacyMaxScalars) // 2000
            {
                var unconfirmed = _store.CompleteClassroomAttempt(prepared.MessageId, new UnconfirmedOutcome(), targetIp, prepared.TryCount);
                return new ClassroomSendResult(false, "legacy_payload_too_large", unconfirmed, ClassroomRoute.Legacy);
            }
        }

        if (cancellation.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellation);
        }

        if (!stillCurrent())
        {
            return new ClassroomSendResult(false, "receiver_not_ready", new Discarded("receiver-not-ready"), route.Value);
        }

        // 2. Record Classroom Route in Store BEFORE sending message
        var routeResult = _store.RecordClassroomRoute(prepared.MessageId, prepared.TryCount, route.Value, helloUtc);
        if (!routeResult.Recorded)
        {
            var outcome = new FailedOutcome(routeResult.Reason);
            var comp = _store.CompleteClassroomAttempt(prepared.MessageId, outcome, targetIp, prepared.TryCount);
            return new ClassroomSendResult(false, routeResult.Reason, comp, route.Value);
        }

        // 3. Perform Transmission according to recorded route
        if (route == ClassroomRoute.Classroom && session is not null)
        {
            try
            {
                byte[] messageBytes = ClassroomPacketWriter.MessageSend(prepared.MessageId, session, targetIp, prepared.RawText, prepared.Reaction);
                using var msgRequest = new HttpRequestMessage(HttpMethod.Post, $"http://{targetIp}:{port}/nekochat/classroom/v1/message");
                msgRequest.Content = new ByteArrayContent(messageBytes);
                msgRequest.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };

                using var msgResponse = await _http.SendAsync(msgRequest, cancellation).ConfigureAwait(false);
                string? detectedMsgIp = ExtractActualLocalIp(msgRequest);
                if (string.IsNullOrWhiteSpace(detectedMsgIp))
                {
                    var comp = _store.CompleteClassroomAttempt(prepared.MessageId, new UnconfirmedOutcome(), targetIp, prepared.TryCount);
                    return new ClassroomSendResult(false, "local_endpoint_unknown", comp, ClassroomRoute.Classroom);
                }
                actualLocalIp = detectedMsgIp;

                int msgStatus = (int)msgResponse.StatusCode;
                byte[] msgRespBytes = await msgResponse.Content.ReadAsByteArrayAsync(cancellation).ConfigureAwait(false);

                var msgContext = new ClassroomResponseContext(
                    targetIp,
                    actualLocalIp,
                    new ClassroomSentRequest(clientNonce, session, prepared.MessageId, prepared.Reaction, prepared.RawText),
                    null);

                var ackResult = ClassroomResponseValidator.Validate(ClassroomResponseKind.MessageAck, msgStatus, msgRespBytes, msgContext);

                if (ackResult.Ok && ackResult.Status is "stored" or "duplicate")
                {
                    byte[] fingerprint = Convert.FromHexString(prepared.FingerprintHex);
                    var comp = _store.CompleteClassroomAttempt(prepared.MessageId, new RemoteStoredOutcome(_utcNow(), fingerprint), targetIp, prepared.TryCount);
                    return new ClassroomSendResult(true, "ok", comp, ClassroomRoute.Classroom);
                }
                else
                {
                    bool isDefinitiveFailed = ackResult.RemoteCode is
                        ClassroomCodes.InvalidPayload or
                        ClassroomCodes.UnsupportedVersion or
                        ClassroomCodes.PayloadTooLarge or
                        ClassroomCodes.RateLimited or
                        ClassroomCodes.WrongRecipient or
                        ClassroomCodes.Conflict or
                        ClassroomCodes.SourceNotAllowed or
                        ClassroomCodes.CapacityExceeded;

                    AttemptOutcome outcome = isDefinitiveFailed ? new FailedOutcome(ackResult.RemoteCode!) : new UnconfirmedOutcome();
                    var comp = _store.CompleteClassroomAttempt(prepared.MessageId, outcome, targetIp, prepared.TryCount);
                    return new ClassroomSendResult(false, ackResult.Code ?? "unconfirmed", comp, ClassroomRoute.Classroom, ackResult.RemoteCode);
                }
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                var comp = _store.CompleteClassroomAttempt(prepared.MessageId, new UnconfirmedOutcome(), targetIp, prepared.TryCount);
                return new ClassroomSendResult(false, "cancelled", comp, ClassroomRoute.Classroom);
            }
            catch (Exception)
            {
                var comp = _store.CompleteClassroomAttempt(prepared.MessageId, new UnconfirmedOutcome(), targetIp, prepared.TryCount);
                return new ClassroomSendResult(false, "network_unconfirmed", comp, ClassroomRoute.Classroom);
            }
        }
        else
        {
            // Legacy Route
            try
            {
                var legacyPayload = JsonSerializer.Serialize(new
                {
                    protocol = "nekochat",
                    version = 1,
                    kind = "direct.message",
                    text = prepared.RawText,
                    reaction = prepared.Reaction
                });

                using var legacyReq = new HttpRequestMessage(HttpMethod.Post, $"http://{targetIp}:{port}/nekochat/v1/direct");
                legacyReq.Content = new StringContent(legacyPayload, Encoding.UTF8, "application/json");

                using var legacyResp = await _http.SendAsync(legacyReq, cancellation).ConfigureAwait(false);
                string respStr = await legacyResp.Content.ReadAsStringAsync(cancellation).ConfigureAwait(false);

                if (legacyResp.IsSuccessStatusCode)
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(respStr);
                        if (doc.RootElement.TryGetProperty("status", out var s) && s.GetString() == "delivered")
                        {
                            var comp = _store.CompleteClassroomLegacyDelivered(prepared.MessageId, targetIp, prepared.TryCount);
                            return new ClassroomSendResult(true, "ok", comp, ClassroomRoute.Legacy);
                        }
                    }
                    catch { }
                }

                // Ben 必修 4: legacy 的錯誤依 Reason 分類
                string? remoteReason = null;
                try
                {
                    using var errDoc = JsonDocument.Parse(respStr);
                    if (errDoc.RootElement.TryGetProperty("error", out var err) && err.ValueKind == JsonValueKind.String)
                        remoteReason = err.GetString();
                    else if (errDoc.RootElement.TryGetProperty("code", out var code) && code.ValueKind == JsonValueKind.String)
                        remoteReason = code.GetString();
                    else if (errDoc.RootElement.TryGetProperty("reason", out var re) && re.ValueKind == JsonValueKind.String)
                        remoteReason = re.GetString();
                }
                catch { }

                int statusCode = (int)legacyResp.StatusCode;
                remoteReason ??= $"http_{statusCode}";

                if (statusCode is >= 400 and < 500)
                {
                    // F3: 明確的 4xx 拒絕記為 Failed (FailedOutcome)
                    var failed = _store.CompleteClassroomAttempt(prepared.MessageId, new FailedOutcome(remoteReason), targetIp, prepared.TryCount);
                    return new ClassroomSendResult(false, "legacy_failed", failed, ClassroomRoute.Legacy, remoteReason);
                }
                else
                {
                    var unconfirmed = _store.CompleteClassroomAttempt(prepared.MessageId, new UnconfirmedOutcome(), targetIp, prepared.TryCount);
                    return new ClassroomSendResult(false, "legacy_unconfirmed", unconfirmed, ClassroomRoute.Legacy, remoteReason);
                }
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                var unconfirmed = _store.CompleteClassroomAttempt(prepared.MessageId, new UnconfirmedOutcome(), targetIp, prepared.TryCount);
                return new ClassroomSendResult(false, "cancelled", unconfirmed, ClassroomRoute.Legacy);
            }
            catch (Exception)
            {
                var unconfirmed = _store.CompleteClassroomAttempt(prepared.MessageId, new UnconfirmedOutcome(), targetIp, prepared.TryCount);
                return new ClassroomSendResult(false, "network_unconfirmed", unconfirmed, ClassroomRoute.Legacy);
            }
        }
    }

    private static string? ExtractActualLocalIp(HttpRequestMessage request)
    {
        if (request.Options.TryGetValue(LocalEndpointOptionKey, out var optIp) && !string.IsNullOrWhiteSpace(optIp))
        {
            return optIp;
        }
        return null;
    }

    public async Task<ClassroomReceiptSendResult> SendReceiptAsync(
        PendingClassroomReceipt receipt,
        int port,
        string ownIp,
        Func<bool> stillCurrent,
        CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        ArgumentNullException.ThrowIfNull(stillCurrent);

        if (!stillCurrent())
        {
            return new ClassroomReceiptSendResult(false, "stale_policy");
        }

        string targetIp = receipt.TargetIp;
        if (!IPAddress.TryParse(targetIp, out var ip) || ClassroomSource.Canonical(ip) is null)
        {
            return new ClassroomReceiptSendResult(false, "invalid_target");
        }

        // 1. Mandatory Hello Probe
        string clientNonce = ClassroomPacketWriter.NewToken();
        byte[] helloBytes = ClassroomPacketWriter.HelloRequest(clientNonce);
        string? session = null;

        try
        {
            using var helloRequest = new HttpRequestMessage(HttpMethod.Post, $"http://{targetIp}:{port}/nekochat/classroom/v1/hello");
            helloRequest.Content = new ByteArrayContent(helloBytes);
            helloRequest.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };

            using var helloResponse = await _http.SendAsync(helloRequest, HttpCompletionOption.ResponseHeadersRead, cancellation).ConfigureAwait(false);
            if (!helloResponse.IsSuccessStatusCode)
            {
                return new ClassroomReceiptSendResult(false, "transient", "hello_failed");
            }

            string? actualHelloLocalIp = ExtractActualLocalIp(helloRequest);
            if (string.IsNullOrWhiteSpace(actualHelloLocalIp))
            {
                return new ClassroomReceiptSendResult(false, "local_endpoint_unknown", "local_endpoint_unknown");
            }
            byte[] helloRespBytes = await helloResponse.Content.ReadAsByteArrayAsync(cancellation).ConfigureAwait(false);
            var helloContext = new ClassroomResponseContext(
                targetIp,
                actualHelloLocalIp,
                new ClassroomSentRequest(clientNonce, "", "", null, ""),
                null);
            var helloResult = ClassroomResponseValidator.Validate(ClassroomResponseKind.HelloOk, (int)helloResponse.StatusCode, helloRespBytes, helloContext);
            if (!helloResult.Ok || helloResult.Hello is null)
            {
                return new ClassroomReceiptSendResult(false, "transient", "hello_validation_failed");
            }

            // CM-13 §9: Only send if hello.ok supports "receipt.read" or "receipts"
            bool supportsReadReceipt = false;
            foreach (var kind in helloResult.Hello.Capabilities)
            {
                if (kind is "receipt.read" or "receipts")
                {
                    supportsReadReceipt = true;
                    break;
                }
            }
            if (!supportsReadReceipt)
            {
                return new ClassroomReceiptSendResult(false, "capability_missing", "unsupported_kind");
            }

            session = helloResult.Hello.Session;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            return new ClassroomReceiptSendResult(false, "cancelled", "cancelled");
        }
        catch (Exception)
        {
            return new ClassroomReceiptSendResult(false, "transient", "network_error");
        }

        if (!stillCurrent())
        {
            return new ClassroomReceiptSendResult(false, "stale_policy");
        }

        // 2. Send receipt
        byte[] receiptBytes = ClassroomPacketWriter.ReceiptRequest(receipt.MessageId, session!, targetIp);

        try
        {
            using var receiptRequest = new HttpRequestMessage(HttpMethod.Post, $"http://{targetIp}:{port}/nekochat/classroom/v1/receipt");
            receiptRequest.Content = new ByteArrayContent(receiptBytes);
            receiptRequest.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };

            using var receiptResponse = await _http.SendAsync(receiptRequest, HttpCompletionOption.ResponseHeadersRead, cancellation).ConfigureAwait(false);
            int statusCode = (int)receiptResponse.StatusCode;
            string? actualReceiptLocalIp = ExtractActualLocalIp(receiptRequest);
            if (string.IsNullOrWhiteSpace(actualReceiptLocalIp))
            {
                return new ClassroomReceiptSendResult(false, "local_endpoint_unknown", "local_endpoint_unknown");
            }
            byte[] respBytes = await receiptResponse.Content.ReadAsByteArrayAsync(cancellation).ConfigureAwait(false);

            if (statusCode == 200)
            {
                var ackContext = new ClassroomResponseContext(
                    targetIp,
                    actualReceiptLocalIp,
                    new ClassroomSentRequest(clientNonce, session!, receipt.MessageId, null, ""),
                    null);
                var ackResult = ClassroomResponseValidator.Validate(ClassroomResponseKind.ReceiptAck, 200, respBytes, ackContext);
                if (ackResult.Ok && ackResult.Status is not null)
                {
                    string ackStatus = ackResult.Status;
                    if (ackStatus is "ok" or "duplicate" or "discarded")
                    {
                        // CM-13 §9: Only verified ok/duplicate/discarded removes the outbox event
                        bool completed = _store.CompleteClassroomReadReceipt(receipt);
                        return new ClassroomReceiptSendResult(completed, ackStatus, completed ? null : "store_complete_failed");
                    }
                }
                return new ClassroomReceiptSendResult(false, "invalid_response", "invalid_ack_payload");
            }
            else if (statusCode == 503)
            {
                // transient (early-read)
                return new ClassroomReceiptSendResult(false, "transient", "transient_early_read");
            }
            else if (statusCode == 409)
            {
                // stale_session
                return new ClassroomReceiptSendResult(false, "transient", "stale_session");
            }
            else
            {
                return new ClassroomReceiptSendResult(false, "transient", $"http_{statusCode}");
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            return new ClassroomReceiptSendResult(false, "cancelled", "cancelled");
        }
        catch (Exception)
        {
            return new ClassroomReceiptSendResult(false, "transient", "network_error");
        }
    }
}
