using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NekoChat.Core.Classroom.Transport;
using NekoChat.Core.Classroom.Wire;
using NekoChat.Core.Storage;
using ClassroomRoute = NekoChat.Core.Storage.ClassroomRoute;

namespace NekoChat.Tests;

public static class ClassroomSendChecks
{
    private sealed class TraceHttpMessageHandler : HttpMessageHandler
    {
        public List<string> PathSequence { get; } = new();
        public bool SimulateLegacy { get; set; }
        public bool SetLocalEndpointOption { get; set; } = true;
        public string SessionId { get; set; } = ClassroomPacketWriter.NewToken();
        public string TargetIp { get; set; } = "192.168.1.50";
        public string OwnIp { get; set; } = "192.168.1.10";

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (SetLocalEndpointOption)
            {
                request.Options.Set(ClassroomSender.LocalEndpointOptionKey, OwnIp);
            }
            string path = request.RequestUri?.AbsolutePath ?? "";
            PathSequence.Add(path);

            if (path == "/nekochat/classroom/v1/hello")
            {
                if (SimulateLegacy)
                {
                    var legacyHello = new HttpResponseMessage(HttpStatusCode.BadRequest)
                    {
                        Content = new ByteArrayContent(Encoding.UTF8.GetBytes("{\"error\":\"empty_text\"}"))
                    };
                    legacyHello.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
                    return legacyHello;
                }

                byte[] body = await request.Content!.ReadAsByteArrayAsync(cancellationToken);
                using var doc = JsonDocument.Parse(body);
                string clientNonce = doc.RootElement.GetProperty("client_nonce").GetString()!;

                byte[] helloOk = ClassroomPacketWriter.HelloOk(
                    clientNonce,
                    SessionId,
                    TargetIp,
                    OwnIp,
                    new[] { "receipts" });

                var resp = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(helloOk)
                };
                resp.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
                return resp;
            }

