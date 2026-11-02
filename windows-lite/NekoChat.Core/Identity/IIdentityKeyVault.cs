using System.Security.Cryptography;
using NekoChat.Core.Storage;

namespace NekoChat.Core.Identity;

public sealed record IdentityCredential(string PeerId, string PublicSpkiBase64, string Reference);

/// <summary>Private material stays outside SQLite. Returned keys are owned and disposed by the caller.</summary>
public interface IIdentityKeyVault : IIdentityKeyProbe
{
    IdentityCredential CreateCredential();
    ECDsa OpenSigningKey(string credentialRef);
}

public sealed class IdentityUnavailableException(string code) : Exception("Local identity is unavailable.")
{
    public string Code { get; } = code;
}
