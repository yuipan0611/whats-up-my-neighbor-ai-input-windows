using System.Text;

namespace NekoChat.Core.V2;

public enum V2TextProblem { None, InvalidUtf16, ControlCharacter, Blank, TooLarge }

/// <summary>
/// Message text rules (N002A-v2.3 §8.4, G-6): well-formed UTF-16, no Cc control characters except TAB/LF/CR, not all white space,
/// and at most <c>maxUtf8Bytes</c> bytes when encoded as UTF-8. The limit stays injectable; the product value is
/// <see cref="ProductMaxUtf8Bytes"/> (D-06, user decision 2026-11-03: "改為 64 KiB 上限").
/// </summary>
public static class V2Text
{
    /// <summary>D-06 (user-confirmed 2026-11-03): v2 body text may be exactly 65,536 UTF-8 bytes; 65,537 is rejected, never truncated.</summary>
    public const int ProductMaxUtf8Bytes = 65536;
    public const int DefaultMaxUtf8Bytes = ProductMaxUtf8Bytes;

    public static V2TextProblem Check(string? text, int maxUtf8Bytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxUtf8Bytes);
        if (text is null) return V2TextProblem.Blank;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (char.IsHighSurrogate(c))
            {
                if (i + 1 < text.Length && char.IsLowSurrogate(text[i + 1])) i++;
                else return V2TextProblem.InvalidUtf16;
            }
            else if (char.IsLowSurrogate(c)) return V2TextProblem.InvalidUtf16;
        }
        foreach (char c in text)
            if (char.IsControl(c) && c is not ('\t' or '\n' or '\r')) return V2TextProblem.ControlCharacter;
        bool allWhite = true;
        foreach (char c in text) if (!char.IsWhiteSpace(c)) { allWhite = false; break; }
        if (allWhite) return V2TextProblem.Blank;
        return Encoding.UTF8.GetByteCount(text) > maxUtf8Bytes ? V2TextProblem.TooLarge : V2TextProblem.None;
    }
}

public enum MessageLimitVerdict { Ok, Invalid, V2TooLarge, LegacyTooLarge }

/// <summary>
/// CM-07 (PM engineering default, not a user decision): v2 recipients use <see cref="V2Text.ProductMaxUtf8Bytes"/> UTF-8 bytes;
/// v1 recipients keep the existing 2,000 Unicode scalar values. A rejection never alters the text, so the caller keeps the draft.
/// </summary>
public static class MessageLimitPolicy
{
    public const int LegacyMaxScalars = 2000;
    public const string LegacyTooLargeNotice = "對方是舊版，最多 2,000 字";

    public static MessageLimitVerdict Check(string? text, bool recipientIsLegacy, int v2MaxUtf8Bytes = V2Text.ProductMaxUtf8Bytes)
    {
        if (!recipientIsLegacy)
            return V2Text.Check(text, v2MaxUtf8Bytes) switch { V2TextProblem.None => MessageLimitVerdict.Ok, V2TextProblem.TooLarge => MessageLimitVerdict.V2TooLarge, _ => MessageLimitVerdict.Invalid };
        if (string.IsNullOrWhiteSpace(text)) return MessageLimitVerdict.Invalid;
        return text.EnumerateRunes().Count() > LegacyMaxScalars ? MessageLimitVerdict.LegacyTooLarge : MessageLimitVerdict.Ok;
    }
}
