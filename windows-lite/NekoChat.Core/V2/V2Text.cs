using System.Text;

namespace NekoChat.Core.V2;

public enum V2TextProblem { None, InvalidUtf16, ControlCharacter, Blank, TooLarge }

/// <summary>
/// Message text rules (N002A-v2.3 §8.4, G-6): well-formed UTF-16, no Cc control characters except TAB/LF/CR, not all white space,
/// and at most <c>maxUtf8Bytes</c> bytes when encoded as UTF-8. The limit is injectable because D-06 is not yet confirmed by the user;
/// <see cref="DefaultMaxUtf8Bytes"/> is the proposed value only.
/// </summary>
public static class V2Text
{
    /// <summary>Proposed (unconfirmed) limit from N002A §8.4. Callers must pass the value from their policy.</summary>
    public const int DefaultMaxUtf8Bytes = 16384;

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
