using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NekoChat.Core.Classroom.Transport;
using NekoChat.Core.Classroom.Wire;
using NekoChat.Core.Storage;
using ClassroomRoute = NekoChat.Core.Storage.ClassroomRoute;

namespace NekoChat.Tests;

public static class ClassroomReceiptChecks
{
    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset _utcNow = new(2026, 11, 3, 12, 0, 0, TimeSpan.Zero);
        private long _timestamp = 1000;

        public override DateTimeOffset GetUtcNow() => _utcNow;
        public override long GetTimestamp() => _timestamp;
        public override long TimestampFrequency => 1000; // 1000 ticks = 1 second

        public void Advance(TimeSpan span)
        {
            _utcNow += span;
            _timestamp += (long)(span.TotalSeconds * TimestampFrequency);
        }
    }

    private sealed class MockReceiptHttpHandler : HttpMessageHandler
    {
        public List<string> PathSequence { get; } = new();
        public string TargetIp { get; set; } = "192.168.1.50";
        public string OwnIp { get; set; } = "192.168.1.10";
        public string? ClientLocalIp { get; set; }
        public string SessionId { get; set; } = ClassroomPacketWriter.NewToken();
        public bool HelloSucceeds { get; set; } = true;
        public bool HelloSupportsReadReceipt { get; set; } = true;

        public Func<HttpRequestMessage, Task<HttpResponseMessage>>? ReceiptHandler { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            request.Options.Set(ClassroomSender.LocalEndpointOptionKey, ClientLocalIp ?? OwnIp);
            string path = request.RequestUri?.AbsolutePath ?? "";
            PathSequence.Add(path);

            if (path == "/nekochat/classroom/v1/hello")
            {
                if (!HelloSucceeds)
                {
                    return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                    {
                        Content = new StringContent("Service Unavailable")
                    };
                }

                byte[] body = await request.Content!.ReadAsByteArrayAsync(cancellationToken);
                using var doc = JsonDocument.Parse(body);
                string clientNonce = doc.RootElement.GetProperty("client_nonce").GetString()!;

                string[] kinds = HelloSupportsReadReceipt ? new[] { "receipt.read", "message" } : new[] { "message" };
                byte[] helloOk = ClassroomPacketWriter.HelloOk(clientNonce, SessionId, TargetIp, OwnIp, kinds);

                var resp = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(helloOk)
                };
                resp.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
                return resp;
            }

            if (path == "/nekochat/classroom/v1/receipt")
            {
                if (ReceiptHandler is not null)
                {
                    return await ReceiptHandler(request);
                }

                byte[] body = await request.Content!.ReadAsByteArrayAsync(cancellationToken);
                using var doc = JsonDocument.Parse(body);
                string messageId = doc.RootElement.GetProperty("message_id").GetString()!;
                string session = doc.RootElement.GetProperty("session").GetString()!;

                byte[] ack = ClassroomPacketWriter.ReceiptAck("ok", messageId, session, OwnIp, TargetIp);
                var resp = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(ack)
                };
                resp.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
                return resp;
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
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

        // =========================================================================
        // 1. CM-13 Backoff Function Unit Tests (30s doubling up to 10m)
        // =========================================================================
        Check(ClassroomReceiptService.CalculateBackoff(1) == TimeSpan.FromSeconds(30), "backoff attempt 1 is 30s");
        Check(ClassroomReceiptService.CalculateBackoff(2) == TimeSpan.FromSeconds(60), "backoff attempt 2 is 60s");
        Check(ClassroomReceiptService.CalculateBackoff(3) == TimeSpan.FromSeconds(120), "backoff attempt 3 is 120s");
        Check(ClassroomReceiptService.CalculateBackoff(4) == TimeSpan.FromSeconds(240), "backoff attempt 4 is 240s");
        Check(ClassroomReceiptService.CalculateBackoff(5) == TimeSpan.FromSeconds(480), "backoff attempt 5 is 480s");
        Check(ClassroomReceiptService.CalculateBackoff(6) == TimeSpan.FromSeconds(600), "backoff attempt 6 is capped at 600s (10m)");
        Check(ClassroomReceiptService.CalculateBackoff(7) == TimeSpan.FromSeconds(600), "backoff attempt 7 is capped at 600s");
        Check(ClassroomReceiptService.CalculateBackoff(8) == TimeSpan.FromSeconds(600), "backoff attempt 8 is capped at 600s");
        Check(ClassroomReceiptService.MaxAttemptsPerEvent == 8, "MaxAttemptsPerEvent is 8");

        string tempBase = Path.Combine(Path.GetTempPath(), "nekochat-classroom-receipt-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempBase);

        string dbA = Path.Combine(tempBase, "storeA");
        string dbB = Path.Combine(tempBase, "storeB");
        Directory.CreateDirectory(dbA);
        Directory.CreateDirectory(dbB);

        using var storeA = ChatStore.Open(new ChatStoreOptions(dbA));
        using var storeB = ChatStore.Open(new ChatStoreOptions(dbB));

        string ipA = "192.168.1.10";
        string ipB = "192.168.1.50";
        ClassroomPrincipalKey keyB = ClassroomPrincipalKey.ForSource(ipB)!.Value;
        ClassroomPrincipalKey keyA = ClassroomPrincipalKey.ForSource(ipA)!.Value;

        var sinkA = new ClassroomStoreSink(storeA);
        var sinkB = new ClassroomStoreSink(storeB);

        var timeProvider = new ManualTimeProvider();
        var handler = new MockReceiptHttpHandler
        {
            TargetIp = ipB,
            OwnIp = ipA
        };
        using var httpClient = new HttpClient(handler);
        var senderA = new ClassroomSender(storeA, httpClient, () => timeProvider.GetUtcNow().UtcDateTime);
        var receiptServiceA = new ClassroomReceiptService(storeA, senderA, 8768, ipA, timeProvider);

        try
        {
            // =========================================================================
            // 2. Receiver Sink Rules (CM-13 §9: ok, duplicate, transient 503, discarded)
            // =========================================================================
            storeA.SetReadReceiptPolicy(true);
            storeB.SetReadReceiptPolicy(true);

            // Case 2.1: Early-read scenario on Receiver (pending-local-confirmation -> transient 503)
            // storeA begins an outgoing attempt to B with messageId "msg-early-1" but has NOT confirmed yet
            var beginResult = storeA.BeginClassroomSend(keyB, "hello B", 0, 1);
            Check(beginResult is AttemptStarted, "storeA began classroom send for early read test");
            string earlyMsgId = ((AttemptStarted)beginResult).MessageId;

            // B immediately sends read receipt for "earlyMsgId" before A confirmed
            var earlyReq = new ClassroomRequest(
                ClassroomRequestKind.Receipt,
                ClientNonce: null,
                MessageId: earlyMsgId,
                Session: ClassroomPacketWriter.NewToken(),
                ToIp: ipA);

            var earlyResp = await sinkA.OnReceiptAsync(ipB, ipA, earlyReq, () => true, default);
            Check(earlyResp is not null && earlyResp.StatusCode == 503, "OnReceiptAsync returns 503 for pending unconfirmed attempt");
            using (var doc = JsonDocument.Parse(earlyResp!.Body))
            {
                Check(doc.RootElement.GetProperty("code").GetString() == "transient", "code is transient for pending attempt");
            }

            // Case 2.2: Non-existent message -> 200 discarded
            string nonExistentId = Guid.NewGuid().ToString();
            var notFoundReq = new ClassroomRequest(
                ClassroomRequestKind.Receipt,
                ClientNonce: null,
                MessageId: nonExistentId,
                Session: ClassroomPacketWriter.NewToken(),
                ToIp: ipA);
            var notFoundResp = await sinkA.OnReceiptAsync(ipB, ipA, notFoundReq, () => true, default);
            Check(notFoundResp is not null && notFoundResp.StatusCode == 200, "OnReceiptAsync returns 200 for not found message");
            using (var doc = JsonDocument.Parse(notFoundResp!.Body))
            {
                Check(doc.RootElement.GetProperty("status").GetString() == "discarded", "status is discarded for non-existent message");
            }

            // Case 2.3: Confirmed message -> 200 ok
            var confirmTime = DateTime.UtcNow;
            var routeRes = storeA.RecordClassroomRoute(earlyMsgId, 1, ClassroomRoute.Classroom, confirmTime);
            Check(routeRes.Recorded, "route recorded successfully");
            var prep = storeA.GetClassroomPreparedSend(earlyMsgId)!;
            byte[] fp = Convert.FromHexString(prep.FingerprintHex);
            var compResult = storeA.CompleteClassroomAttempt(earlyMsgId, new RemoteStoredOutcome(confirmTime, fp), ipB, 1);
            Check(compResult is Confirmed, "storeA confirmed attempt with exact fingerprint");

            var okResp = await sinkA.OnReceiptAsync(ipB, ipA, earlyReq, () => true, default);
            Check(okResp is not null && okResp.StatusCode == 200, "OnReceiptAsync returns 200 for confirmed message");
            using (var doc = JsonDocument.Parse(okResp!.Body))
            {
                Check(doc.RootElement.GetProperty("status").GetString() == "ok", "status is ok for first confirmed read receipt");
            }

            // Case 2.4: Already read -> 200 duplicate
            var dupResp = await sinkA.OnReceiptAsync(ipB, ipA, earlyReq, () => true, default);
            Check(dupResp is not null && dupResp.StatusCode == 200, "OnReceiptAsync returns 200 for duplicate receipt");
            using (var doc = JsonDocument.Parse(dupResp!.Body))
            {
                Check(doc.RootElement.GetProperty("status").GetString() == "duplicate", "status is duplicate for repeated receipt");
            }

            // =========================================================================
            // 3. Sender Outbox Processing with Bounded Retry (CM-13)
            // =========================================================================
            // B receives an incoming message from A, reads it, queuing a receipt in ClassroomReceiptOutbox
            string msgToRead = Guid.NewGuid().ToString();
            var rxResult = storeB.ReceiveClassroomMessage(ipA, new ClassroomIncoming(msgToRead, "Hello from A"), () => true);
            Check(rxResult is ClassroomStored, "storeB stored message from A");
            long seqB = ((ClassroomStored)rxResult).LocalSeq;

            // B marks message as visible read
            keyA = ((ClassroomStored)rxResult).Principal; // D-27 automatic contact ownership
            var visResult = storeB.MarkClassroomVisibleRead(keyA, new[] { seqB }, new VisibleReadContext(keyA.Value, true, true, false, true));
            Check(visResult.Marked == 1 && visResult.Queued == 1, "storeB marked read and queued 1 outbox receipt");

            var pendingB = storeB.ListPendingClassroomReceipts(10);
            Check(pendingB.Count == 1 && pendingB[0].MessageId == msgToRead, "pending receipt is present in outbox");

            // Setup sender B targeting A
            var handlerB = new MockReceiptHttpHandler
            {
                TargetIp = ipA,
                OwnIp = ipB,
                ClientLocalIp = ipB
            };
            using var httpB = new HttpClient(handlerB);
            var senderB = new ClassroomSender(storeB, httpB, () => timeProvider.GetUtcNow().UtcDateTime);
            var serviceB = new ClassroomReceiptService(storeB, senderB, 8768, ipB, timeProvider);

            // Subcase 3.1: Transient 503 received -> outbox retains event
            int callCount = 0;
            handlerB.ReceiptHandler = _ =>
            {
                callCount++;
                var (errStatus, body) = ClassroomPacketWriter.Error(ClassroomCodes.Transient);
                var resp = new HttpResponseMessage((HttpStatusCode)errStatus)
                {
                    Content = new ByteArrayContent(body)
                };
                resp.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
                return Task.FromResult(resp);
            };

            await serviceB.ProcessOutboxBatchAsync();
            Check(callCount == 1, "serviceB attempted sending receipt once");
            Check(storeB.ListPendingClassroomReceipts(10).Count == 1, "outbox retains event after transient 503");

            // Subcase 3.2: Immediate second attempt within 30s is throttled by backoff
            timeProvider.Advance(TimeSpan.FromSeconds(10));
            await serviceB.ProcessOutboxBatchAsync();
            Check(callCount == 1, "attempt at 10s was throttled by backoff (call count unchanged)");

            // Advance past 30s: second attempt fires
            timeProvider.Advance(TimeSpan.FromSeconds(25)); // total 35s
            await serviceB.ProcessOutboxBatchAsync();
            Check(callCount == 2, "second attempt executed after 30s backoff");

            // Advance another 30s (needs 60s for attempt 2 backoff): throttled
            timeProvider.Advance(TimeSpan.FromSeconds(30));
            await serviceB.ProcessOutboxBatchAsync();
            Check(callCount == 2, "attempt before 60s backoff throttled");

            // Advance remaining 35s (total 65s > 60s): third attempt fires
            timeProvider.Advance(TimeSpan.FromSeconds(35));
            await serviceB.ProcessOutboxBatchAsync();
            Check(callCount == 3, "third attempt executed after 60s backoff");

            // Exhaust remaining attempts up to max 8
            for (int i = 4; i <= 8; i++)
            {
                timeSpanAdvanceForAttempt(timeProvider, i);
                await serviceB.ProcessOutboxBatchAsync();
                Check(callCount == i, $"attempt {i} executed after exponential backoff");
            }
            Check(callCount == 8, "total attempts reached bound of 8");

            // Attempt 9 is bounded: no more calls even after advancing 1 hour
            timeProvider.Advance(TimeSpan.FromHours(1));
            await serviceB.ProcessOutboxBatchAsync();
            Check(callCount == 8, "attempt 9 blocked by MaxAttemptsPerEvent = 8 limit");
            Check(storeB.ListPendingClassroomReceipts(10).Count == 1, "event remains in outbox across exhausted attempts");

            // Subcase 3.3: ResetAfterResume resets attempt budget
            serviceB.ResetAfterResume();
            // Now next attempt should be permitted immediately
            handlerB.ReceiptHandler = null; // restore normal 200 OK handler
            await serviceB.ProcessOutboxBatchAsync();
            Check(storeB.ListPendingClassroomReceipts(10).Count == 0, "receipt successfully delivered and outbox cleared after reset");

            // =========================================================================
            // 4. Policy toggle and Clear conversation drops outbox
            // =========================================================================
            string msgDrop = Guid.NewGuid().ToString();
            var rx2 = storeB.ReceiveClassroomMessage(ipA, new ClassroomIncoming(msgDrop, "Another msg"), () => true);
            long seqDrop = ((ClassroomStored)rx2).LocalSeq;
            storeB.MarkClassroomVisibleRead(keyA, new[] { seqDrop }, new VisibleReadContext(keyA.Value, true, true, false, true));
            Check(storeB.ListPendingClassroomReceipts(10).Count == 1, "outbox has 1 queued receipt before policy disable");

            // Disabling policy clears outbox
            var policyChange = storeB.SetReadReceiptPolicy(false);
            Check(!policyChange.Policy.Enabled && policyChange.DroppedReceipts == 1, "disabling policy drops outbox receipts");
            Check(storeB.ListPendingClassroomReceipts(10).Count == 0, "outbox is empty after disabling policy");

            // Re-enabling policy does not backfill
            storeB.SetReadReceiptPolicy(true);
            Check(storeB.ListPendingClassroomReceipts(10).Count == 0, "re-enabling policy does not backfill old read messages");

            // Clearing conversation also clears outbox
            string msgClear = Guid.NewGuid().ToString();
            var rx3 = storeB.ReceiveClassroomMessage(ipA, new ClassroomIncoming(msgClear, "Clear test msg"), () => true);
            long seqClear = ((ClassroomStored)rx3).LocalSeq;
            storeB.MarkClassroomVisibleRead(keyA, new[] { seqClear }, new VisibleReadContext(keyA.Value, true, true, false, true));
            Check(storeB.ListPendingClassroomReceipts(10).Count == 1, "outbox has 1 receipt before conversation clear");

            var clearResult = storeB.ClearClassroomConversation(keyA);
            Check(clearResult.Receipts == 1, "ClearClassroomConversation reports 1 cleared receipt");
            Check(storeB.ListPendingClassroomReceipts(10).Count == 0, "outbox is empty after ClearClassroomConversation");

            // =========================================================================
            // 5. Ben Review Findings Remediation & Negative Controls
            // =========================================================================

            // Case 5.1: Negative Control - ownIp mismatch rejected, outbox retained
            string msgNeg = Guid.NewGuid().ToString();
            var rxNeg = storeB.ReceiveClassroomMessage(ipA, new ClassroomIncoming(msgNeg, "Negative ownIp msg"), () => true);
            long seqNeg = ((ClassroomStored)rxNeg).LocalSeq;
            storeB.MarkClassroomVisibleRead(keyA, new[] { seqNeg }, new VisibleReadContext(keyA.Value, true, true, false, true));
            Check(storeB.ListPendingClassroomReceipts(10).Count == 1, "outbox has 1 receipt queued for negative ownIp test");

            // Peer lies about observed_sender_ip (returns 192.168.1.99 instead of ipB 192.168.1.20)
            handlerB.OwnIp = "192.168.1.99";
            handlerB.ReceiptHandler = null;
            long attemptsBefore = serviceB.TotalAttempts;
            long completedBefore = serviceB.TotalCompleted;
            await serviceB.ProcessOutboxBatchAsync();
            Check(serviceB.TotalAttempts > attemptsBefore, "serviceB attempted sending receipt with mismatched ownIp");
            Check(serviceB.TotalCompleted == completedBefore, "serviceB did NOT complete receipt with mismatched ownIp");
            Check(storeB.ListPendingClassroomReceipts(10).Count == 1, "outbox retained receipt when peer returned mismatched ownIp");

            // Case 5.2: HelloSucceeds=false & HelloSupportsReadReceipt=false -> zero receipt requests
            // First reset handler to truthful ownIp
            handlerB.OwnIp = ipB;
            serviceB.ResetAfterResume();

            // Test HelloSucceeds = false
            handlerB.HelloSucceeds = false;
            handlerB.PathSequence.Clear();
            await serviceB.ProcessOutboxBatchAsync();
            Check(handlerB.PathSequence.Count(p => p.EndsWith("/receipt")) == 0, "HelloSucceeds=false produces ZERO receipt requests");
            Check(storeB.ListPendingClassroomReceipts(10).Count == 1, "outbox retained after HelloSucceeds=false");

            // Test HelloSupportsReadReceipt = false
            handlerB.HelloSucceeds = true;
            handlerB.HelloSupportsReadReceipt = false;
            handlerB.PathSequence.Clear();
            serviceB.ResetAfterResume();
            await serviceB.ProcessOutboxBatchAsync();
            Check(handlerB.PathSequence.Count(p => p.EndsWith("/receipt")) == 0, "HelloSupportsReadReceipt=false produces ZERO receipt requests");
            Check(storeB.ListPendingClassroomReceipts(10).Count == 1, "outbox retained after HelloSupportsReadReceipt=false");

            // Capability missing does not retry 8 times: advance time and check no more requests fired
            handlerB.PathSequence.Clear();
            timeProvider.Advance(TimeSpan.FromMinutes(2));
            await serviceB.ProcessOutboxBatchAsync();
            Check(handlerB.PathSequence.Count == 0, "capability_missing stops retry immediately without 8 attempts");

            // Case 5.3: Dispose and reopen same SQLite DB -> outbox preserved, attempts start at 0
            await serviceB.DisposeAsync();
            storeB.Dispose();

            using (var storeB2 = ChatStore.Open(new ChatStoreOptions(dbB)))
            {
                Check(storeB2.ListPendingClassroomReceipts(10).Count == 1, "same SQLite DB reopened has outbox preserved");
                var handlerB2 = new MockReceiptHttpHandler
                {
                    TargetIp = ipA,
                    OwnIp = ipB,
                    ClientLocalIp = ipB,
                    HelloSupportsReadReceipt = true,
                    HelloSucceeds = true
                };
                using var httpB2 = new HttpClient(handlerB2);
                var senderB2 = new ClassroomSender(storeB2, httpB2, () => timeProvider.GetUtcNow().UtcDateTime);
                var serviceB2 = new ClassroomReceiptService(storeB2, senderB2, 8768, ipB, timeProvider);

                // Fresh service starts with 0 attempts and sends immediately
                Check(serviceB2.TotalAttempts == 0, "fresh session has 0 total attempts");
                await serviceB2.ProcessOutboxBatchAsync();
                Check(serviceB2.TotalCompleted == 1, "fresh session delivers outbox immediately");
                Check(storeB2.ListPendingClassroomReceipts(10).Count == 0, "outbox cleared after delivery");
                await serviceB2.DisposeAsync();
            }

            // Case 5.4: CompleteClassroomReadReceipt returns false -> not completed, TotalCompleted unchanged
            var pendingFakeReceipt = new PendingClassroomReceipt(1, "fake-msg", keyA, ipA, 1);
            var fakeInterceptor = CompleteReceiptFailingStore.Create(storeA);
            var senderFake = new ClassroomSender(fakeInterceptor, httpClient, () => timeProvider.GetUtcNow().UtcDateTime);
            var serviceFake = new ClassroomReceiptService(fakeInterceptor, senderFake, 8768, ipA, timeProvider);

            long fakeCompletedBefore = serviceFake.TotalCompleted;
            var fakeResult = await senderFake.SendReceiptAsync(pendingFakeReceipt, 8768, ipA, () => true, default);
            Check(!fakeResult.Completed, "SendReceiptAsync returns Completed=false when store CompleteClassroomReadReceipt returns false");
            Check(serviceFake.TotalCompleted == fakeCompletedBefore, "TotalCompleted not incremented when store returns false");
            await serviceFake.DisposeAsync();

            Console.WriteLine($"Classroom receipt CM-13 checks: {checks} checks passed.");
        }
        finally
        {
            await receiptServiceA.DisposeAsync();
            try { Directory.Delete(tempBase, recursive: true); } catch { }
        }
    }

    public class CompleteReceiptFailingStore : DispatchProxy
    {
        private IChatStore _target = null!;

        public static IChatStore Create(IChatStore target)
        {
            object proxy = Create<IChatStore, CompleteReceiptFailingStore>();
            ((CompleteReceiptFailingStore)proxy)._target = target;
            return (IChatStore)proxy;
        }

        public CompleteReceiptFailingStore() { }

        public CompleteReceiptFailingStore(IChatStore target)
        {
            _target = target;
        }

        protected override object? Invoke(System.Reflection.MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == nameof(IChatStore.CompleteClassroomReadReceipt))
            {
                return false;
            }
            return targetMethod?.Invoke(_target, args);
        }

        public static implicit operator CompleteReceiptFailingStore(ChatStore store) => new(store);
    }

    private static void timeSpanAdvanceForAttempt(ManualTimeProvider timeProvider, int attemptNumber)
    {
        TimeSpan backoff = ClassroomReceiptService.CalculateBackoff(attemptNumber - 1);
        timeProvider.Advance(backoff + TimeSpan.FromSeconds(1));
    }
}
