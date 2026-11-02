using System.Text;

namespace NekoChat.Core.V2;

/// <summary>
/// Field-format checks for the v2 wire contract (N002A-v2.3 §5.1, §5.3 "格式比對規則").
/// Every check is a hand-written whole-string comparison: no Regex, so there is no "$ matches before a final \n" trap,
/// and any line terminator (LF, CR, NEL, LS, PS) or other stray character simply fails the character-set test.
/// </summary>
public static class V2Format
{
    public const int PeerIdLength = 43;
    public const int Nonce22Length = 22;
    public const int MaxCapabilityLength = 32;
    public const int MaxReactionLength = 32;
    public const int MaxPositiveIntDigits = 9;

    private static bool IsLowerAlnum(char c) => c is (>= 'a' and <= 'z') or (>= '0' and <= '9');
    private static bool IsBase64UrlChar(char c) => c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-' or '_';
    private static bool IsLowerHex(char c) => c is (>= '0' and <= '9') or (>= 'a' and <= 'f');
    private static bool IsAscii09(char c) => c is >= '0' and <= '9';

    /// <summary>PeerId: 43 characters of the base64url alphabet (format only; identity is always compared with H(SPKI)).</summary>
    public static bool IsPeerId(string? s)
    {
        if (s is null || s.Length != PeerIdLength) return false;
        foreach (char c in s) if (!IsBase64UrlChar(c)) return false;
        return true;
    }

    /// <summary>22-character canonical base64url of 16 bytes: the last character carries only 2 data bits, so it must be one of A, Q, g, w.</summary>
    public static bool IsNonce22(string? s)
    {
        if (s is null || s.Length != Nonce22Length) return false;
        foreach (char c in s) if (!IsBase64UrlChar(c)) return false;
        return s[^1] is 'A' or 'Q' or 'g' or 'w';
    }

    /// <summary>Lower-case canonical 8-4-4-4-12 UUID text. Format only: version and variant bits are not inspected.</summary>
    public static bool IsUuid(string? s)
    {
        if (s is null || s.Length != 36) return false;
        for (int i = 0; i < 36; i++)
        {
            bool dash = i is 8 or 13 or 18 or 23;
            if (dash ? s[i] != '-' : !IsLowerHex(s[i])) return false;
        }
        return true;
    }

    public static bool IsHex64(string? s)
    {
        if (s is null || s.Length != 64) return false;
        foreach (char c in s) if (!IsLowerHex(c)) return false;
        return true;
    }

    /// <summary>Capability token: [a-z][a-z0-9.-]{0,31}.</summary>
    public static bool IsCapability(string? s)
    {
        if (s is null || s.Length is 0 or > MaxCapabilityLength) return false;
        if (s[0] is not (>= 'a' and <= 'z')) return false;
        for (int i = 1; i < s.Length; i++) if (!(IsLowerAlnum(s[i]) || s[i] is '.' or '-')) return false;
        return true;
    }

    /// <summary>Raw JSON integer token 1..999,999,999 without sign, leading zero, fraction or exponent: [1-9][0-9]{0,8}.</summary>
    public static bool IsPositiveIntLiteral(string? s)
    {
        if (s is null || s.Length is 0 or > MaxPositiveIntDigits) return false;
        if (s[0] is not (>= '1' and <= '9')) return false;
        for (int i = 1; i < s.Length; i++) if (!IsAscii09(s[i])) return false;
        return true;
    }

    /// <summary>Opaque reaction id: [a-z0-9][a-z0-9-]{0,31}. The empty string is invalid (it would sign identically to null).</summary>
    public static bool IsReactionId(string? s)
    {
        if (s is null || s.Length is 0 or > MaxReactionLength) return false;
        if (!IsLowerAlnum(s[0])) return false;
        for (int i = 1; i < s.Length; i++) if (!(IsLowerAlnum(s[i]) || s[i] == '-')) return false;
        return true;
    }

    /// <summary>
    /// Standard base64 with padding, in canonical form: only the alphabet and trailing '=' are accepted (so no whitespace or URL alphabet),
    /// and re-encoding the decoded bytes must reproduce the input exactly (so non-zero padding bits are rejected).
    /// </summary>
    public static bool TryDecodeBase64(string? s, out byte[] bytes)
    {
        bytes = Array.Empty<byte>();
        if (s is null || s.Length == 0 || s.Length % 4 != 0) return false;
        int dataEnd = s.Length;
        while (dataEnd > 0 && s[dataEnd - 1] == '=') dataEnd--;
        if (s.Length - dataEnd > 2) return false;
        for (int i = 0; i < dataEnd; i++)
        {
            char c = s[i];
            if (!(c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9') or '+' or '/')) return false;
        }
        try
        {
            var decoded = Convert.FromBase64String(s);
            if (Convert.ToBase64String(decoded) != s) return false;
            bytes = decoded;
            return true;
        }
        catch (FormatException) { return false; }
    }

    /// <summary>Decodes a canonical 22-character base64url nonce to its 16 bytes.</summary>
    public static bool TryDecodeNonce22(string? s, out byte[] bytes)
    {
        bytes = Array.Empty<byte>();
        if (!IsNonce22(s)) return false;
        bytes = Convert.FromBase64String(s!.Replace('-', '+').Replace('_', '/') + "==");
        return bytes.Length == 16;
    }

    public static string ToBase64Url(ReadOnlySpan<byte> data) => Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static string ToLowerHex(ReadOnlySpan<byte> data) => Convert.ToHexString(data).ToLowerInvariant();

    internal static byte[] Utf8(string s) => new UTF8Encoding(false, true).GetBytes(s);
}
