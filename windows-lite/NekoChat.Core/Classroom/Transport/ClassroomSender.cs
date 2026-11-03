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
            PooledConnectionLifetime = TimeSpan.Zero,
            PooledConnectionIdleTimeout = TimeSpan.Zero,
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
            _store.CancelClassroomAttempt(prepared.MessageId, prepared.TryCount);
            return new ClassroomSendResult(false, "cancelled", null, ClassroomRoute.Classroom);
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
        string clientNonce = ClassroomPacketWriter.NewToken();

        async Task<(bool Success, string? Session, ClassroomRoute? Route, string? ErrorCode, bool IsTransient)> RunHelloProbeAsync()
        {
            clientNonce = ClassroomPacketWriter.NewToken();
            byte[] helloBytes = ClassroomPacketWriter.HelloRequest(clientNonce);

            using var helloRequest = new HttpRequestMessage(HttpMethod.Post, $"http://{targetIp}:{port}/nekochat/classroom/v1/hello");
            helloRequest.Headers.ConnectionClose = true;
            helloRequest.Content = new ByteArrayContent(helloBytes);
            helloRequest.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };

            HttpResponseMessage helloResponse;
            try
            {
                helloResponse = await _http.SendAsync(helloRequest, HttpCompletionOption.ResponseHeadersRead, cancellation).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                return (false, null, null, ex is TimeoutException or TaskCanceledException ? "timeout" : "network_error", false);
            }

            using (helloResponse)
            {
                string? detectedIp = ExtractActualLocalIp(helloRequest);
                if (string.IsNullOrWhiteSpace(detectedIp))
                {
                    return (false, null, null, "local_endpoint_unknown", false);
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
                        "msg.plain");
                    var helloResult = ClassroomResponseValidator.Validate(ClassroomResponseKind.HelloOk, 200, helloRespBytes, helloContext);
                    if (!helloResult.Ok || helloResult.Hello is null)
                    {
                        return (false, null, null, helloResult.RemoteCode ?? helloResult.Code, false);
                    }

                    return (true, helloResult.Hello.Session, ClassroomRoute.Classroom, null, false);
                }
                else if (helloStatus == 400 && helloRespBytes.AsSpan().SequenceEqual(ExactLegacyBody))
                {
                    // CM-14: 只有收到 400 {"error":"empty_text"} 才能走 legacy！
                    return (true, null, ClassroomRoute.Legacy, null, false);
                }
                else
                {
                    var helloContext = new ClassroomResponseContext(
                        targetIp,
                        actualLocalIp,
                        new ClassroomSentRequest(clientNonce, "", "", null, ""),
                        "msg.plain");
                    var valResult = ClassroomResponseValidator.Validate(ClassroomResponseKind.HelloOk, helloStatus, helloRespBytes, helloContext);

                    bool isTransient = false;
                    string errCode;
                    if (valResult.Code == ClassroomCodes.RemoteError && valResult.RemoteCode == "transient")
                    {
                        isTransient = true;
                        errCode = "transient";
                    }
                    else if (valResult.Code == ClassroomCodes.RemoteError)
                    {
                        errCode = valResult.RemoteCode!;
                    }
                    else
                    {
                        errCode = helloStatus == 400 ? "hello_rejected" : $"http_{helloStatus}";
                    }

                    return (false, null, null, errCode, isTransient);
                }
            }
        }

        (bool HelloSuccess, string? HelloSession, ClassroomRoute? HelloRoute, string? HelloError, bool HelloTransient) probe;
        try
        {
            probe = await RunHelloProbeAsync().ConfigureAwait(false);
            helloUtc = _utcNow();
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            _store.CancelClassroomAttempt(prepared.MessageId, prepared.TryCount);
            return new ClassroomSendResult(false, "cancelled", null, ClassroomRoute.Classroom);
        }

        if (!probe.HelloSuccess)
        {
            if (probe.HelloTransient)
            {
                // §10 第 267 行: hello 收到 transient 記 unconfirmed
                var unconfirmed = _store.CompleteClassroomAttempt(prepared.MessageId, new UnconfirmedOutcome(), targetIp, prepared.TryCount);
                return new ClassroomSendResult(false, probe.HelloError ?? "hello_failed", unconfirmed, ClassroomRoute.Classroom);
            }
            else
            {
                // CM-18: 其他 hello 階段失敗一律記 failed
                var failed = _store.CompleteClassroomAttempt(prepared.MessageId, new FailedOutcome(probe.HelloError ?? "hello_failed"), targetIp, prepared.TryCount);
                return new ClassroomSendResult(false, probe.HelloError ?? "hello_failed", failed, ClassroomRoute.Classroom);
            }
        }

        session = probe.HelloSession;
        route = probe.HelloRoute;

        // F2: legacy 路徑套用 2,000 字上限，超過就不得發送，草稿保留
        if (route == ClassroomRoute.Legacy)
        {
            int scalarCount = prepared.RawText.EnumerateRunes().Count();
            if (scalarCount > MessageLimitPolicy.LegacyMaxScalars) // 2000
            {
                var failed = _store.CompleteClassroomAttempt(prepared.MessageId, new FailedOutcome("legacy_payload_too_large"), targetIp, prepared.TryCount);
                return new ClassroomSendResult(false, "legacy_payload_too_large", failed, ClassroomRoute.Legacy);
            }
        }

        if (cancellation.IsCancellationRequested)
        {
            _store.CancelClassroomAttempt(prepared.MessageId, prepared.TryCount);
            return new ClassroomSendResult(false, "cancelled", null, route!.Value);
        }

        if (!stillCurrent())
        {
            return new ClassroomSendResult(false, "receiver_not_ready", new Discarded("receiver-not-ready"), route!.Value);
        }

        // 2. Record Classroom Route in Store BEFORE sending message
        var routeResult = _store.RecordClassroomRoute(prepared.MessageId, prepared.TryCount, route!.Value, helloUtc);
        if (!routeResult.Recorded)
        {
            var outcome = new FailedOutcome(routeResult.Reason);
            var comp = _store.CompleteClassroomAttempt(prepared.MessageId, outcome, targetIp, prepared.TryCount);
            return new ClassroomSendResult(false, routeResult.Reason, comp, route.Value);
        }

        // 3. Perform Transmission according to recorded route
        if (route == ClassroomRoute.Classroom && session is not null)
        {
            for (int sessionTry = 0; sessionTry < 2; sessionTry++)
            {
                if (cancellation.IsCancellationRequested)
                {
                    if (sessionTry == 0)
                    {
                        _store.CancelClassroomAttempt(prepared.MessageId, prepared.TryCount);
                        return new ClassroomSendResult(false, "cancelled", null, ClassroomRoute.Classroom);
                    }
                    else
                    {
                        var comp = _store.CompleteClassroomAttempt(prepared.MessageId, new UnconfirmedOutcome(), targetIp, prepared.TryCount);
                        return new ClassroomSendResult(false, "cancelled", comp, ClassroomRoute.Classroom);
                    }
                }

                try
                {
                    byte[] messageBytes = ClassroomPacketWriter.MessageSend(prepared.MessageId, session, targetIp, prepared.RawText, prepared.Reaction);
                    using var msgRequest = new HttpRequestMessage(HttpMethod.Post, $"http://{targetIp}:{port}/nekochat/classroom/v1/message");
                    msgRequest.Headers.ConnectionClose = true;
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

                    // §6.9: 檢查是否為 stale_session
                    if (ackResult.RemoteCode == ClassroomCodes.StaleSession)
                    {
                        if (sessionTry == 0)
                        {
                            // 第一次 stale_session: 重新執行一次 hello 拿新 session 再送一次
                            var probe2 = await RunHelloProbeAsync().ConfigureAwait(false);
                            if (probe2.Success && probe2.Session is not null && probe2.Route == ClassroomRoute.Classroom)
                            {
                                session = probe2.Session;
                                continue;
                            }
                        }

                        // 第二次仍為 stale_session 或重新 hello 失敗: 記 FailedOutcome(stale_session)
                        var failed = _store.CompleteClassroomAttempt(prepared.MessageId, new FailedOutcome(ClassroomCodes.StaleSession), targetIp, prepared.TryCount);
                        return new ClassroomSendResult(false, "remote_error", failed, ClassroomRoute.Classroom, ClassroomCodes.StaleSession);
                    }

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

            var fallbackUnconfirmed = _store.CompleteClassroomAttempt(prepared.MessageId, new UnconfirmedOutcome(), targetIp, prepared.TryCount);
            return new ClassroomSendResult(false, "unconfirmed", fallbackUnconfirmed, ClassroomRoute.Classroom);
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
                legacyReq.Headers.ConnectionClose = true;
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
        string? session = null;
        string actualHelloLocalIp = ownIp;

        async Task<(bool Success, string? Session, string? ErrorStatus, string? ErrorCode)> ProbeReceiptHelloAsync()
        {
            clientNonce = ClassroomPacketWriter.NewToken();
            byte[] helloBytes = ClassroomPacketWriter.HelloRequest(clientNonce);

            try
            {
                using var helloRequest = new HttpRequestMessage(HttpMethod.Post, $"http://{targetIp}:{port}/nekochat/classroom/v1/hello");
                helloRequest.Headers.ConnectionClose = true;
                helloRequest.Content = new ByteArrayContent(helloBytes);
                helloRequest.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };

                using var helloResponse = await _http.SendAsync(helloRequest, HttpCompletionOption.ResponseHeadersRead, cancellation).ConfigureAwait(false);
                if (!helloResponse.IsSuccessStatusCode)
                {
                    return (false, null, "transient", "hello_failed");
                }

                string? detectedHelloIp = ExtractActualLocalIp(helloRequest);
                if (string.IsNullOrWhiteSpace(detectedHelloIp))
                {
                    return (false, null, "local_endpoint_unknown", "local_endpoint_unknown");
                }
                actualHelloLocalIp = detectedHelloIp;
                byte[] helloRespBytes = await helloResponse.Content.ReadAsByteArrayAsync(cancellation).ConfigureAwait(false);
                var helloContext = new ClassroomResponseContext(
                    targetIp,
                    actualHelloLocalIp,
                    new ClassroomSentRequest(clientNonce, "", "", null, ""),
                    null);
                var helloResult = ClassroomResponseValidator.Validate(ClassroomResponseKind.HelloOk, (int)helloResponse.StatusCode, helloRespBytes, helloContext);
                if (!helloResult.Ok || helloResult.Hello is null)
                {
                    return (false, null, "transient", "hello_validation_failed");
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
                    return (false, null, "capability_missing", "unsupported_kind");
                }

                return (true, helloResult.Hello.Session, null, null);
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                return (false, null, "cancelled", "cancelled");
            }
            catch (Exception)
            {
                return (false, null, "transient", "network_error");
            }
        }

        var initialProbe = await ProbeReceiptHelloAsync().ConfigureAwait(false);
        if (!initialProbe.Success)
        {
            return new ClassroomReceiptSendResult(false, initialProbe.ErrorStatus ?? "transient", initialProbe.ErrorCode);
        }
        session = initialProbe.Session;

        if (!stillCurrent())
        {
            return new ClassroomReceiptSendResult(false, "stale_policy");
        }

        // 2. Send receipt (with stale_session single retry)
        for (int receiptTry = 0; receiptTry < 2; receiptTry++)
        {
            byte[] receiptBytes = ClassroomPacketWriter.ReceiptRequest(receipt.MessageId, session!, targetIp);

            try
            {
                using var receiptRequest = new HttpRequestMessage(HttpMethod.Post, $"http://{targetIp}:{port}/nekochat/classroom/v1/receipt");
                receiptRequest.Headers.ConnectionClose = true;
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

                var ackContext = new ClassroomResponseContext(
                    targetIp,
                    actualReceiptLocalIp,
                    new ClassroomSentRequest(clientNonce, session!, receipt.MessageId, null, ""),
                    null);
                var ackResult = ClassroomResponseValidator.Validate(ClassroomResponseKind.ReceiptAck, statusCode, respBytes, ackContext);

                if (statusCode == 200)
                {
                    if (ackResult.Ok && ackResult.Status is not null)
                    {
                        string ackStatus = ackResult.Status;
                        if (ackStatus is "ok" or "duplicate" or "discarded")
                        {
                            bool completed = _store.CompleteClassroomReadReceipt(receipt);
                            return new ClassroomReceiptSendResult(completed, ackStatus, completed ? null : "store_complete_failed");
                        }
                    }
                    return new ClassroomReceiptSendResult(false, "invalid_response", "invalid_ack_payload");
                }
                else if (statusCode == 409 && ackResult.Code == ClassroomCodes.RemoteError && ackResult.RemoteCode == ClassroomCodes.StaleSession)
                {
                    // §9 第 3 點: 收到合法的 409 stale_session，重新 hello 重發一次
                    if (receiptTry == 0)
                    {
                        var reProbe = await ProbeReceiptHelloAsync().ConfigureAwait(false);
                        if (reProbe.Success && reProbe.Session is not null)
                        {
                            session = reProbe.Session;
                            continue;
                        }
                    }
                    return new ClassroomReceiptSendResult(false, "transient", "stale_session");
                }
                else if (statusCode == 503)
                {
                    return new ClassroomReceiptSendResult(false, "transient", "transient_early_read");
                }
                else
                {
                    return new ClassroomReceiptSendResult(false, "transient", ackResult.RemoteCode ?? $"http_{statusCode}");
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

        return new ClassroomReceiptSendResult(false, "transient", "stale_session_exhausted");
    }
}
