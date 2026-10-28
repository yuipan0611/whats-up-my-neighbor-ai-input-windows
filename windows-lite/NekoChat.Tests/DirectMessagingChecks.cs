using System.Net;
using System.Net.Sockets;
using System.Text;
using NekoChat.Core;

internal static class DirectMessagingChecks
{
    internal static async Task<int> Run()
    {
        int checks = 0;
        void Check(bool condition, string name)
        {
            checks++;
            if (!condition) throw new Exception("Direct messaging: " + name);
        }
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        var reservationA = new TcpListener(IPAddress.Loopback, 0);
        var reservationB = new TcpListener(IPAddress.Loopback, 0);
        reservationA.Start(); reservationB.Start();
        int portA = ((IPEndPoint)reservationA.LocalEndpoint).Port;
        int portB = ((IPEndPoint)reservationB.LocalEndpoint).Port;
        reservationA.Stop(); reservationB.Stop();

        await using (var alice = new DirectLanListener(portA))
        await using (var bob = new DirectLanListener(portB))
        {
            var atAlice = new TaskCompletionSource<(string Ip, string Text)>(TaskCreationOptions.RunContinuationsAsynchronously);
            var atBob = new TaskCompletionSource<(string Ip, string Text)>(TaskCreationOptions.RunContinuationsAsynchronously);
            alice.MessageReceived += (ip, text) => atAlice.TrySetResult((ip, text));
            bob.MessageReceived += (ip, text) => atBob.TrySetResult((ip, text));
            alice.Start(); bob.Start();
            Check(alice.Running && bob.Running, "both real TCP listeners start");
            string first = "A → B：下課一起走 🐈\n第二行中文";
            string reply = "B → A：收到 🐾";
            Check(await DirectLanMessaging.SendDirectAsync("127.0.0.1", first, portB, timeout.Token), "Alice gets delivery receipt");
            var receivedB = await atBob.Task.WaitAsync(timeout.Token);
            Check(receivedB == ("127.0.0.1", first), "Bob receives exact Unicode, newline and TCP source IP");
            Check(await DirectLanMessaging.SendDirectAsync("127.0.0.1", reply, portA, timeout.Token), "Bob gets reply receipt");
            Check(await atAlice.Task.WaitAsync(timeout.Token) == ("127.0.0.1", reply), "Alice receives Bob's reply");

            await using var conflict = new DirectLanListener(portA);
            conflict.Start();
            Check(!conflict.Running && !string.IsNullOrWhiteSpace(conflict.Error), "occupied listener port reports failure");
        }
        bool offline = false;
        try { await DirectLanMessaging.SendDirectAsync("127.0.0.1", "offline", portA, timeout.Token); }
        catch (InvalidOperationException) { offline = true; }
        Check(offline, "closed listener cannot report delivery");

        foreach (string response in new[] { "{\"status\":\"pending\"}", "{}", "{\"status\":true}", "[]", "invalid-json" })
        {
            using var server = new TcpListener(IPAddress.Loopback, 0);
            server.Start();
            int port = ((IPEndPoint)server.LocalEndpoint).Port;
            var serve = ReplyOnceAsync(server, response, timeout.Token);
            bool rejected = false;
            try { await DirectLanMessaging.SendDirectAsync("127.0.0.1", "no receipt", port, timeout.Token); }
            catch (InvalidOperationException) { rejected = true; }
            await serve;
            Check(rejected, "HTTP 200 without delivered receipt must fail: " + response);
        }
        Console.WriteLine($"Direct messaging: {checks} real TCP behavior checks passed (same-host loopback).");
        return checks;
    }

    private static async Task ReplyOnceAsync(TcpListener server, string body, CancellationToken cancellation)
    {
        using var client = await server.AcceptTcpClientAsync(cancellation);
        await using var stream = client.GetStream();
        using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
        int length = 0;
        while (await reader.ReadLineAsync(cancellation) is { Length: > 0 } line)
            if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                length = int.Parse(line[15..].Trim());
        var buffer = new char[length];
        int read = 0;
        while (read < length)
        {
            int count = await reader.ReadAsync(buffer.AsMemory(read), cancellation);
            if (count == 0) throw new EndOfStreamException();
            read += count;
        }
        var bytes = Encoding.UTF8.GetBytes(body);
        var header = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(header, cancellation);
        await stream.WriteAsync(bytes, cancellation);
    }
}
