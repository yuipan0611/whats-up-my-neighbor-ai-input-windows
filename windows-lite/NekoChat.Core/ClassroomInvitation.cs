using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NekoChat.Core;

/// <summary>Admission capability for a whole classroom, never a member/host credential.</summary>
public sealed record ClassroomInvitation(int Version,string RoomId,string[] Hosts,int Port,string Token)
{
    private const string Prefix="NEKOCLASS1-";
    private static readonly JsonSerializerOptions JsonOptions=new(){
        PropertyNamingPolicy=JsonNamingPolicy.CamelCase,UnmappedMemberHandling=JsonUnmappedMemberHandling.Disallow,
        RespectNullableAnnotations=true,RespectRequiredConstructorParameters=true,MaxDepth=4
    };
    public override string ToString()=>"NekoChat private classroom invitation";
    public static string UserForIP(string ip)
    {
        if(!LanInvitation.PrivateIPv4(ip))throw new InvalidDataException("請輸入同學完整的內網 IPv4，例如 192.168.1.23。");
        return "ip-"+ip.Replace('.','-');
    }
    public static string IPForUser(string user)
    {
        if(!user.StartsWith("ip-",StringComparison.Ordinal))throw new InvalidDataException("教室成員位址不正確。");
        var ip=user[3..].Replace('-','.');
        if(UserForIP(ip)!=user)throw new InvalidDataException("教室成員位址不正確。");
        return ip;
    }
    public void Validate()
    {
        if(Version!=1 || RoomId is null || RoomId.Length!=32 || RoomId.Any(c=>!char.IsAsciiHexDigitLower(c)) ||
            Hosts is null || Hosts.Length is <1 or >8 || Hosts.Any(h=>h is null || !LanInvitation.PrivateIPv4(h)) ||
            Hosts.Distinct(StringComparer.Ordinal).Count()!=Hosts.Length || Port is <1 or >65535 ||
            Token is null || Token.Length!=43 || Token.Any(c=>!char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_')))
            throw new InvalidDataException("教室邀請碼格式不正確，請重新複製完整內容。");
    }
    public string Encode()
    {
        Validate();return Prefix+Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(this,JsonOptions)).TrimEnd('=').Replace('+','-').Replace('/','_');
    }
    public static ClassroomInvitation Parse(string code)
    {
        code=code.Trim();
        if(code.Length>4096 || !code.StartsWith(Prefix,StringComparison.Ordinal))throw new InvalidDataException("請貼上新版教室房間邀請碼；全班使用同一份邀請。");
        try {
            var encoded=code[Prefix.Length..].Replace('-','+').Replace('_','/');
            var bytes=Convert.FromBase64String(encoded.PadRight((encoded.Length+3)/4*4,'='));
            var invite=StrictRead<ClassroomInvitation>(bytes);invite.Validate();return invite;
        }catch(Exception ex)when(ex is JsonException or FormatException or InvalidOperationException){throw new InvalidDataException("教室邀請碼無法讀取，請重新複製完整內容。");}
    }
    private static T StrictRead<T>(ReadOnlyMemory<byte> bytes)
    {
        using var doc=JsonDocument.Parse(bytes,new JsonDocumentOptions{MaxDepth=4});
        var names=doc.RootElement.EnumerateObject().Select(p=>p.Name).ToArray();
        if(names.Length!=names.Distinct(StringComparer.Ordinal).Count())throw new JsonException();
        return JsonSerializer.Deserialize<T>(bytes.Span,JsonOptions)??throw new JsonException();
    }
    private static async Task<byte[]> ReadBoundedAsync(HttpResponseMessage response,CancellationToken cancellation)
    {
        await using var stream=await response.Content.ReadAsStreamAsync(cancellation);
        var bytes=new byte[2049];int count=0;
        while(count<bytes.Length){int n=await stream.ReadAsync(bytes.AsMemory(count),cancellation);if(n==0)break;count+=n;}
        if(count>2048)throw new InvalidDataException("房間回應過大。");
        return bytes[..count];
    }
    public async Task<Uri> FindEndpointAsync(HttpClient http,CancellationToken cancellation)
    {
        // Reuse bounded, credential-free v1 health discovery, then require classroom mode before admission.
        Validate();
        var probe=new LanInvitation(1,RoomId,Hosts,Port,"probe-a","probe-b",Token);
        var endpoint=await probe.FindEndpointAsync(http,cancellation);
        using var request=new HttpRequestMessage(HttpMethod.Get,$"http://{endpoint.Host}:{Port}/healthz");
        using var response=await http.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,cancellation);
        using var health=JsonDocument.Parse(await ReadBoundedAsync(response,cancellation));
        var root=health.RootElement;
        if(!response.IsSuccessStatusCode || root.GetProperty("room_id").GetString()!=RoomId ||
            root.GetProperty("room_mode").GetString()!="classroom" || root.GetProperty("admission_version").GetInt32()!=1)
            throw new InvalidDataException("這不是相容的教室房間，請使用相同新版 App。");
        return endpoint;
    }
    public async Task<ClassroomSession> JoinAsync(HttpClient http,Uri endpoint,CancellationToken cancellation)
    {
        Validate();RelayConnection.ValidateEndpoint(endpoint.AbsoluteUri,true);
        if(endpoint.Scheme!="ws" || !Hosts.Contains(endpoint.Host,StringComparer.Ordinal) || endpoint.Port!=Port)
            throw new InvalidDataException("房間位址不符合邀請碼。");
        using var request=new HttpRequestMessage(HttpMethod.Post,$"http://{endpoint.Host}:{Port}/nekochat/v1/classroom/join");
        request.Headers.Authorization=new AuthenticationHeaderValue("Bearer",Token);
        request.Headers.Add("X-NekoChat-Room",RoomId);
        using var response=await http.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,cancellation);
        if(!response.IsSuccessStatusCode)throw new InvalidDataException(response.StatusCode switch {
            HttpStatusCode.Conflict=>"這台 IP 已在房間中。請先離開舊 App；中斷後最多等待 90 秒再加入。",
            HttpStatusCode.TooManyRequests=>"加入過於頻繁，請稍後再試。",
            HttpStatusCode.ServiceUnavailable=>"教室房間已滿（最多 100 台）。",
            _=>"加入教室失敗，請確認房主仍開著 App，並重新取得邀請碼。"
        });
        try {
            var session=StrictRead<ClassroomSession>(await ReadBoundedAsync(response,cancellation));
            if(session.Protocol!="nekochat" || session.Version!=1 || session.RoomId!=RoomId || session.Ip is null ||
                session.TeacherIP is null || session.TeacherIP!=Hosts[0] || session.User!=UserForIP(session.Ip) || session.Token is null || session.Token.Length!=43 ||
                session.Token.Any(c=>!char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_')))
                throw new InvalidDataException("教室加入回應不正確。");
            return session;
        }catch(JsonException){throw new InvalidDataException("教室加入回應不正確。");}
    }
}

public sealed record ClassroomSession(string Protocol,int Version,[property:JsonPropertyName("room_id")]string RoomId,string User,string Ip,[property:JsonPropertyName("teacher_ip")]string TeacherIP,string Token)
{
    public override string ToString()=>"NekoChat ephemeral classroom session";
}
