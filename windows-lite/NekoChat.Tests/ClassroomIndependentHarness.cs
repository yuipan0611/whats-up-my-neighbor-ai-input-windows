using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using NekoChat.Core.Classroom.Transport;
using NekoChat.Core.Classroom.Wire;
using NekoChat.Core.Storage;

namespace NekoChat.Tests;

/// <summary>Shared pieces of the independent classroom checks (contract NCMW-v2). Public API of the product only; real loopback TCP peers.</summary>
internal sealed record WireReq(int Seq, string Path, byte[] Body, IPAddress? Remote)
{
    public bool IsHello => Path.EndsWith("/nekochat/classroom/v1/hello", StringComparison.Ordinal);
    public bool IsClassroomMessage => Path.EndsWith("/nekochat/classroom/v1/message", StringComparison.Ordinal);
    public bool IsLegacyDirect => Path.EndsWith("/nekochat/v1/direct", StringComparison.Ordinal);
    /// <summary>The request body is JSON with a non-empty "text" string: the user's message reached the wire.</summary>
    public bool CarriesUserText
    {
        get
        {
            if (Body.Length == 0) return false;
            try
            {
                using var doc = JsonDocument.Parse(Body);
                return doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("text", out var t) &&
                    t.ValueKind == JsonValueKind.String && t.GetString()!.Length > 0;
            }
            catch (JsonException) { return false; }
        }
    }
}

internal abstract record WireReply;
internal sealed record WireRaw(int Status, string ContentType, byte[] Body) : WireReply;
/// <summary>Read the whole request, then never answer (the client times out or cancels).</summary>
internal sealed record WireSilence : WireReply;
/// <summary>Read the whole request, then close the connection without a response.</summary>
internal sealed record WireHangup : WireReply;

internal sealed class LoopbackPeer : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly Func<WireReq, WireReply> _script;
    private readonly List<WireReq> _seen = new();
    private int _seq;
    internal int Port { get; }
    internal IReadOnlyList<WireReq> Seen { get { lock (_seen) return _seen.ToArray(); } }

    internal LoopbackPeer(Func<WireReq, WireReply> script)
    {
        _script = script; _listener.Start(); Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _ = Task.Run(AcceptLoop);
    }

    private async Task AcceptLoop()
    {
        while (!_stop.IsCancellationRequested)
        {
            try { var client = await _listener.AcceptTcpClientAsync(_stop.Token); _ = Task.Run(() => Serve(client)); }
            catch (Exception e) when (e is OperationCanceledException or SocketException or ObjectDisposedException) { return; }
        }
    }

    private async Task Serve(TcpClient client)
    {
        using (client)
        {
            try
            {
                var remote = (client.Client.RemoteEndPoint as IPEndPoint)?.Address;
                var stream = client.GetStream();
                var buffer = new List<byte>(); var chunk = new byte[8192]; int headEnd = -1;
                while (headEnd < 0)
                {
                    int n = await stream.ReadAsync(chunk, _stop.Token); if (n == 0) return;
                    buffer.AddRange(chunk.AsSpan(0, n).ToArray());
                    headEnd = IndexOf(buffer, "\r\n\r\n"u8);
                }
                string head = Encoding.ASCII.GetString(buffer.GetRange(0, headEnd).ToArray());
                string[] lines = head.Split("\r\n");
                string path = lines[0].Split(' ')[1];
                int length = 0;
                foreach (string line in lines.Skip(1))
                    if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)) length = int.Parse(line[15..].Trim());
                while (buffer.Count < headEnd + 4 + length)
                {
                    int n = await stream.ReadAsync(chunk, _stop.Token); if (n == 0) break;
                    buffer.AddRange(chunk.AsSpan(0, n).ToArray());
                }
                var body = buffer.Skip(headEnd + 4).Take(length).ToArray();
                WireReq request; lock (_seen) { request = new WireReq(++_seq, path, body, remote); _seen.Add(request); }
                switch (_script(request))
                {
                    case WireRaw raw:
                        string reason = raw.Status switch { 200 => "OK", 400 => "Bad Request", 403 => "Forbidden", 404 => "Not Found", 409 => "Conflict", 413 => "Content Too Large", 429 => "Too Many Requests", 503 => "Service Unavailable", _ => "Status" };
                        await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 {raw.Status} {reason}\r\nContent-Type: {raw.ContentType}\r\nContent-Length: {raw.Body.Length}\r\nConnection: close\r\n\r\n"), _stop.Token);
                        await stream.WriteAsync(raw.Body, _stop.Token); await stream.FlushAsync(_stop.Token);
                        break;
                    case WireSilence:
                        await Task.Delay(Timeout.Infinite, _stop.Token);
                        break;
                    case WireHangup:
                        break; // dispose closes the socket without a response
                }
            }
            catch (Exception e) when (e is OperationCanceledException or IOException or SocketException or ObjectDisposedException) { }
        }
    }

    private static int IndexOf(List<byte> data, ReadOnlySpan<byte> pattern)
    {
        for (int i = 0; i + pattern.Length <= data.Count; i++)
        {
            bool hit = true; for (int j = 0; j < pattern.Length && hit; j++) hit = data[i + j] == pattern[j];
            if (hit) return i;
        }
        return -1;
    }

    public ValueTask DisposeAsync() { _stop.Cancel(); _listener.Stop(); _stop.Dispose(); return ValueTask.CompletedTask; }
}

