using NekoChat.Core.V2;

namespace NekoChat.Core.Pairing;

/// <summary>Borrowed endpoint. Handler must honor cancellation before committing trust; stop invalidates intent immediately and drains its work.</summary>
public interface IPairingEndpoint
{
    Task<V2HttpResponse> HandleAsync(ReadOnlyMemory<byte> body, string source, CancellationToken cancellation = default);
    Task StopAsync();
}
