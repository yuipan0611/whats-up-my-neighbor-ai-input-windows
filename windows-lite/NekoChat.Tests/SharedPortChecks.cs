using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using NekoChat.Core;
using NekoChat.Core.Storage;
using NekoChat.Core.V2;

namespace NekoChat.Tests;

internal static class SharedPortChecks
{
    public static async Task<int> Run()
    {
        int checks = 0;
        void Check(bool ok, string name) { checks++; if (!ok) throw new Exception("Shared LAN port: " + name); }
        string root = Path.Combine(Path.GetTempPath(), "neko-shared-port-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            using var a = new V2PacketWriter(ECDsa.Create(ECCurve.NamedCurves.nistP256), 16384);
            using var b = new V2PacketWriter(ECDsa.Create(ECCurve.NamedCurves.nistP256), 16384);
            using var aStore = ChatStore.Open(new(Path.Combine(root, "a")) { TextValidator = t => V2Text.Check(t, 16384) == V2TextProblem.None });
            using var bStore = ChatStore.Open(new(Path.Combine(root, "b")) { TextValidator = t => V2Text.Check(t, 16384) == V2TextProblem.None });
            aStore.EnsureLocalIdentity(a.PeerId, Convert.FromBase64String(a.PublicSpkiBase64), "fixture"); bStore.EnsureLocalIdentity(b.PeerId, Convert.FromBase64String(b.PublicSpkiBase64), "fixture");
            string aConversation = ((PeerRegistered)aStore.RegisterPeer(b.PeerId, PairingState.Paired, Convert.FromBase64String(b.PublicSpkiBase64))).Conversation.ConversationId;
            string bConversation = ((PeerRegistered)bStore.RegisterPeer(a.PeerId, PairingState.Paired, Convert.FromBase64String(a.PublicSpkiBase64))).Conversation.ConversationId;
            var receiver = new V2Receiver(bStore, b); await using var listener = new DirectLanListener(receiver, Options());
            var direct = new ConcurrentQueue<(string Ip, DirectMessage Message)>(); var text = new ConcurrentQueue<(string Ip, string Text)>(); var diagnostics = new ConcurrentQueue<string>(); int stored = 0;
            listener.DirectMessageReceived += (ip, message) => direct.Enqueue((ip, message)); listener.MessageReceived += (ip, caption) => text.Enqueue((ip, caption));
            listener.V2MessageStored += _ => Interlocked.Increment(ref stored); listener.Diagnostic += diagnostics.Enqueue; listener.Start();
            Check(listener.Running && listener.Port != 0, "compatibility facade starts real shared listener");
            Check(await DirectLanMessaging.SendDirectAsync("127.0.0.1", "legacy caption", listener.Port), "unchanged v1 sender still receives delivered courtesy reply");
            Check(direct.TryDequeue(out var item) && item == ("127.0.0.1", new DirectMessage("legacy caption", null)) && text.TryDequeue(out var caption) && caption == ("127.0.0.1", "legacy caption"), "both existing v1 events retain source and exact text");
            Check(stored == 0 && bStore.ListMessages(bConversation).Items.Count == 0 && PeerEvidenceCount(bStore) == 0, "legacy creates no verified message, read or presence fact");

            aStore.SaveDraft(aConversation, "verified next", 0, 1); var attempt = (AttemptStarted)aStore.BeginManualSend(aConversation, "verified next", 0, 1);
            using var client = new V2LanClient(aStore, a);
            var result = await client.SendPreparedAsync(aStore.GetPreparedSend(attempt.MessageId)!, "127.0.0.1", listener.Port, () => true);
            Check(result.Code == "ok" && bStore.ListMessages(bConversation).Items.Single().RawText == "verified next" && stored == 1 && PeerEvidenceCount(bStore) == 1, "same exact port receives real signed v2 and commits before ACK");
            Check(direct.IsEmpty && text.IsEmpty, "v2 never raises legacy events or silently downgrades identity");

            string emoji = string.Concat(Enumerable.Repeat("🐈", 2000));
            Check(await DirectLanMessaging.SendDirectAsync("127.0.0.1", emoji, listener.Port) && direct.TryDequeue(out item) && item.Message.Text == emoji && text.TryDequeue(out _),
                "v1 keeps2000 scalar allowance and body above8KiB independent from v2 control limit");
            byte[] Legacy(string value, string? reaction = null) => JsonSerializer.SerializeToUtf8Bytes(new { text = value, reaction });
            var future = await Post(listener.Port, "/nekochat/v1/direct", Legacy("聽不懂", "future-sticker"));
            Check(future.Status == 200 && direct.TryDequeue(out item) && item.Message.Reaction == "future-sticker" && text.TryDequeue(out caption) && caption.Text == "聽不懂" &&
                ClassroomReactions.ForIncoming(item.Message.Text, item.Message.Reaction, true) is null, "envelope-free old peer and unknown reaction remain readable ordinary text");
            foreach (var reaction in ClassroomReactions.All)
            {
                Check(await DirectLanMessaging.SendDirectAsync("127.0.0.1", reaction.Text, listener.Port, reaction: reaction.Id) &&
                    direct.TryDequeue(out item) && item.Message == new DirectMessage(reaction.Text, reaction.Id) && text.TryDequeue(out caption) && caption.Text == reaction.Text,
                    "shared port preserves reaction and legacy caption " + reaction.Id);
            }
            foreach (string malformed in new[] {
                "{\"text\":\"x\",\"sender_id\":\"claimed-person\"}", "{\"text\":\"x\",\"reaction\":true}", "{\"text\":\"x\",\"text\":\"y\"}",
                "{\"text\":\"x\",\"\\u0074ext\":\"y\"}", "{\"text\":\"\\ud800\"}", "{\"text\":\" \"}",
                "{\"protocol\":\"nekochat\",\"version\":2,\"kind\":\"direct.message\",\"text\":\"x\"}", "{\"protocol\":\"nekochat\",\"version\":1.0,\"kind\":\"direct.message\",\"text\":\"x\"}",
                "{\"protocol\":\"other\",\"version\":1,\"kind\":\"direct.message\",\"text\":\"x\"}" })
                Check((await Post(listener.Port, "/nekochat/v1/direct", Encoding.UTF8.GetBytes(malformed))).Status == 400 && direct.IsEmpty && text.IsEmpty, "malformed v1 rejected without either legacy event " + checks);
            Check((await Post(listener.Port, "/nekochat/v1/direct", Legacy(emoji + "🐈"))).Status == 400, "v1 scalar2001 rejected without changing shared Protocol.ValidText");
            Check((await Post(listener.Port, "/nekochat/v1/direct", new byte[65537])).Status == 413, "v1 body65536 independent hard upper bound");
            byte[] v2 = a.Message(V2PacketWriter.NewMessageId(), b.PeerId, V2PacketWriter.NewClientNonce(), "cannot guess version");
            Check((await Post(listener.Port, "/nekochat/v1/direct", v2)).Status == 400 && direct.IsEmpty && text.IsEmpty, "v2 body on v1 path never treated as unsigned legacy text");
            Check((await Post(listener.Port, "/nekochat/v2/message", Legacy("wrong route"))).Status == 400 && stored == 1, "legacy body on v2 path never becomes trusted data");
            receiver.SetReady(false);
            Check((await Post(listener.Port, "/nekochat/v2/hello", a.HelloRequest(V2PacketWriter.NewClientNonce()))).Status == 503, "v2 identity service can be held not-ready independently");
            Check(await DirectLanMessaging.SendDirectAsync("127.0.0.1", "unpaired classroom reaction", listener.Port) && direct.TryDequeue(out _) && text.TryDequeue(out _), "legacy path remains available while v2 service not-ready");
            receiver.SetReady(true);

            Action<string, DirectMessage> fail = (_, _) => throw new InvalidOperationException("private legacy content must not be logged"); listener.DirectMessageReceived += fail;
            bool failed = false; try { await DirectLanMessaging.SendDirectAsync("127.0.0.1", "unhandled callback", listener.Port); } catch (InvalidOperationException) { failed = true; }
            listener.DirectMessageReceived -= fail;
            Check(failed && listener.Running && diagnostics.Contains("request_failed") && !diagnostics.Any(d => d.Contains("private legacy", StringComparison.Ordinal)), "legacy callback failure cannot report delivered or kill listener or leak exception");
            // The first subscriber ran before the throwing subscriber, matching the existing legacy event ordering.
            while (direct.TryDequeue(out _)) { } while (text.TryDequeue(out _)) { }
            Check(await DirectLanMessaging.SendDirectAsync("127.0.0.1", "recovered callback", listener.Port) && direct.TryDequeue(out _) && text.TryDequeue(out _), "listener still receives after a failed legacy subscriber");
            listener.V2MessageStored += _ => throw new InvalidOperationException("private v2 view failure");
            var draft = aStore.GetDraftState(aConversation)!; aStore.SaveDraft(aConversation, "durable despite view", draft.Epoch, draft.Revision + 1);
            attempt = (AttemptStarted)aStore.BeginManualSend(aConversation, "durable despite view", draft.Epoch, draft.Revision + 1);
            result = await client.SendPreparedAsync(aStore.GetPreparedSend(attempt.MessageId)!, "127.0.0.1", listener.Port, () => true);
            Check(result.Code == "ok" && diagnostics.Contains("notification_failed") && bStore.ListMessages(bConversation).Items.Count == 2 && stored == 2, "v2 committed fact still ACKs when UI notice subscriber fails");
            Check(direct.IsEmpty && text.IsEmpty, "failed v2 UI callback cannot fall back to legacy receive");
        }
        finally { Directory.Delete(root, true); }
        await using (var onlyLegacy = new DirectLanListener(null, Options()))
        {
            int received = 0; onlyLegacy.MessageReceived += (_, _) => received++; onlyLegacy.Start();
            Check(await DirectLanMessaging.SendDirectAsync("127.0.0.1", "old application mode", onlyLegacy.Port) && received == 1, "existing constructor works without a key or selecting identity policy");
            Check((await Post(onlyLegacy.Port, "/nekochat/v2/hello", "{}"u8.ToArray())).Status == 503, "v1-only application explicitly refuses ready v2 claims");
        }
        await using (var limited = new DirectLanListener(null, Options() with { RequestsPerSourcePerMinute = 2 }))
        {
            int received = 0; limited.MessageReceived += (_, _) => received++; limited.Start();
            Check((await Post(limited.Port, "/nekochat/v1/direct", "{\"text\":\"one\"}"u8.ToArray())).Status == 200 &&
                (await Post(limited.Port, "/nekochat/v2/hello", "{}"u8.ToArray())).Status == 503 &&
                (await Post(limited.Port, "/nekochat/v1/direct", "{\"text\":\"three\"}"u8.ToArray())).Status == 429 && received == 1,
                "v1/v2 routes share the same source quota instead of multiplying it");
        }
        Console.WriteLine($"Shared v1/v2 LAN port: {checks} real TCP routing, compatibility, isolation and callback checks."); return checks;
    }
    private static V2ListenerOptions Options() => new() { BindAddress = IPAddress.Loopback, Port = 0, RequestsPerSourcePerMinute = 1000 };
    private static long PeerEvidenceCount(ChatStore store)
    {
        using var c = new SqliteConnection("Data Source=" + store.DatabasePath + ";Mode=ReadOnly;Pooling=False"); c.Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT count(*) FROM Peer WHERE lastSeenUtc IS NOT NULL OR installEvidence IS NOT NULL"; return Convert.ToInt64(cmd.ExecuteScalar());
    }
    private sealed record Response(int Status, byte[] Body);
    private static async Task<Response> Post(int port, string path, byte[] body)
    {
        using var http = new HttpClient(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(5) };
        using var content = new ByteArrayContent(body); content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        using var result = await http.PostAsync($"http://127.0.0.1:{port}{path}", content);
        return new((int)result.StatusCode, await result.Content.ReadAsByteArrayAsync());
    }
}
