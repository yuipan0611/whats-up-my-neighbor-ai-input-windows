using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NekoChat.Core;

/// <summary>
/// Opt-in LAN pairing by private IPv4. Knowing the IP can only request a connection;
/// the desktop host must approve before any WebSocket credential is issued.
/// </summary>
public static class ClassroomDirect
{
    public const int Port = 48361;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
        RespectNullableAnnotations = true,
        MaxDepth = 4
    };

    public static async Task<(Uri Endpoint, ClassroomSession Session)> JoinAsync(
        HttpClient http, string hostIP, CancellationToken cancellation)
    {
        if (!LanInvitation.PrivateIPv4(hostIP) || hostIP.StartsWith("127.", StringComparison.Ordinal))
            throw new InvalidDataException("請輸入房主的完整教室內網 IP，例如 192.168.24.116。");

        var baseUrl = $"http://{hostIP}:{Port}";
        string roomId;
        using (var healthResponse = await http.GetAsync(
            baseUrl + "/healthz", HttpCompletionOption.ResponseHeadersRead, cancellation))
        {
            if (healthResponse.StatusCode != HttpStatusCode.OK)
                throw new InvalidDataException("該 IP 沒有啟用相容的 NekoChat LAN 直接連線。");
            using var health = await ReadBoundedJsonAsync(healthResponse, cancellation);
            try
            {
                var root = health.RootElement;
                roomId = root.GetProperty("room_id").GetString() ?? "";
                if (root.GetProperty("protocol").GetString() != "nekochat" ||
                    root.GetProperty("version").GetInt32() != 1 ||
                    root.GetProperty("room_mode").GetString() != "classroom" ||
                    root.GetProperty("ip_pairing_version").GetInt32() != 1 ||
                    root.GetProperty("host_ip").GetString() != hostIP ||
                    roomId.Length != 32 || roomId.Any(c => !char.IsAsciiHexDigitLower(c)))
                    throw new InvalidDataException("該 IP 的房間版本或位址不相容。");
            }
            catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException or FormatException)
            {
                throw new InvalidDataException("目標電腦不是相容的 NekoChat 房主。");
            }
        }

        string requestId;
        using (var start = new HttpRequestMessage(HttpMethod.Post, baseUrl + "/nekochat/v1/classroom/request"))
        using (var response = await http.SendAsync(start, HttpCompletionOption.ResponseHeadersRead, cancellation))
        {
            if (!response.IsSuccessStatusCode)
                throw new InvalidDataException(response.StatusCode switch
                {
                    HttpStatusCode.Conflict => "這個 IP 已經在房間內，或有尚未處理的連線請求。",
                    HttpStatusCode.TooManyRequests => "連線請求過於頻繁，請稍後再試。",
                    HttpStatusCode.ServiceUnavailable => "對方目前的待處理請求已滿。",
                    _ => "房主無法接收 IP 連線請求，請確認對方已開啟 LAN 直接聊天。"
                });
            using var json = await ReadBoundedJsonAsync(response, cancellation);
            try
            {
                var root = json.RootElement;
                if (root.GetProperty("protocol").GetString() != "nekochat" ||
                    root.GetProperty("version").GetInt32() != 1 ||
                    root.GetProperty("room_id").GetString() != roomId ||
                    root.GetProperty("state").GetString() != "pending")
                    throw new InvalidDataException("LAN 請求回應不正確。");
                requestId = root.GetProperty("request_id").GetString() ?? "";
                if (requestId.Length != 32 || requestId.Any(c =>
                    !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_')))
                    throw new InvalidDataException("LAN 請求 ID 不正確。");
            }
            catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException or FormatException)
            {
                throw new InvalidDataException("LAN 請求回應不正確。");
            }
        }

        var statusUrl = baseUrl + "/nekochat/v1/classroom/request/" + requestId;
        while (true)
        {
            cancellation.ThrowIfCancellationRequested();
            await Task.Delay(TimeSpan.FromMilliseconds(1200), cancellation);
            using var response = await http.GetAsync(statusUrl, HttpCompletionOption.ResponseHeadersRead, cancellation);
            if (response.StatusCode == HttpStatusCode.Accepted)
                continue;
            if (response.StatusCode == HttpStatusCode.Forbidden)
                throw new InvalidDataException("對方拒絕了連線請求，請先向同學確認。");
            if (response.StatusCode == HttpStatusCode.Gone)
                throw new InvalidDataException("連線請求已過期，請重新發起。");
            if (response.StatusCode != HttpStatusCode.OK)
                throw new InvalidDataException("房主沒有完成連線核准，請重新連線。");
            using var json = await ReadBoundedJsonAsync(response, cancellation);
            try
            {
                var fields = json.RootElement.EnumerateObject().Select(x => x.Name).ToArray();
                if (fields.Length != fields.Distinct(StringComparer.Ordinal).Count())
                    throw new JsonException("Duplicate fields");
                var session = JsonSerializer.Deserialize<ClassroomSession>(json.RootElement.GetRawText(), JsonOptions)
                    ?? throw new JsonException();
                if (session.Protocol != "nekochat" || session.Version != 1 || session.RoomId != roomId ||
                    session.TeacherIP != hostIP || session.User != ClassroomInvitation.UserForIP(session.Ip) ||
                    session.Token is null || session.Token.Length != 43 ||
                    session.Token.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_')))
                    throw new InvalidDataException("對方回傳的個人連線憑證不正確。");
                var endpoint = RelayConnection.ValidateEndpoint(
                    $"ws://{hostIP}:{Port}/nekochat/v1/ws", true);
                return (endpoint, session);
            }
            catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
            {
                throw new InvalidDataException("LAN 連線回應不正確。");
            }
        }
    }

    private static async Task<JsonDocument> ReadBoundedJsonAsync(
        HttpResponseMessage response, CancellationToken cancellation)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellation);
        var bytes = new byte[2049];
        var count = 0;
        while (count < bytes.Length)
        {
            var n = await stream.ReadAsync(bytes.AsMemory(count), cancellation);
            if (n == 0) break;
            count += n;
        }
        if (count > 2048)
            throw new InvalidDataException("LAN 回應內容超過大小上限。");
        try { return JsonDocument.Parse(bytes.AsMemory(0, count), new JsonDocumentOptions { MaxDepth = 4 }); }
        catch (JsonException) { throw new InvalidDataException("LAN 回應不是有效的 JSON。"); }
    }
}
