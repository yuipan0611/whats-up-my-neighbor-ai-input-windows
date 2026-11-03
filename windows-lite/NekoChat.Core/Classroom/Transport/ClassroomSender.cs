using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NekoChat.Core.Classroom.Wire;
using NekoChat.Core.Storage;
using ClassroomRoute = NekoChat.Core.Storage.ClassroomRoute;

namespace NekoChat.Core.Classroom.Transport;

public sealed record ClassroomSendResult(bool Success, string Code, CompleteResult? Completion, ClassroomRoute Route);

public sealed class ClassroomSender
{
    private readonly IChatStore _store;
    private readonly HttpClient _http;
    private readonly Func<DateTime> _utcNow;

    public ClassroomSender(IChatStore store, HttpClient? httpClient = null, Func<DateTime>? utcNow = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
        if (httpClient is not null)
        {
            _http = httpClient;
        }
        else
        {
            var handler = new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false, ConnectTimeout = TimeSpan.FromSeconds(3) };
            _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
        }
    }

    public async Task<ClassroomSendResult> SendPreparedAsync(
        ClassroomPreparedSend prepared,
        int port,
        string ownIp,
        Func<bool> stillCurrent,
        CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(prepared);
        ArgumentNullException.ThrowIfNull(stillCurrent);

        if (!stillCurrent())
        {
            return new ClassroomSendResult(false, "stale_attempt", new Discarded("stale-attempt"), ClassroomRoute.Classroom);
        }

        string targetIp = prepared.TargetIp;
        string? session = null;
        ClassroomRoute route = ClassroomRoute.Legacy;
        DateTime helloUtc = _utcNow();

        // 1. Mandatory Hello Probe before routing and message send
        string clientNonce = ClassroomPacketWriter.NewToken();
        byte[] helloBytes = ClassroomPacketWriter.HelloRequest(clientNonce);

        try
        {
            using var helloRequest = new HttpRequestMessage(HttpMethod.Post, $"http://{targetIp}:{port}/nekochat/classroom/v1/hello");
            helloRequest.Content = new ByteArrayContent(helloBytes);
            helloRequest.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };

            using var helloResponse = await _http.SendAsync(helloRequest, cancellation).ConfigureAwait(false);
            helloUtc = _utcNow();

            if (helloResponse.IsSuccessStatusCode)
            {
                byte[] helloRespBytes = await helloResponse.Content.ReadAsByteArrayAsync(cancellation).ConfigureAwait(false);
                var helloContext = new ClassroomResponseContext(
                    targetIp,
                    ownIp,
                    new ClassroomSentRequest(clientNonce, "", "", null, ""),
                    null);
                var helloResult = ClassroomResponseValidator.Validate(ClassroomResponseKind.HelloOk, (int)helloResponse.StatusCode, helloRespBytes, helloContext);
                if (helloResult.Ok && helloResult.Hello is not null)
                {
                    session = helloResult.Hello.Session;
                    route = ClassroomRoute.Classroom;
                }
            }
        }
        catch (Exception)
        {
            // Hello failed or peer does not support classroom endpoint; falls back to legacy route
            route = ClassroomRoute.Legacy;
        }

        if (!stillCurrent())
        {
            return new ClassroomSendResult(false, "receiver_not_ready", new Discarded("receiver-not-ready"), route);
        }

        // 2. Record Classroom Route in Store BEFORE sending message
        var routeResult = _store.RecordClassroomRoute(prepared.MessageId, prepared.TryCount, route, helloUtc);
        if (!routeResult.Recorded)
        {
            var outcome = new FailedOutcome(routeResult.Reason);
            var comp = _store.CompleteClassroomAttempt(prepared.MessageId, outcome, targetIp, prepared.TryCount);
            return new ClassroomSendResult(false, routeResult.Reason, comp, route);
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
                byte[] msgRespBytes = await msgResponse.Content.ReadAsByteArrayAsync(cancellation).ConfigureAwait(false);

                var msgContext = new ClassroomResponseContext(
                    targetIp,
                    ownIp,
                    new ClassroomSentRequest(clientNonce, session, prepared.MessageId, prepared.Reaction, prepared.RawText),
                    null);

                var ackResult = ClassroomResponseValidator.Validate(ClassroomResponseKind.MessageAck, (int)msgResponse.StatusCode, msgRespBytes, msgContext);

                if (ackResult.Ok && ackResult.Status is "stored" or "duplicate")
                {
                    byte[] fingerprint = Convert.FromHexString(prepared.FingerprintHex);
                    var comp = _store.CompleteClassroomAttempt(prepared.MessageId, new RemoteStoredOutcome(_utcNow(), fingerprint), targetIp, prepared.TryCount);
                    return new ClassroomSendResult(true, "ok", comp, ClassroomRoute.Classroom);
                }
                else
                {
                    var comp = _store.CompleteClassroomAttempt(prepared.MessageId, new UnconfirmedOutcome(), targetIp, prepared.TryCount);
                    return new ClassroomSendResult(false, ackResult.Code ?? "unconfirmed", comp, ClassroomRoute.Classroom);
                }
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
                if (legacyResp.IsSuccessStatusCode)
                {
                    string respStr = await legacyResp.Content.ReadAsStringAsync(cancellation).ConfigureAwait(false);
                    using var doc = JsonDocument.Parse(respStr);
                    if (doc.RootElement.TryGetProperty("status", out var s) && s.GetString() == "delivered")
                    {
                        var comp = _store.CompleteClassroomLegacyDelivered(prepared.MessageId, targetIp, prepared.TryCount);
                        return new ClassroomSendResult(true, "ok", comp, ClassroomRoute.Legacy);
                    }
                }

                var unconfirmed = _store.CompleteClassroomAttempt(prepared.MessageId, new UnconfirmedOutcome(), targetIp, prepared.TryCount);
                return new ClassroomSendResult(false, "legacy_unconfirmed", unconfirmed, ClassroomRoute.Legacy);
            }
            catch (Exception)
            {
                var unconfirmed = _store.CompleteClassroomAttempt(prepared.MessageId, new UnconfirmedOutcome(), targetIp, prepared.TryCount);
                return new ClassroomSendResult(false, "network_unconfirmed", unconfirmed, ClassroomRoute.Legacy);
            }
        }
    }
}
