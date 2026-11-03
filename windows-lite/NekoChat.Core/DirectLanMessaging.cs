using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace NekoChat.Core;

public static class DirectLanMessaging
{
    public const int DefaultPort = 8768;

    public static async Task<bool> SendDirectAsync(string targetIp, string text, int port = DefaultPort, CancellationToken cancellation = default, string? reaction = null)
    {
        if (!LanInvitation.PrivateIPv4(targetIp))
            throw new ArgumentException("請輸入有效的內網 IPv4 位址（例如 192.168.1.25）。");
        if (!Protocol.ValidText(text))
            throw new ArgumentException("訊息格式不正確（最多 2000 字且不可為空白）。");
        if (reaction is not null && ClassroomReactions.Find(reaction) is null)
            throw new ArgumentException("未知的反應貼圖。");

        using var handler = new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };

        var payload = JsonSerializer.Serialize(new {
            protocol = "nekochat",
            version = 1,
            kind = "direct.message",
            text,
            reaction
        });

        using var content = new StringContent(payload, Encoding.UTF8, "application/json");
        try
        {
            using var response = await client.PostAsync($"http://{targetIp}:{port}/nekochat/v1/direct", content, cancellation);
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException($"對方回應 HTTP {(int)response.StatusCode}");
            var body = await response.Content.ReadAsStringAsync(cancellation);
            try
            {
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.ValueKind != JsonValueKind.Object ||
                    !doc.RootElement.TryGetProperty("status", out var s) ||
                    s.ValueKind != JsonValueKind.String || s.GetString() != "delivered")
                    throw new InvalidOperationException("對方沒有確認訊息送達，請先向對方確認後再重送。");
            }
            catch (JsonException)
            {
                throw new InvalidOperationException("對方的送達回覆無法讀取，請先向對方確認後再重送。");
            }
            return true;
        }
        catch (Exception ex) when (ex is HttpRequestException or SocketException or TaskCanceledException)
        {
            throw new InvalidOperationException($"未能確認送達 {targetIp}（TCP {port}）。請確認 IP 正確、對方顯示「接收已啟動」；若仍失敗，請教室管理者檢查 Windows 網路權限及 Wi-Fi／AP 隔離。重送前請先向對方確認。");
        }
    }
}

/// <summary>Compatibility facade: v1's two events are retained; verified v2 storage notices have a separate event.</summary>
public sealed class DirectLanListener : IAsyncDisposable
{
    private readonly V2.V2TcpListener _transport;
    public int Port => _transport.Port;
    public bool Running => _transport.Running;
    public string? Error { get; private set; }
    public event Action<string, string>? MessageReceived;
    public event Action<string, DirectMessage>? DirectMessageReceived;
    public event Action<V2.V2DeliveryNotice>? V2MessageStored;
    public event Action<LegacyDeliveryNotice>? LegacyMessageStored;
    public event Action<string>? Diagnostic;

    public DirectLanListener(int port = DirectLanMessaging.DefaultPort) : this(null, new V2.V2ListenerOptions { Port = port }) { }
    public DirectLanListener(V2.V2Receiver receiver, int port = DirectLanMessaging.DefaultPort) : this(receiver, new V2.V2ListenerOptions { Port = port }) { }
    public DirectLanListener(V2.V2Receiver? receiver, V2.V2ListenerOptions options, Storage.IChatStore? legacyStore = null) : this(receiver, options, legacyStore, null) { }
    public DirectLanListener(V2.V2Receiver? receiver, V2.V2ListenerOptions options, Storage.IChatStore? legacyStore, Pairing.IPairingEndpoint? pairing)
    {
        _transport = new(receiver, options, pairing) {
            LegacyMessageHandler = legacyStore is null ? null : (source, message, live) => {
                var captured = legacyStore.GetLegacyReceiveContext(source); var result = legacyStore.ReceiveLegacyMessage(message, captured, live);
                if (result.Stored && result.LegacySeq is { } seq)
                { try { LegacyMessageStored?.Invoke(new(source, captured.Scope, seq, message.Reaction ?? ClassroomReactions.MatchText(message.Text)?.Id)); } catch { try { Diagnostic?.Invoke("notification_failed"); } catch { } } }
                return result.Stored;
            }
        };
        _transport.LegacyMessageReceived += (source, message) => {
            DirectMessageReceived?.Invoke(source, message);
            MessageReceived?.Invoke(source, message.Text);
        };
        _transport.MessageStored += notice => V2MessageStored?.Invoke(notice);
        _transport.Diagnostic += code => {
            if (code == "receiver_not_ready") Error = "接收服務已停止，請重新啟動。";
            Diagnostic?.Invoke(code);
        };
    }
    public void Start()
    {
        try { _transport.Start(); Error = null; }
        catch (Exception e) when (e is SocketException or InvalidOperationException or ObjectDisposedException)
        { Error = "接收服務無法啟動，請檢查 TCP 接收埠是否已被占用。"; }
    }
    public ValueTask DisposeAsync() => _transport.DisposeAsync();
}

public sealed record DirectMessage(string Text, string? Reaction);
public sealed record LegacyDeliveryNotice(string SourceIp, Storage.LegacyHistoryScope Scope, long LegacySeq, string? Reaction);
