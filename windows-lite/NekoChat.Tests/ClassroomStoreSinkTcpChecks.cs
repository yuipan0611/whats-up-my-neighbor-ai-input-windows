using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NekoChat.Core;
using NekoChat.Core.Classroom.Transport;
using NekoChat.Core.Classroom.Wire;
using NekoChat.Core.Storage;

namespace NekoChat.Tests;

/// <summary>
/// Ben review mandatory 2: ClassroomStoreSink real SQLite + real TCP tests.
/// - When ACK arrives, data is already durably stored in SQLite.
/// - Source IP is taken strictly from the physical socket, not payload claims.
/// - When store throws an exception, no ACK is returned (503 storage_unavailable).
/// </summary>
internal static class ClassroomStoreSinkTcpChecks
{
    internal static async Task<int> RunAsync()
    {
        int checks = 0;
        void Check(bool value, string label) { checks++; if (!value) throw new Exception("ClassroomStoreSink TCP check failed: " + label); }

        string tempBase = Path.Combine(Path.GetTempPath(), "nekochat-sink-tcp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempBase);

        try
        {
            // =========================================================================
            // 1. Happy Path: Real TCP + Real SQLite -> ACK guarantees durable storage
            // =========================================================================
            string dataDir = Path.Combine(tempBase, "store1");
            using (var store = ChatStore.Open(new ChatStoreOptions(dataDir)))
            {
                var sink = new ClassroomStoreSink(store);
                var adapter = new ClassroomEndpointAdapter(sink);
                adapter.SetReady(true);

                await using var listener = new ClassroomTcpListener(adapter, new ClassroomListenerOptions
                {
                    BindAddress = IPAddress.Loopback,
                    Port = 0
                });
                listener.Start();
                int port = listener.Port;

                using var http = new HttpClient();

                // Step 1: Hello handshake
                string nonce = ClassroomPacketWriter.NewToken();
                byte[] helloBytes = ClassroomPacketWriter.HelloRequest(nonce);
                using var helloReq = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{port}/nekochat/classroom/v1/hello")
                {
                    Content = new ByteArrayContent(helloBytes)
                };
                helloReq.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
                using var helloResp = await http.SendAsync(helloReq);
                Check(helloResp.IsSuccessStatusCode, "hello handshake succeeded over real TCP");

                byte[] helloRespBytes = await helloResp.Content.ReadAsByteArrayAsync();
                using var helloDoc = JsonDocument.Parse(helloRespBytes);
                string session = helloDoc.RootElement.GetProperty("session").GetString()!;
                Check(!string.IsNullOrEmpty(session), "session received from hello");

                // Step 2: Send message over TCP
                string msgId = Guid.NewGuid().ToString();
                string testText = "真實 TCP 訊息持久化驗證測試文本 🐈";
                byte[] msgBytes = ClassroomPacketWriter.MessageSend(msgId, session, "127.0.0.1", testText, null);

                using var msgReq = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{port}/nekochat/classroom/v1/message")
                {
                    Content = new ByteArrayContent(msgBytes)
                };
                msgReq.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };

                using var msgResp = await http.SendAsync(msgReq);
                Check(msgResp.IsSuccessStatusCode, "message send succeeded over real TCP");

                byte[] msgRespBytes = await msgResp.Content.ReadAsByteArrayAsync();
                using var msgDoc = JsonDocument.Parse(msgRespBytes);
                string status = msgDoc.RootElement.GetProperty("status").GetString()!;
                Check(status == "stored", "receiver returned stored ACK");

                // Assert 1: ACK arrival guarantees data is ALREADY stored in SQLite
                var principal = ClassroomPrincipalKey.ForSource("127.0.0.1")!.Value;
                var list = store.ListClassroomMessages(principal);
                var storedMsg = list.Items.FirstOrDefault(m => m.MessageId == msgId);
                Check(storedMsg is not null, "data is already durably stored in SQLite when ACK arrives");
                Check(storedMsg!.RawText == testText, "stored text matches sent text");
                Check(storedMsg.Direction == "incoming", "direction is incoming");

                // Assert 2: Source IP is strictly taken from socket (127.0.0.1)
                Check(storedMsg.SourceIp == "127.0.0.1", "source IP is strictly taken from the physical TCP socket");
                Check(storedMsg.Principal == principal, "principal is bound to socket source IP");
            }

            // =========================================================================
            // 2. Failure Path: Store throws exception -> NO ACK, returns 503 storage_unavailable
            // =========================================================================
            string dataDir2 = Path.Combine(tempBase, "store2");
            using (var store2 = ChatStore.Open(new ChatStoreOptions(dataDir2)))
            {
                var failingStore = ThrowingStoreProxy.Create(store2);
                var sinkFailing = new ClassroomStoreSink(failingStore);
                var adapterFailing = new ClassroomEndpointAdapter(sinkFailing);
                adapterFailing.SetReady(true);

                await using var listenerFailing = new ClassroomTcpListener(adapterFailing, new ClassroomListenerOptions
                {
                    BindAddress = IPAddress.Loopback,
                    Port = 0
                });
                listenerFailing.Start();
                int portFailing = listenerFailing.Port;

                using var http2 = new HttpClient();

                // Hello first
                string nonce2 = ClassroomPacketWriter.NewToken();
                byte[] helloBytes2 = ClassroomPacketWriter.HelloRequest(nonce2);
                using var helloReq2 = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{portFailing}/nekochat/classroom/v1/hello")
                {
                    Content = new ByteArrayContent(helloBytes2)
                };
                helloReq2.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
                using var helloResp2 = await http2.SendAsync(helloReq2);
                Check(helloResp2.IsSuccessStatusCode, "hello succeeded on failing server");

                byte[] helloRespBytes2 = await helloResp2.Content.ReadAsByteArrayAsync();
                using var helloDoc2 = JsonDocument.Parse(helloRespBytes2);
                string session2 = helloDoc2.RootElement.GetProperty("session").GetString()!;

                // Send message when store will throw
                string msgId2 = Guid.NewGuid().ToString();
                byte[] msgBytes2 = ClassroomPacketWriter.MessageSend(msgId2, session2, "127.0.0.1", "will fail store", null);

                using var msgReq2 = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{portFailing}/nekochat/classroom/v1/message")
                {
                    Content = new ByteArrayContent(msgBytes2)
                };
                msgReq2.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };

                using var msgResp2 = await http2.SendAsync(msgReq2);

                // Assert 3: Store exception does NOT return ACK; returns 503 storage_unavailable
                Check((int)msgResp2.StatusCode == 503, "store exception produces HTTP 503 instead of 200 stored ACK");
                byte[] errBytes = await msgResp2.Content.ReadAsByteArrayAsync();
                using var errDoc = JsonDocument.Parse(errBytes);
                string errCode = errDoc.RootElement.GetProperty("code").GetString()!;
                Check(errCode == ClassroomCodes.StorageUnavailable, "code is storage_unavailable when store throws exception");
            }

            // =========================================================================
            // 3. Mandatory D: Legacy Reason Mapping (rate_limited, receiver_not_ready, invalid_payload, fail-closed)
            // =========================================================================
            string dataDir3 = Path.Combine(tempBase, "store3");
            using (var store3 = ChatStore.Open(new ChatStoreOptions(dataDir3) { OffRoster = new ClassroomOffRosterLimits(MaxMessagesPerSource: 3) }))
            {
                var sink3 = new ClassroomStoreSink(store3);

                // D-1: invalid-text -> 400 invalid_payload
                var invalidMsg = new DirectMessage(new string('x', 2005), null);
                var respInvalid = await sink3.OnLegacyDirectAsync("127.0.0.1", invalidMsg, () => true, CancellationToken.None);
                Check(respInvalid is not null, "OnLegacyDirectAsync returned response for invalid-text");
                Check(respInvalid!.StatusCode == 400, "invalid-text returns HTTP 400");
                using (var doc = JsonDocument.Parse(respInvalid.Body))
                {
                    Check(doc.RootElement.GetProperty("code").GetString() == ClassroomCodes.InvalidPayload, "invalid-text mapped to invalid_payload");
                }

                // D-2: stale-legacy-scope -> 503 receiver_not_ready
                var validMsg = new DirectMessage("valid text", null);
                var respStale = await sink3.OnLegacyDirectAsync("127.0.0.1", validMsg, () => false, CancellationToken.None);
                Check(respStale is not null, "OnLegacyDirectAsync returned response for stale-legacy-scope");
                Check(respStale!.StatusCode == 503, "stale-legacy-scope returns HTTP 503");
                using (var doc = JsonDocument.Parse(respStale.Body))
                {
                    Check(doc.RootElement.GetProperty("code").GetString() == ClassroomCodes.ReceiverNotReady, "stale-legacy-scope mapped to receiver_not_ready");
                }

                // D-3: offroster-limit -> 429 rate_limited
                // Fill off-roster budget for 10.99.99.99
                string strangerIp = "10.99.99.99";
                for (int i = 0; i < 3; i++)
                {
                    store3.ReceiveLegacyMessage(new DirectMessage(new string('z', 10), null), store3.GetLegacyReceiveContext(strangerIp), () => true);
                }
                var respLimit = await sink3.OnLegacyDirectAsync(strangerIp, new DirectMessage(new string('z', 10), null), () => true, CancellationToken.None);
                Check(respLimit is not null, "OnLegacyDirectAsync returned response for offroster-limit");
                Check(respLimit!.StatusCode == 429, "offroster-limit returns HTTP 429");
                using (var doc = JsonDocument.Parse(respLimit.Body))
                {
                    Check(doc.RootElement.GetProperty("code").GetString() == ClassroomCodes.RateLimited, "offroster-limit mapped to rate_limited");
                }

                // D-4: Unknown reason -> fail closed to 503 receiver_not_ready
                var customProxy = MockReasonStoreProxy.Create(store3, "unrecognized_internal_error");
                var sinkCustom = new ClassroomStoreSink(customProxy);
                var respUnknown = await sinkCustom.OnLegacyDirectAsync("127.0.0.1", validMsg, () => true, CancellationToken.None);
                Check(respUnknown is not null, "OnLegacyDirectAsync returned response for unknown reason");
                Check(respUnknown!.StatusCode == 503, "unknown reason fail-closed returns HTTP 503");
                using (var doc = JsonDocument.Parse(respUnknown.Body))
                {
                    Check(doc.RootElement.GetProperty("code").GetString() == ClassroomCodes.ReceiverNotReady, "unknown reason fail-closed mapped to receiver_not_ready");
                }
            }

            // =========================================================================
            // 4. Mandatory C & Notification Rules: Duplicate fires 1x, Cleared fires 0x, v1 fires 1x
            // =========================================================================
            string dataDir4 = Path.Combine(tempBase, "store4");
            using (var store4 = ChatStore.Open(new ChatStoreOptions(dataDir4)))
            {
                var sink4 = new ClassroomStoreSink(store4);
                int classroomStoredFired = 0;
                int legacyStoredFired = 0;
                sink4.MessageStored += (_, _, _) => classroomStoredFired++;
                sink4.LegacyMessageStored += _ => legacyStoredFired++;

                string dupMsgId = ClassroomPacketWriter.NewMessageId();
                string validSession = ClassroomPacketWriter.NewToken();
                var req1 = new ClassroomRequest(ClassroomRequestKind.Message, MessageId: dupMsgId, Session: validSession, ToIp: "127.0.0.1", Text: "測試去重訊息");

                // C-1: Same ID sent twice -> Event fires exactly 1 time
                var respMsg1 = await sink4.OnMessageAsync("127.0.0.1", "127.0.0.1", req1, () => true, CancellationToken.None);
                Check(classroomStoredFired == 1, "first message send fires MessageStored once");

                var respMsg2 = await sink4.OnMessageAsync("127.0.0.1", "127.0.0.1", req1, () => true, CancellationToken.None);
                Check(classroomStoredFired == 1, "second message send (duplicate) does NOT fire MessageStored (exactly 1 time total)");
                using (var doc = JsonDocument.Parse(respMsg2!.Body))
                {
                    Check(doc.RootElement.GetProperty("status").GetString() == "duplicate", "second send returned duplicate status");
                }

                // C-2: Clear conversation and resend -> Fires 0 additional times
                var pKey = ClassroomPrincipalKey.ForSource("127.0.0.1")!.Value;
                store4.ClearClassroomConversation(pKey);

                int beforeClearedResend = classroomStoredFired;
                var respMsgCleared = await sink4.OnMessageAsync("127.0.0.1", "127.0.0.1", req1, () => true, CancellationToken.None);
                Check(classroomStoredFired == beforeClearedResend, "resend after clear (ClearedDuplicate) fires 0 additional times");
                using (var doc = JsonDocument.Parse(respMsgCleared!.Body))
                {
                    Check(doc.RootElement.GetProperty("status").GetString() == "duplicate", "cleared resend returned duplicate status");
                }

                // C-3: v1 legacy message fires exactly 1 time (LegacyMessageStored only, NOT MessageStored)
                int legacyBefore = legacyStoredFired;
                int classroomBefore = classroomStoredFired;
                var v1Msg = new DirectMessage("v1 single notification test", null);
                var respV1 = await sink4.OnLegacyDirectAsync("127.0.0.1", v1Msg, () => true, CancellationToken.None);
                Check(respV1 is not null && respV1.StatusCode == 200, "v1 direct message stored successfully");
                Check(legacyStoredFired == legacyBefore + 1, "v1 message fires LegacyMessageStored exactly once");
                Check(classroomStoredFired == classroomBefore, "v1 message does NOT fire MessageStored (avoids double notification)");
            }

            Console.WriteLine($"ClassroomStoreSink TCP checks: {checks} checks passed.");
            return checks;
        }
        finally
        {
            try { Directory.Delete(tempBase, recursive: true); } catch { }
        }
    }

    public class ThrowingStoreProxy : DispatchProxy
    {
        private IChatStore _target = null!;

        public static IChatStore Create(IChatStore target)
        {
            object proxy = Create<IChatStore, ThrowingStoreProxy>();
            ((ThrowingStoreProxy)proxy)._target = target;
            return (IChatStore)proxy;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == nameof(IChatStore.ReceiveClassroomMessage))
            {
                throw new StoreException(StoreErrorKind.Corrupt, "Simulated store failure for TCP negative testing.");
            }
            return targetMethod?.Invoke(_target, args);
        }
    }

    public class MockReasonStoreProxy : DispatchProxy
    {
        private IChatStore _target = null!;
        private string _reason = null!;

        public static IChatStore Create(IChatStore target, string reason)
        {
            object proxy = Create<IChatStore, MockReasonStoreProxy>();
            ((MockReasonStoreProxy)proxy)._target = target;
            ((MockReasonStoreProxy)proxy)._reason = reason;
            return (IChatStore)proxy;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == nameof(IChatStore.ReceiveLegacyMessage))
            {
                return new LegacyReceiveResult(false, _reason);
            }
            return targetMethod?.Invoke(_target, args);
        }
    }
}
