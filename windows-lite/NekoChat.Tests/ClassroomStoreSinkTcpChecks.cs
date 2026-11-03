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
}
