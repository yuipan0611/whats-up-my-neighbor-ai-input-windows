using System.Net;
using System.Net.Sockets;
using System.Text;
using NekoChat.Core;
using NekoChat.Core.Classroom.Transport;
using NekoChat.Core.V2;

namespace NekoChat.Tests;

/// <summary>Actual accepted sockets plus explicit per-accept fault injection; no classroom codec or identity claim.</summary>
internal static class ListenerAcceptRecoveryChecks
{
    internal static async Task<int> Run()
    {
        int checks = 0;
        void Check(bool ok, string label) { checks++; if (!ok) throw new Exception("Accept recovery: " + label); }
        int accepted = 0;
        void Fault(TcpClient client)
        {
            int n = Interlocked.Increment(ref accepted);
            if (n == 1) throw new SocketException((int)SocketError.ConnectionAborted);
            if (n == 2) client.Dispose(); // The product then reads the actual disposed socket's endpoint.
        }
        var endpoint = new Endpoint();
        await using (var server = new ClassroomTcpListener(endpoint, new() { BindAddress = IPAddress.Loopback, Port = 0, AcceptedForTest = Fault }))
        {
            server.Start();
            Check(await ClosedWithoutReply(server.Port), "classroom closes the first faulted accepted socket");
            Check(await ClosedWithoutReply(server.Port), "classroom accept continuation reaches the next disposed socket");
            Check(await Post(server.Port) == 200 && server.Running && server.AcceptFailures == 2,
                "classroom serves a fresh real request after both per-accept faults");
            Check(endpoint.Calls == 1 && endpoint.BecameUnavailable == 0,
                "transient accepts neither enter the handler nor clear healthy endpoint/session state");
            await server.DisposeAsync();
            Check(!server.Running && server.ActiveConnections == 0, "classroom stop drains accept and request workers after recovery");
        }
        accepted = 0;
        await using (var server = new V2TcpListener(null, new() { BindAddress = IPAddress.Loopback, Port = 0, AcceptedForTest = Fault }))
        {
            int delivered = 0; server.LegacyMessageReceived += (_, _) => Interlocked.Increment(ref delivered); server.Start();
            Check(await ClosedWithoutReply(server.Port), "shared v1/v2 closes the first faulted accepted socket");
            Check(await ClosedWithoutReply(server.Port), "shared v1/v2 accept continuation reaches the next disposed socket");
            bool sent;
            try { sent = await DirectLanMessaging.SendDirectAsync("127.0.0.1", "accept recovery", server.Port); }
            catch (Exception e) when (e is HttpRequestException or SocketException or InvalidOperationException or OperationCanceledException) { sent = false; }
            Check(sent && server.Running && server.AcceptFailures == 2 && delivered == 1,
                "ordinary shared legacy transport still delivers after both per-accept faults");
            await server.DisposeAsync();
            Check(!server.Running && server.ActiveConnections == 0, "shared listener drains before disposal after recovery");
        }
        Console.WriteLine($"Accept recovery: {checks} real TCP classroom/shared continuation, endpoint availability and shutdown checks; two injected faults per listener.");
        return checks;
    }
    private static async Task<bool> ClosedWithoutReply(int port)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)); using var client = new TcpClient();
        try
        {
            await client.ConnectAsync(IPAddress.Loopback, port, timeout.Token);
            return await client.GetStream().ReadAsync(new byte[1], timeout.Token) == 0;
        }
        catch (Exception error) when (error is IOException or SocketException) { return true; }
        catch (OperationCanceledException) { return false; }
    }
    private static async Task<int> Post(int port)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)); using var client = new TcpClient();
        try
        {
            await client.ConnectAsync(IPAddress.Loopback, port, timeout.Token);
            await client.GetStream().WriteAsync("POST /nekochat/classroom/v1/hello HTTP/1.1\r\nHost: localhost\r\nContent-Length: 1\r\nContent-Type: application/json\r\n\r\nx"u8.ToArray(), timeout.Token);
            using var reader = new StreamReader(client.GetStream(), Encoding.ASCII, leaveOpen: true);
            return int.TryParse((await reader.ReadLineAsync(timeout.Token) ?? "").Split(' ').ElementAtOrDefault(1), out int status) ? status : 0;
        }
        catch (Exception error) when (error is IOException or SocketException or OperationCanceledException) { return 0; }
    }
    private sealed class Endpoint : IClassroomRequestEndpoint
    {
        internal int Calls, BecameUnavailable;
        public void SetReady(bool ready) { if (!ready) Interlocked.Increment(ref BecameUnavailable); }
        public ClassroomHeadDecision ValidateHead(ReadOnlyMemory<byte> header) => new("ok", new("hello", 1, 8192));
        public ClassroomTransportResponse Error(string code) => new(503, "{}"u8.ToArray());
        public Task<ClassroomTransportResponse> HandleAsync(ClassroomRequestFrame frame, ClassroomConnectionContext connection, Func<bool> current, CancellationToken token)
        { Interlocked.Increment(ref Calls); return Task.FromResult(new ClassroomTransportResponse(200, "{}"u8.ToArray())); }
    }
}
