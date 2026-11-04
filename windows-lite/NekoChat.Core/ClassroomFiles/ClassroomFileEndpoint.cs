using System.Text;
using NekoChat.Core.Classroom.Transport;
using NekoChat.Core.Classroom.Wire;
namespace NekoChat.Core.ClassroomFiles;

public sealed class ClassroomFileEndpoint(IClassroomRequestEndpoint next,ClassroomFileService service):IClassroomRequestEndpoint
{
    public static string? Operation(ReadOnlySpan<byte> header)
    {
        int end=header.IndexOf("\r\n"u8);if(end<0)return null;string line=Encoding.ASCII.GetString(header[..end]);
        foreach(string name in new[]{"sync","push","status","chunk"})if(line=="POST "+ShareProtocol.Prefix+name+" HTTP/1.1")return name;return null;
    }
    public void SetReady(bool ready){next.SetReady(ready);service.SetReady(ready);}
    public ClassroomHeadDecision ValidateHead(ReadOnlyMemory<byte> header)
    {
        string? op=Operation(header.Span);if(op is null)return next.ValidateHead(header);
        foreach(byte b in header.Span)if(b is not(9 or 10 or 13)&&(b<32||b>126))return new("invalid_payload");
        var check=ClassroomHead.Check(Encoding.ASCII.GetString(header.Span).Replace(ShareProtocol.Prefix+op,"/nekochat/classroom/v1/receipt",StringComparison.Ordinal));
        return check.Ok?new("ok",new("classroom-files."+op,check.Length,ClassroomWire.ControlBodyMaxBytes)):new(check.Code);
    }
    public ClassroomTransportResponse Error(string code)=>next.Error(code);
    public Task<ClassroomTransportResponse> HandleAsync(ClassroomRequestFrame frame,ClassroomConnectionContext context,Func<bool> current,CancellationToken cancellation)=>frame.Route.StartsWith("classroom-files.",StringComparison.Ordinal)?service.HandleAsync(frame.Route[16..],frame.Body,context.SourceIp,current,cancellation):next.HandleAsync(frame,context,current,cancellation);
}
