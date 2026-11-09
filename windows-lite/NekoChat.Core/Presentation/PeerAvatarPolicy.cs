using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace NekoChat.Core.Presentation;

/// <summary>Deterministic fallback artwork, independent of runtime string-hash salts or local notes.</summary>
public static class PeerAvatarPolicy
{
    public static int PresetIndex(string? identityKey, string? fallbackSeed, int presetCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(presetCount);
        string? key = string.IsNullOrWhiteSpace(identityKey) ? fallbackSeed : identityKey;
        if (string.IsNullOrWhiteSpace(key)) return 0;
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        return (int)(BinaryPrimitives.ReadUInt32BigEndian(hash) % (uint)presetCount);
    }
}
