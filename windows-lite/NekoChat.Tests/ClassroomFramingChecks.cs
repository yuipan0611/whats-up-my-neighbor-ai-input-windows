using System.Net;
using System.Net.Sockets;
using System.Text;
using NekoChat.Core.Classroom.Transport;

namespace NekoChat.Tests;

/// <summary>Actual TCP stream boundaries. The injected codec decision isolates framing; it does not claim JSON/header/session validation.</summary>
internal static class ClassroomFramingChecks
{
    internal static async Task<int> Run()
    {
        int checks = 0;
        void Check(bool ok, string label) { checks++; if (!ok) throw new Exception("Classroom framing: " + label); }
        byte[] Header(int size = 160)
        {
            const string prefix = "POST /nekochat/classroom/v1/message HTTP/1.1\r\nX-Padding: ";
            return Encoding.ASCII.GetBytes(prefix + new string('a', size - prefix.Length - 4) + "\r\n\r\n");
        }
        ClassroomHeadDecision Decision(int length, int limit) => new("ok", new("message", length, limit));
        async Task<ClassroomFrameReadResult> TcpRead(byte[] header, byte[] body, int declared, int limit, bool halfClose = false)
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var accepted = listener.AcceptTcpClientAsync(timeout.Token).AsTask();
            using var sender = new TcpClient(); await sender.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port, timeout.Token);
            using var receiver = await accepted;
            var read = ClassroomStreamFraming.ReadRequestAsync(receiver.GetStream(), bytes =>
            {
                Check(bytes.Span.SequenceEqual(header), "codec gets only the complete head, never coalesced body bytes");
                return Decision(declared, limit);
            }, timeout.Token);
            var stream = sender.GetStream();
            // Multiple writes cover delimiters crossing writes; the reader cannot assume packet boundaries.
            await stream.WriteAsync(header.AsMemory(0, header.Length - 2), timeout.Token);
            await stream.WriteAsync(header.AsMemory(header.Length - 2), timeout.Token);
            if (body.Length > 0) await stream.WriteAsync(body, timeout.Token);
            if (halfClose) sender.Client.Shutdown(SocketShutdown.Send);
            return await read.WaitAsync(timeout.Token);
        }
        byte[] text = Encoding.UTF8.GetBytes(new string('a', 65536 - 4) + "🐈");
        var normal = await TcpRead(Header(), text, text.Length, ClassroomStreamFraming.MaximumMessageBodyBytes);
        Check(normal.Success && normal.Frame!.Body.Span.SequenceEqual(text), "64 KiB UTF8 body survives actual TCP unchanged");
        var exactHead = await TcpRead(Header(8192), [42], 1, 8192);
        Check(exactHead.Success && exactHead.Frame!.Body.Span.SequenceEqual(new byte[] { 42 }), "8192-byte head is accepted without consuming the body as header");
        var oversized = await TcpRead(Header(), [], ClassroomStreamFraming.MaximumMessageBodyBytes + 1, ClassroomStreamFraming.MaximumMessageBodyBytes);
        Check(oversized.Code == "payload_too_large", "oversized declared body is refused while peer keeps connection open without sending body");
        var control = await TcpRead(Header(), [], 8193, 8192);
        Check(control.Code == "payload_too_large", "control-body bound is enforced before waiting for absent bytes");
        var partial = await TcpRead(Header(), [1, 2], 3, 8192, halfClose: true);
        Check(partial.Code == "invalid_payload", "actual TCP EOF in the body is refused, not accepted as truncated text");
        int codecCalls = 0;
        var longHead = await ClassroomStreamFraming.ReadRequestAsync(new MemoryStream(Header(8193)), _ => { codecCalls++; return Decision(1, 8192); });
        Check(longHead.Code == "invalid_payload" && codecCalls == 0, "8193-byte head never reaches codec or body allocation");
        byte[] coalesced = Header().Concat(new byte[] { 1, 2 }).ToArray();
        var extra = await ClassroomStreamFraming.ReadRequestAsync(new MemoryStream(coalesced), _ => Decision(1, 8192));
        Check(extra.Code == "invalid_payload", "already-buffered bytes beyond declared body are refused");
        var rejected = await ClassroomStreamFraming.ReadRequestAsync(new MemoryStream(Header()), _ => new("unsupported_version"));
        Check(rejected.Code == "unsupported_version", "codec refusal propagates without waiting for a body");
        // A real peer that sends a head but stalls mid-body must yield to the connection owner's deadline.
        using (var listener = new TcpListener(IPAddress.Loopback, 0))
        {
            listener.Start(); using var sender = new TcpClient();
            var accepted = listener.AcceptTcpClientAsync();
            await sender.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
            using var receiver = await accepted;
            using var deadline = new CancellationTokenSource();
            var headParsed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var read = ClassroomStreamFraming.ReadRequestAsync(receiver.GetStream(), _ => { headParsed.TrySetResult(); return Decision(2, 8192); }, deadline.Token);
            await sender.GetStream().WriteAsync(Header());
            await headParsed.Task.WaitAsync(TimeSpan.FromSeconds(10));
            deadline.CancelAfter(TimeSpan.FromMilliseconds(50));
            bool canceled = false; try { await read.WaitAsync(TimeSpan.FromSeconds(10)); } catch (OperationCanceledException) { canceled = true; }
            Check(canceled, "actual stalled TCP body is interrupted by the owner's deadline");
        }
        Console.WriteLine($"Classroom framing: {checks} stream allocation, TCP truncation, head/body boundary and deadline checks; codec/storage/session acceptance remains separate.");
        return checks;
    }
}
