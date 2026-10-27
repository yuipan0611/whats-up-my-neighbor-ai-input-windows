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

    public static async Task<bool> SendDirectAsync(string targetIp, string text, int port = DefaultPort, CancellationToken cancellation = default)
    {
        if (!LanInvitation.PrivateIPv4(targetIp))
            throw new ArgumentException("請輸入有效的內網 IPv4 位址（例如 192.168.1.25）。");
        if (!Protocol.ValidText(text))
            throw new ArgumentException("訊息格式不正確（最多 2000 字且不可為空白）。");

        using var handler = new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };

        var payload = JsonSerializer.Serialize(new {
            protocol = "nekochat",
            version = 1,
            kind = "direct.message",
            text
        });

        using var content = new StringContent(payload, Encoding.UTF8, "application/json");
        try
        {
            var response = await client.PostAsync($"http://{targetIp}:{port}/nekochat/v1/direct", content, cancellation);
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException($"對方回應 HTTP {(int)response.StatusCode}");
            var body = await response.Content.ReadAsStringAsync(cancellation);
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.TryGetProperty("status", out var s) && s.GetString() == "delivered";
        }
        catch (Exception ex) when (ex is HttpRequestException or SocketException or TaskCanceledException)
        {
            throw new InvalidOperationException($"無法連線至 {targetIp}（請確認對方已開啟 NekoChat，且雙方在同一個 Wi-Fi／內網）。");
        }
    }
}

public sealed class DirectLanListener : IAsyncDisposable
{
    private readonly TcpListener listener;
    private readonly CancellationTokenSource lifetime = new();
    private Task? loop;
    public int Port { get; }
    public bool Running { get; private set; }
    public string? Error { get; private set; }

    public event Action<string, string>? MessageReceived;

    public DirectLanListener(int port = DirectLanMessaging.DefaultPort)
    {
        Port = port;
        listener = new TcpListener(IPAddress.Any, port);
    }

    public void Start()
    {
        if (Running) return;
        try
        {
            listener.Start();
            Running = true;
            loop = ListenLoopAsync(lifetime.Token);
        }
        catch (Exception ex)
        {
            Error = ex.Message;
        }
    }

    private async Task ListenLoopAsync(CancellationToken cancellation)
    {
        while (!cancellation.IsCancellationRequested && Running)
        {
            try
            {
                var client = await listener.AcceptTcpClientAsync(cancellation);
                _ = HandleClientAsync(client, cancellation);
            }
            catch when (cancellation.IsCancellationRequested || !Running) { break; }
            catch (ObjectDisposedException) { break; }
            catch (InvalidOperationException) { break; }
            catch (SocketException) when (cancellation.IsCancellationRequested || !Running) { break; }
            catch { /* Continue accepting if still active */ }
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken cancellation)
    {
        using (client)
        using (var stream = client.GetStream())
        {
            try
            {
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(6));

                var remoteEndpoint = client.Client.RemoteEndPoint as IPEndPoint;
                var remoteIp = remoteEndpoint?.Address.ToString() ?? "unknown";

                var buffer = new byte[8192];
                int totalRead = 0;
                int headerEnd = -1;

                while (headerEnd < 0 && totalRead < buffer.Length)
                {
                    int n = await stream.ReadAsync(buffer.AsMemory(totalRead, buffer.Length - totalRead), timeoutCts.Token);
                    if (n == 0) break;
                    totalRead += n;
                    headerEnd = FindHeaderEnd(buffer, totalRead);
                }

                if (headerEnd < 0)
                {
                    await SendResponseAsync(stream, 400, "Bad Request", "{\"error\":\"bad_request\"}");
                    return;
                }

                string headerString = Encoding.ASCII.GetString(buffer, 0, headerEnd);
                var lines = headerString.Split(new[] { "\r\n" }, StringSplitOptions.None);
                if (lines.Length == 0 || !lines[0].StartsWith("POST ", StringComparison.OrdinalIgnoreCase))
                {
                    await SendResponseAsync(stream, 400, "Bad Request", "{\"error\":\"method_not_allowed\"}");
                    return;
                }

                int contentLength = 0;
                foreach (var line in lines)
                {
                    if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                    {
                        int.TryParse(line.Substring(15).Trim(), out contentLength);
                        break;
                    }
                }

                if (contentLength <= 0 || contentLength > 65536)
                {
                    await SendResponseAsync(stream, 400, "Bad Request", "{\"error\":\"invalid_length\"}");
                    return;
                }

                byte[] bodyBytes = new byte[contentLength];
                int bodyAlreadyRead = totalRead - headerEnd;
                if (bodyAlreadyRead > 0)
                {
                    int toCopy = Math.Min(bodyAlreadyRead, contentLength);
                    Array.Copy(buffer, headerEnd, bodyBytes, 0, toCopy);
                }

                int bodyRemaining = contentLength - bodyAlreadyRead;
                int bodyReadPos = Math.Min(bodyAlreadyRead, contentLength);
                while (bodyRemaining > 0)
                {
                    int n = await stream.ReadAsync(bodyBytes.AsMemory(bodyReadPos, bodyRemaining), timeoutCts.Token);
                    if (n == 0) break;
                    bodyReadPos += n;
                    bodyRemaining -= n;
                }

                string body = Encoding.UTF8.GetString(bodyBytes);
                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;
                if (!root.TryGetProperty("text", out var textElem) || string.IsNullOrWhiteSpace(textElem.GetString()))
                {
                    await SendResponseAsync(stream, 400, "Bad Request", "{\"error\":\"empty_text\"}");
                    return;
                }

                string text = textElem.GetString()!;
                MessageReceived?.Invoke(remoteIp, text);

                await SendResponseAsync(stream, 200, "OK", "{\"status\":\"delivered\"}");
            }
            catch
            {
                // Drop malformed connection
            }
        }
    }

    private static int FindHeaderEnd(byte[] buffer, int length)
    {
        for (int i = 0; i <= length - 4; i++)
        {
            if (buffer[i] == 13 && buffer[i + 1] == 10 && buffer[i + 2] == 13 && buffer[i + 3] == 10)
                return i + 4;
        }
        return -1;
    }

    private static async Task SendResponseAsync(Stream stream, int statusCode, string reason, string json)
    {
        byte[] bodyBytes = Encoding.UTF8.GetBytes(json);
        string headers = $"HTTP/1.1 {statusCode} {reason}\r\n" +
                         "Content-Type: application/json; charset=utf-8\r\n" +
                         $"Content-Length: {bodyBytes.Length}\r\n" +
                         "Connection: close\r\n\r\n";
        byte[] headerBytes = Encoding.ASCII.GetBytes(headers);
        await stream.WriteAsync(headerBytes);
        await stream.WriteAsync(bodyBytes);
        await stream.FlushAsync();
    }

    public async ValueTask DisposeAsync()
    {
        Running = false;
        lifetime.Cancel();
        try { listener.Stop(); } catch { }
        if (loop != null)
        {
            try { await loop.WaitAsync(TimeSpan.FromSeconds(2)); } catch { }
        }
        lifetime.Dispose();
    }
}