internal static class Loopback
{
    internal static WireReply Json(int status, byte[] body) => new WireRaw(status, "application/json; charset=utf-8", body);
    internal static WireReply ClassroomError(string code) { var (status, body) = ClassroomPacketWriter.Error(code); return Json(status, body); }
    internal static byte[] Ascii(string s) => Encoding.ASCII.GetBytes(s);

    /// <summary>Every connection goes to the loopback peer (or is refused), whatever IP the sender targets.</summary>
    internal static HttpClient Client(int? peerPort, int timeoutMs = 1500) => new(ClassroomSender.CreateDefaultHandler(async (_, cancellation) =>
    {
        // Only the way the connection is made changed: the product's own handler wraps this callback and records the real LocalEndPoint in the request Options.
        if (peerPort is not { } port) throw new SocketException((int)SocketError.ConnectionRefused);
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try { await socket.ConnectAsync(new IPEndPoint(IPAddress.Loopback, port), cancellation); return new NetworkStream(socket, ownsSocket: true); }
        catch { socket.Dispose(); throw; }
    })) { Timeout = TimeSpan.FromMilliseconds(timeoutMs) };

    /// <summary>The store as the app configures it: 65,536 UTF-8 bytes of text (D-06). The store default would refuse more than 2,000 scalars.</summary>
    internal static ChatStore OpenStore(string dir) => ChatStore.Open(new(Path.Combine(dir, "data"))
    {
        TextValidator = text => NekoChat.Core.V2.V2Text.Check(text, NekoChat.Core.V2.V2Text.ProductMaxUtf8Bytes) == NekoChat.Core.V2.V2TextProblem.None
    });

    internal static string TempDir(string prefix) { string dir = Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir); return dir; }
    internal static void Cleanup(string dir) { try { Directory.Delete(dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
}

/// <summary>Records every call made through IChatStore and forwards it to a real store (public interface only).</summary>
public class RecordingStore : DispatchProxy
{
    private IChatStore _inner = null!;
    private readonly List<(string Method, object?[] Args)> _calls = new();
    public static (IChatStore Proxy, RecordingStore Recorder) Wrap(IChatStore inner)
    {
        var proxy = Create<IChatStore, RecordingStore>(); var recorder = (RecordingStore)(object)proxy; recorder._inner = inner; return (proxy, recorder);
    }
    public IReadOnlyList<(string Method, object?[] Args)> Calls { get { lock (_calls) return _calls.ToArray(); } }
    /// <summary>Called with the member name right after the real store call returned (used to cancel at an exact point). Never throws into the product.</summary>
    public Action<string>? AfterCall { get; set; }
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        lock (_calls) _calls.Add((targetMethod!.Name, args ?? Array.Empty<object?>()));
        try { var result = targetMethod.Invoke(_inner, args); AfterCall?.Invoke(targetMethod.Name); return result; }
        catch (TargetInvocationException e) when (e.InnerException is not null) { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(e.InnerException).Throw(); throw; }
    }
}

/// <summary>An IChatStore whose every member throws the configured exception (a store that is down).</summary>
public class FailingStore : DispatchProxy
{
    private Func<Exception> _failure = () => new IOException("disk gone");
    public static IChatStore Down(Func<Exception> failure) { var proxy = Create<IChatStore, FailingStore>(); ((FailingStore)(object)proxy)._failure = failure; return proxy; }
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => throw _failure();
}
