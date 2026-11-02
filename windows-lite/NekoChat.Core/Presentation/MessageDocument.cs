using System.Collections.ObjectModel;

namespace NekoChat.Core.Presentation;

public enum MessageBlockKind { Text, Code }
public enum CodeCopyNewlines { Preserve, Lf, CrLf }

public readonly record struct MessageTextRange(int Start, int Length);

public sealed class MessageBlock
{
    internal MessageBlock(MessageBlockKind kind, MessageTextRange rawRange,
        MessageTextRange contentRange, string? language, bool isClosed = true)
    {
        Kind = kind;
        RawRange = rawRange;
        ContentRange = contentRange;
        Language = language;
        IsClosed = isClosed;
    }

    public MessageBlockKind Kind { get; }
    public MessageTextRange RawRange { get; }
    public MessageTextRange ContentRange { get; }
    public string? Language { get; }
    /// <summary>False only for a code fence that reaches the end of the message without a closing delimiter.</summary>
    public bool IsClosed { get; }
}

/// <summary>
/// Splits original message text into literal text and fenced code. Rendering remains
/// literal: this model never interprets HTML, links, images, XAML, or executable code.
/// Ranges use UTF-16 offsets into Source; code copies preserve every original newline.
/// </summary>
public sealed class MessageDocument
{
    private MessageDocument(string source, List<MessageBlock> blocks)
    {
        Source = source;
        Blocks = new ReadOnlyCollection<MessageBlock>(blocks);
    }

    public string Source { get; }
    public IReadOnlyList<MessageBlock> Blocks { get; }

    public string GetContent(int blockIndex)
    {
        var range = Blocks[blockIndex].ContentRange;
        return Source.Substring(range.Start, range.Length);
    }

    public string GetRawText(int blockIndex)
    {
        var range = Blocks[blockIndex].RawRange;
        return Source.Substring(range.Start, range.Length);
    }

    public string CopyCode(int blockIndex) => CopyCode(blockIndex, CodeCopyNewlines.Preserve);

    public string CopyCode(int blockIndex, CodeCopyNewlines newlines)
    {
        if (Blocks[blockIndex].Kind != MessageBlockKind.Code)
            throw new InvalidOperationException("The selected block is not code.");
        string content = GetContent(blockIndex);
        return newlines switch
        {
            CodeCopyNewlines.Preserve => content,
            CodeCopyNewlines.Lf => content.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n'),
            CodeCopyNewlines.CrLf => content.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n')
                .Replace("\n", "\r\n", StringComparison.Ordinal),
            _ => throw new ArgumentOutOfRangeException(nameof(newlines))
        };
    }

    public static MessageDocument Parse(string source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var blocks = new List<MessageBlock>();
        int position = 0, plainStart = 0;
        while (position < source.Length)
        {
            var opening = ReadLine(source, position);
            if (!TryOpen(source.AsSpan(position, opening.End - position),
                    out char marker, out int markerCount, out string? language))
            {
                position = opening.Next;
                continue;
            }

            int search = opening.Next;
            bool closed = false;
            while (search < source.Length)
            {
                var closing = ReadLine(source, search);
                if (IsClose(source.AsSpan(search, closing.End - search), marker, markerCount))
                {
                    AddText(blocks, plainStart, position - plainStart);
                    blocks.Add(new MessageBlock(MessageBlockKind.Code,
                        new(position, closing.Next - position),
                        new(opening.Next, search - opening.Next), language));
                    position = closing.Next;
                    plainStart = position;
                    closed = true;
                    break;
                }
                search = closing.Next;
            }

            if (!closed)
            {
                AddText(blocks, plainStart, position - plainStart);
                blocks.Add(new MessageBlock(MessageBlockKind.Code,
                    new(position, source.Length - position),
                    new(opening.Next, source.Length - opening.Next), language, isClosed: false));
                plainStart = source.Length;
                // The unclosed body owns the remaining text, so suffixes are never rescanned.
                break;
            }
        }
        AddText(blocks, plainStart, source.Length - plainStart);
        return new MessageDocument(source, blocks);
    }

    private static void AddText(List<MessageBlock> blocks, int start, int length)
    {
        if (length > 0)
            blocks.Add(new MessageBlock(MessageBlockKind.Text,
                new(start, length), new(start, length), null));
    }

    private static (int End, int Next) ReadLine(string source, int start)
    {
        int end = start;
        while (end < source.Length && source[end] is not ('\r' or '\n')) end++;
        int next = end;
        if (next < source.Length)
        {
            char newline = source[next++];
            if (newline == '\r' && next < source.Length && source[next] == '\n') next++;
        }
        return (end, next);
    }

    private static int Indent(ReadOnlySpan<char> line)
    {
        int spaces = 0;
        while (spaces < line.Length && line[spaces] == ' ') spaces++;
        return spaces;
    }

    private static bool TryOpen(ReadOnlySpan<char> line, out char marker,
        out int markerCount, out string? language)
    {
        marker = default;
        markerCount = 0;
        language = null;
        int start = Indent(line);
        if (start > 3 || start == line.Length || line[start] is not ('`' or '~')) return false;
        marker = line[start];
        int end = start;
        while (end < line.Length && line[end] == marker) end++;
        markerCount = end - start;
        if (markerCount < 3) return false;
        var info = line[end..].Trim();
        if (marker == '`' && info.Contains('`')) return false;
        int tokenEnd = 0;
        while (tokenEnd < info.Length && !char.IsWhiteSpace(info[tokenEnd])) tokenEnd++;
        language = KnownLanguage(info[..tokenEnd]);
        return true;
    }

    private static bool IsClose(ReadOnlySpan<char> line, char marker, int markerCount)
    {
        int start = Indent(line);
        if (start > 3) return false;
        int end = start;
        while (end < line.Length && line[end] == marker) end++;
        if (end - start < markerCount) return false;
        foreach (char c in line[end..])
            if (c is not (' ' or '\t')) return false;
        return true;
    }

    private static string? KnownLanguage(ReadOnlySpan<char> token) => token.ToString().ToLowerInvariant() switch
    {
        "c#" or "cs" or "csharp" => "csharp",
        "c++" or "cpp" => "cpp",
        "python" or "py" => "python",
        "javascript" or "js" => "javascript",
        "typescript" or "ts" => "typescript",
        "powershell" or "ps1" => "powershell",
        "bash" or "sh" => "bash",
        "json" => "json",
        "sql" => "sql",
        "xml" => "xml",
        "html" => "html",
        "css" => "css",
        "text" or "txt" or "plaintext" => "plaintext",
        _ => null
    };
}
