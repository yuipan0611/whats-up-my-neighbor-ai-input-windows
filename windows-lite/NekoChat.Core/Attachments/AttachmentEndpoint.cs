using System.Text;
using NekoChat.Core.Classroom.Transport;
using NekoChat.Core.Classroom.Wire;

namespace NekoChat.Core.Attachments;

/// <summary>Additive wrapper: the frozen text codec still receives every original request unchanged.</summary>
public sealed class AttachmentEndpoint(ClassroomEndpointAdapter text, AttachmentService service) : IClassroomRequestEndpoint
{
    public const string Prefix = "/nekochat/attachments/v1/";
    public static string? Operation(ReadOnlySpan<byte> header)
    {
        int end = header.IndexOf("\r\n"u8); if (end < 0) return null;
        string line = Encoding.ASCII.GetString(header[..end]);
        foreach (var name in new[] { "offer", "chunk", "complete" })
            if (line == "POST " + Prefix + name + " HTTP/1.1") return name;
        return null;
    }
    public void SetReady(bool ready) { text.SetReady(ready); service.SetReady(ready); }
    public ClassroomHeadDecision ValidateHead(ReadOnlyMemory<byte> header)
    {
        string? operation = Operation(header.Span);
        if (operation is null) return text.ValidateHead(header);
        foreach (byte b in header.Span)
            if (b is not (9 or 10 or 13) && (b < 32 || b > 126)) return new("invalid_payload");
        // Reuse strict header syntax, duplicate-header and allocation bounds without editing the frozen codec.
        string original = Encoding.ASCII.GetString(header.Span);
        string rewritten = original.Replace(Prefix + operation, operation == "offer" ? "/nekochat/classroom/v1/message" : "/nekochat/classroom/v1/receipt", StringComparison.Ordinal);
        var check = ClassroomHead.Check(rewritten);
        return check.Ok ? new("ok", new("attachment." + operation, check.Length, operation == "offer" ? ClassroomWire.MessageBodyMaxBytes : ClassroomWire.ControlBodyMaxBytes)) : new(check.Code);
    }
    public ClassroomTransportResponse Error(string code) => text.Error(code);
    public Task<ClassroomTransportResponse> HandleAsync(ClassroomRequestFrame frame, ClassroomConnectionContext connection, Func<bool> current, CancellationToken cancellation) =>
        frame.Route.StartsWith("attachment.", StringComparison.Ordinal)
            ? service.HandleAsync(frame.Route[11..], frame.Body, connection.SourceIp, text.SessionTable, current, cancellation)
            : text.HandleAsync(frame, connection, current, cancellation);
}
