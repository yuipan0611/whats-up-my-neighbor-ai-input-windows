using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using NekoChat.Core;

internal static class ReactionChecks
{
    internal static async Task<int> Run()
    {
        int checks = 0;
        void Check(bool value, string name) { checks++; if (!value) throw new Exception("Reactions: " + name); }
        foreach (var r in ClassroomReactions.All)
        {
            Check(ClassroomReactions.ForIncoming(r.Text, r.Id, true) == r, "teacher receives " + r.Id);
            Check((ClassroomReactions.ForIncoming(r.Text, r.Id, false) is null) == r.TeacherOnly, "recipient scope " + r.Id);
            Check(ClassroomReactions.MatchText("  " + r.Text + "！！。 ") == r, "full-phrase punctuation " + r.Id);
        }
        foreach (var text in new[] { "不會太快", "我沒有聽不懂", "他說跟不上，但我可以", "這張圖寫著聽不懂", "聽不懂嗎", "" })
            Check(ClassroomReactions.MatchText(text) is null, "ordinary chat is not a sentiment classifier: " + text);
        Check(ClassroomReactions.ForIncoming("聽不懂", "future-sticker", true) is null, "unknown sticker falls back to text");
        Check(ClassroomReactions.ForIncoming("普通訊息", "dont-understand", true) is null, "metadata cannot replace unrelated text");
        var fast = ClassroomReactions.Find("too-fast")!;
        var inbox = new ReactionInbox();
        inbox.Enqueue(fast, "192.168.24.1", 10);
        Check(inbox.Current is null && inbox.PendingCount == 1, "deferred until UI is ready");
        Check(inbox.TryStart(20)?.SenderIp == "192.168.24.1" && inbox.Deadline == 22, "two seconds start when shown");
        inbox.Enqueue(fast, "192.168.24.2", 21.9);
        Check(inbox.Current?.Count == 2 && inbox.Deadline == 22, "same reaction aggregates without prolonging alert");
        for (int n = 0; n < 100; n++)
            foreach (var r in ClassroomReactions.All) inbox.Enqueue(r, "192.168.24.3", 21.9);
        Check(inbox.PendingCount == 4, "burst remains bounded to one pending entry per other type");
        Check(inbox.TryStart(21.9) is null, "cannot overlap alerts");
        inbox.Complete();
        Check(inbox.TryStart(25)?.Reaction.Id == "cant-keep-up" && inbox.Deadline == 27, "next reaction gets its own two seconds");

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var reserve = new TcpListener(IPAddress.Loopback, 0); reserve.Start();
        int port = ((IPEndPoint)reserve.LocalEndpoint).Port; reserve.Stop();
        await using var listener = new DirectLanListener(port);
        var received = Channel.CreateUnbounded<(string Ip, DirectMessage Message)>();
        var legacy = Channel.CreateUnbounded<string>();
        listener.DirectMessageReceived += (ip, message) => received.Writer.TryWrite((ip, message));
        listener.MessageReceived += (_, text) => legacy.Writer.TryWrite(text);
        listener.Start();
        foreach (var r in ClassroomReactions.All)
        {
            Check(await DirectLanMessaging.SendDirectAsync("127.0.0.1", r.Text, port, timeout.Token, r.Id), "sticker receipt " + r.Id);
            var actual = await received.Reader.ReadAsync(timeout.Token);
            Check(actual == ("127.0.0.1", new DirectMessage(r.Text, r.Id)), "real TCP preserves source/text/sticker " + r.Id);
            Check(await legacy.Reader.ReadAsync(timeout.Token) == r.Text, "legacy text subscriber gets readable caption " + r.Id);
        }
        using var http = new HttpClient(new HttpClientHandler { UseProxy = false });
        using var content = new StringContent("""{"text":"聽不懂","reaction":"future-sticker"}""", Encoding.UTF8, "application/json");
        using var response = await http.PostAsync($"http://127.0.0.1:{port}/nekochat/v1/direct", content, timeout.Token);
        var future = await received.Reader.ReadAsync(timeout.Token);
        Check(response.IsSuccessStatusCode && future.Message.Reaction == "future-sticker" &&
            ClassroomReactions.ForIncoming(future.Message.Text, future.Message.Reaction, true) is null, "unknown wire ID remains readable ordinary text");

        // Model an old peer: it only reads text and ignores the new optional field.
        using var oldPeer = new TcpListener(IPAddress.Loopback, 0); oldPeer.Start();
        int oldPort = ((IPEndPoint)oldPeer.LocalEndpoint).Port;
        var oldReceive = ReadLegacyAsync(oldPeer, timeout.Token);
        Check(await DirectLanMessaging.SendDirectAsync("127.0.0.1", fast.Text, oldPort, timeout.Token, fast.Id), "new sender accepts old delivered receipt");
        Check(await oldReceive == fast.Text, "old peer sees caption without new schema support");
        Console.WriteLine($"Reactions: {checks} trigger, scope, burst and real TCP compatibility checks passed.");
        return checks;
    }

    private static async Task<string> ReadLegacyAsync(TcpListener listener, CancellationToken cancellation)
    {
        using var client = await listener.AcceptTcpClientAsync(cancellation);
        await using var stream = client.GetStream();
        var header = new List<byte>(); var single = new byte[1];
        while (!Encoding.ASCII.GetString(header.ToArray()).EndsWith("\r\n\r\n", StringComparison.Ordinal))
        {
            await stream.ReadExactlyAsync(single, cancellation); header.Add(single[0]);
            if (header.Count > 8192) throw new IOException("test header overflow");
        }
        var line = Encoding.ASCII.GetString(header.ToArray()).Split("\r\n").Single(l => l.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase));
        var body = new byte[int.Parse(line[15..].Trim())]; await stream.ReadExactlyAsync(body, cancellation);
        using var json = JsonDocument.Parse(body);
        const string ack = "{\"status\":\"delivered\"}";
        await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Length: {ack.Length}\r\nConnection: close\r\n\r\n{ack}"), cancellation);
        return json.RootElement.GetProperty("text").GetString()!;
    }
}
