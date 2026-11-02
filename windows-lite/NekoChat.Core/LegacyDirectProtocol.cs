using NekoChat.Core.V2;

namespace NekoChat.Core;

/// <summary>Only for /nekochat/v1/direct. Produces unverified text/IP events, never verified identity or receipt evidence.</summary>
internal static class LegacyDirectProtocol
{
    internal const int MaxBodyBytes = 65536;
    internal static bool TryParse(ReadOnlySpan<byte> bytes, out DirectMessage? message, out string error)
    {
        message = null; error = "invalid_payload";
        if (!V2Json.TryParseObject(bytes, new(MaxBodyBytes, 8, 64), out var p, out var jsonError))
        { if (jsonError == V2JsonError.TooLarge) error = "payload_too_large"; return false; }
        bool plain = p!.HasExactKeys("text") || p.HasExactKeys("text", "reaction");
        bool envelope = p.HasExactKeys("protocol", "version", "kind", "text") || p.HasExactKeys("protocol", "version", "kind", "text", "reaction");
        if (!plain && !envelope) return false;
        if (envelope && (!p.TryGetString("protocol", out var protocol) || protocol != "nekochat" ||
            !p.TryGetNumberToken("version", out var version) || version != "1" || !p.TryGetString("kind", out var kind) || kind != "direct.message"))
        { error = "unsupported_version"; return false; }
        if (!p.TryGetString("text", out var text) || !Protocol.ValidText(text)) return false;
        string? reaction = null;
        if (p.ContainsKey("reaction") && !p.IsNull("reaction"))
        { if (!p.TryGetString("reaction", out var value)) return false; reaction = value; }
        message = new(text, reaction); return true;
    }
}
