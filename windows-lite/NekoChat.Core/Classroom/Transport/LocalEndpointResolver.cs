using System;
using System.Net;
using System.Net.Sockets;

namespace NekoChat.Core.Classroom.Transport;

public sealed class LocalEndpointResolutionException : InvalidOperationException
{
    public LocalEndpointResolutionException(string message) : base(message) { }
    public LocalEndpointResolutionException(string message, Exception innerException) : base(message, innerException) { }
}

public static class LocalEndpointResolver
{
    /// <summary>
    /// 連線前預估本機端點位址（純預估值，供早期檢查與狀態顯示使用）。
    /// 注意：真正的通訊端點完全由 TCP SocketsHttpHandler.ConnectCallback 從 NetworkStream 提取並寫入 request.Options。
    /// </summary>
    public static string Resolve(string targetIp, int port)
    {
        if (IPAddress.TryParse(targetIp, out var ip) && IPAddress.IsLoopback(ip))
        {
            return "127.0.0.1";
        }
        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, 0);
            socket.Connect(targetIp, port > 0 ? port : 8768);
            if (socket.LocalEndPoint is IPEndPoint ep)
            {
                var addr = ep.Address;
                return addr.IsIPv4MappedToIPv6 ? addr.MapToIPv4().ToString() : addr.ToString();
            }
        }
        catch (Exception ex)
        {
            throw new LocalEndpointResolutionException($"無法解析本機網路端點位址（fail closed）：{ex.Message}", ex);
        }
        throw new LocalEndpointResolutionException("無法解析本機網路端點位址（fail closed）。");
    }
}