            if (path == "/nekochat/classroom/v1/message")
            {
                byte[] body = await request.Content!.ReadAsByteArrayAsync(cancellationToken);
                using var doc = JsonDocument.Parse(body);
                string messageId = doc.RootElement.GetProperty("message_id").GetString()!;
                string session = doc.RootElement.GetProperty("session").GetString()!;
                string text = doc.RootElement.GetProperty("text").GetString()!;
                string? reaction = doc.RootElement.TryGetProperty("reaction", out var r) && r.ValueKind == JsonValueKind.String ? r.GetString() : null;

                byte[] ack = ClassroomPacketWriter.MessageAck(
                    "stored",
                    messageId,
                    session,
                    OwnIp,
                    TargetIp,
                    reaction,
                    text);

                var resp = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(ack)
                };
                resp.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
                return resp;
            }

            if (path == "/nekochat/v1/direct")
            {
                var resp = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"status\":\"delivered\"}", Encoding.UTF8, "application/json")
                };
                return resp;
            }

            if (path == "/nekochat/classroom/v1/receipt")
            {
                byte[] body = await request.Content!.ReadAsByteArrayAsync(cancellationToken);
                using var doc = JsonDocument.Parse(body);
                string messageId = doc.RootElement.GetProperty("message_id").GetString()!;
                string session = doc.RootElement.GetProperty("session").GetString()!;

                byte[] ack = ClassroomPacketWriter.ReceiptAck(
                    "ok",
                    messageId,
                    session,
                    OwnIp,
                    TargetIp);

                var resp = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(ack)
                };
                resp.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
                return resp;
            }

            return new HttpResponseMessage(HttpStatusCode.BadRequest);
        }
    }

    public static async Task RunAsync()
    {
        int checks = 0;
        void Check(bool cond, string name)
        {
            checks++;
            if (!cond) throw new InvalidOperationException($"Check failed: {name}");
        }

        string tempDir = Path.Combine(Path.GetTempPath(), "classroom-send-checks-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            using var store = ChatStore.Open(new(Path.Combine(tempDir, "data")));
            string ownIp = "192.168.1.10";
            string peerIp = "192.168.1.50";
            var principal = ClassroomPrincipalKey.ForSource(peerIp)!.Value;

            // -----------------------------------------------------------------------------------------
            // Check 1: Classroom path — Must Hello first, then RecordClassroomRoute, then send message
            // -----------------------------------------------------------------------------------------
            var handler1 = new TraceHttpMessageHandler { OwnIp = ownIp, TargetIp = peerIp, SimulateLegacy = false };
            using var http1 = new HttpClient(handler1);
            var sender1 = new ClassroomSender(store, http1);

            var start1 = store.BeginClassroomSend(principal, "哈囉教室！", 0, 1);
            Check(start1 is AttemptStarted, "BeginClassroomSend started attempt");
            var started1 = (AttemptStarted)start1;

            var prepared1 = store.GetClassroomPreparedSend(started1.MessageId);
            Check(prepared1 is not null, "GetClassroomPreparedSend returned prepared attempt");

            var outcome1 = await sender1.SendPreparedAsync(prepared1!, 8768, ownIp, () => true);
            Check(outcome1.Success, "Classroom send succeeded");
            Check(outcome1.Route == ClassroomRoute.Classroom, "Route is Classroom");
            Check(outcome1.Completion is Confirmed { DraftCleared: true }, "Draft cleared on confirmed send");

            // Sequence verification: Hello was called BEFORE message
            Check(handler1.PathSequence.Count == 2, "2 HTTP calls made");
            Check(handler1.PathSequence[0] == "/nekochat/classroom/v1/hello", "First call was hello");
            Check(handler1.PathSequence[1] == "/nekochat/classroom/v1/message", "Second call was message");

            // SQLite Verification: stored goes to RemoteStoredOutcome
            // remoteStoredAt IS NOT NULL, legacyDeliveredAt IS NULL
            var page1 = store.ListClassroomMessages(principal);
            Check(page1.Items.Count == 1, "1 message in history");
            var msg1 = page1.Items[0];
            Check(msg1.RemoteStoredAt is not null, "remoteStoredAt is recorded");
            Check(msg1.LegacyDeliveredAt is null, "legacyDeliveredAt is null for classroom route");

            // -----------------------------------------------------------------------------------------
            // Check 2: Legacy fallback path — Hello fails, RecordClassroomRoute(Legacy), then v1 direct
            // -----------------------------------------------------------------------------------------
            string legacyPeerIp = "192.168.1.60";
            var legacyPrincipal = ClassroomPrincipalKey.ForSource(legacyPeerIp)!.Value;

            var handler2 = new TraceHttpMessageHandler { OwnIp = ownIp, TargetIp = legacyPeerIp, SimulateLegacy = true };
            using var http2 = new HttpClient(handler2);
            var sender2 = new ClassroomSender(store, http2);

            var start2 = store.BeginClassroomSend(legacyPrincipal, "舊版同學你好！", 0, 1);
            Check(start2 is AttemptStarted, "BeginClassroomSend started legacy attempt");
            var started2 = (AttemptStarted)start2;

            var prepared2 = store.GetClassroomPreparedSend(started2.MessageId);
            Check(prepared2 is not null, "GetClassroomPreparedSend returned prepared legacy attempt");

            var outcome2 = await sender2.SendPreparedAsync(prepared2!, 8768, ownIp, () => true);
            Check(outcome2.Success, "Legacy fallback send succeeded");
            Check(outcome2.Route == ClassroomRoute.Legacy, "Route is Legacy");
            Check(outcome2.Completion is Confirmed { DraftCleared: true }, "Draft cleared on legacy send");

            // Sequence verification: Hello was probed first, then fallback to v1 direct
            Check(handler2.PathSequence.Count == 2, "2 HTTP calls made for legacy fallback");
            Check(handler2.PathSequence[0] == "/nekochat/classroom/v1/hello", "First call was hello");
            Check(handler2.PathSequence[1] == "/nekochat/v1/direct", "Second call was v1 direct");

            // SQLite Verification: legacy delivered goes to CompleteClassroomLegacyDelivered
            // legacyDeliveredAt IS NOT NULL, remoteStoredAt IS NULL
            var page2 = store.ListClassroomMessages(legacyPrincipal);
            Check(page2.Items.Count == 1, "1 legacy message in history");
            var msg2 = page2.Items[0];
            Check(msg2.LegacyDeliveredAt is not null, "legacyDeliveredAt is recorded for legacy route");
            Check(msg2.RemoteStoredAt is null, "remoteStoredAt is null for legacy route");

            // -----------------------------------------------------------------------------------------
            // Check 3: Route change protection (Classroom cannot downgrade to Legacy on retry)
            // -----------------------------------------------------------------------------------------
            // If a message previously routed to Classroom fails to send, retry cannot route to Legacy
            string retryPeerIp = "192.168.1.70";
            var retryPrincipal = ClassroomPrincipalKey.ForSource(retryPeerIp)!.Value;

            var handler3 = new TraceHttpMessageHandler { OwnIp = ownIp, TargetIp = retryPeerIp, SimulateLegacy = false };
            using var http3 = new HttpClient(handler3);
            var sender3 = new ClassroomSender(store, http3);

            var start3 = store.BeginClassroomSend(retryPrincipal, "測試重試降級保護", 0, 1);
            var started3 = (AttemptStarted)start3;
            var prep3 = store.GetClassroomPreparedSend(started3.MessageId)!;

            // Try 1: Classroom route recorded but transmission cancelled
            var tHello1 = DateTime.UtcNow;
            var rr1 = store.RecordClassroomRoute(prep3.MessageId, prep3.TryCount, ClassroomRoute.Classroom, tHello1);
            Check(rr1.Recorded, "Try 1 recorded classroom route");
            store.CancelClassroomAttempt(prep3.MessageId, prep3.TryCount);

            // Try 2: Retry begins
            var retryAttempt = store.BeginClassroomRetry(prep3.MessageId, retryPrincipal, "測試重試降級保護", 0, 1);
            Check(retryAttempt is AttemptStarted, "Retry attempt started");
            var startedRetry = (AttemptStarted)retryAttempt;
            var prepRetry = store.GetClassroomPreparedSend(startedRetry.MessageId)!;

            // Peer now claims legacy; sender attempts to route to legacy
            handler3.SimulateLegacy = true;
            var outcome3 = await sender3.SendPreparedAsync(prepRetry, 8768, ownIp, () => true);
            Check(!outcome3.Success, "Downgrade to legacy is refused");
            Check(outcome3.Code == "classroom-may-have-delivered", "Refusal reason is classroom-may-have-delivered");

            // Verify NO message packet was sent on the network during refused attempt
            Check(handler3.PathSequence[^1] == "/nekochat/classroom/v1/hello", "No message sent when route recording failed");

            // -----------------------------------------------------------------------------------------
            // Check 4: Receiver Store Sink verification
            // -----------------------------------------------------------------------------------------
            var sink = new ClassroomStoreSink(store);
            var reqMsg = new ClassroomRequest(
                ClassroomRequestKind.Message,
                MessageId: Guid.NewGuid().ToString("D"),
                Session: ClassroomPacketWriter.NewToken(),
                ToIp: ownIp,
                Text: "收訊端測試");

            var sinkResp = await sink.OnMessageAsync(peerIp, ownIp, reqMsg, () => true, default);
            Check(sinkResp is not null && sinkResp.StatusCode == 200, "Store sink received message successfully");

            // -----------------------------------------------------------------------------------------
            // Check 5: Negative control - Options without LocalEndpointOptionKey must fail closed (item 5)
            // -----------------------------------------------------------------------------------------
            var handlerNoOption = new TraceHttpMessageHandler { OwnIp = ownIp, TargetIp = peerIp, SetLocalEndpointOption = false };
            using var httpNoOption = new HttpClient(handlerNoOption);
            var senderNoOption = new ClassroomSender(store, httpNoOption);

            var negPrincipal = ClassroomPrincipalKey.ForSource("192.168.1.99")!.Value;
            var startNeg = store.BeginClassroomSend(negPrincipal, "負控測試：無 Options 端點", 0, 1);
            var startedNeg = (AttemptStarted)startNeg;
            var prepNeg = store.GetClassroomPreparedSend(startedNeg.MessageId)!;

            // 1. SendPreparedAsync with no endpoint option
            var sendNegOutcome = await senderNoOption.SendPreparedAsync(prepNeg, 8768, ownIp, () => true);
            Check(!sendNegOutcome.Success, "SendPreparedAsync without endpoint option failed");
            Check(sendNegOutcome.Code == "local_endpoint_unknown", "SendPreparedAsync code is local_endpoint_unknown");
            Check(sendNegOutcome.Completion is not Confirmed, "SendPreparedAsync without endpoint option is NOT Confirmed");

            // 2. SendReceiptAsync with no endpoint option
            var dummyReceipt = new PendingClassroomReceipt(1L, ClassroomPacketWriter.NewMessageId(), negPrincipal, "192.168.1.99", 1L);
            var receiptNegOutcome = await senderNoOption.SendReceiptAsync(dummyReceipt, 8768, ownIp, () => true);
            Check(!receiptNegOutcome.Completed, "SendReceiptAsync without endpoint option is NOT Completed");
            Check(receiptNegOutcome.Status == "local_endpoint_unknown", "SendReceiptAsync status is local_endpoint_unknown");

            Console.WriteLine($"Classroom send and store wiring: {checks} checks passed.");
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }
}
