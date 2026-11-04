using System.Text.RegularExpressions;
using System.Windows.Documents;
using System.Windows.Media;

namespace NekoChat.Windows;

internal static class SyntaxHighlighter
{
    private static readonly SolidColorBrush KeywordBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x56, 0x9C, 0xD6)));
    private static readonly SolidColorBrush StringBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xCE, 0x91, 0x78)));
    private static readonly SolidColorBrush CommentBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x6A, 0x99, 0x55)));
    private static readonly SolidColorBrush NumberBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xB5, 0xCE, 0xA8)));
    private static readonly SolidColorBrush FunctionBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xDC, 0xDC, 0xAA)));
    private static readonly SolidColorBrush DefaultBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xD4, 0xD4, 0xD4)));

    private static SolidColorBrush Freeze(SolidColorBrush b) { b.Freeze(); return b; }

    private static readonly HashSet<string> PythonKeywords = new(StringComparer.Ordinal)
    {
        "def", "class", "import", "from", "return", "if", "elif", "else", "for", "while",
        "try", "except", "finally", "with", "as", "in", "is", "not", "and", "or",
        "True", "False", "None", "self", "lambda", "async", "await", "yield", "break",
        "continue", "pass", "raise", "global", "nonlocal", "del", "assert"
    };

    private static readonly HashSet<string> CSharpKeywords = new(StringComparer.Ordinal)
    {
        "public", "private", "protected", "internal", "static", "void", "class", "struct",
        "record", "interface", "enum", "namespace", "using", "new", "return", "if", "else",
        "for", "foreach", "while", "do", "switch", "case", "break", "continue", "try",
        "catch", "finally", "throw", "async", "await", "var", "string", "int", "bool",
        "double", "float", "long", "byte", "object", "null", "true", "false", "this",
        "base", "get", "set", "init", "readonly", "override", "virtual", "abstract",
        "sealed", "event", "delegate", "const", "in", "out", "ref", "is", "as"
    };

    private static readonly HashSet<string> JsKeywords = new(StringComparer.Ordinal)
    {
        "function", "const", "let", "var", "return", "if", "else", "for", "while",
        "switch", "case", "break", "continue", "try", "catch", "finally", "throw",
        "async", "await", "import", "export", "from", "default", "class", "extends",
        "new", "this", "true", "false", "null", "undefined", "typeof", "instanceof",
        "yield", "super", "debugger"
    };

    public static void HighlightInto(InlineCollection inlines, string text, string? rawLanguage)
    {
        inlines.Clear();
        if (string.IsNullOrEmpty(text)) return;

        string lang = (rawLanguage ?? "").Trim().ToLowerInvariant();
        if (lang.Length == 0 || lang is "text" or "plain" or "txt")
        {
            inlines.Add(new Run(text) { Foreground = DefaultBrush });
            return;
        }

        var keywords = lang switch
        {
            "python" or "py" => PythonKeywords,
            "csharp" or "c#" or "cs" => CSharpKeywords,
            "javascript" or "js" or "typescript" or "ts" => JsKeywords,
            _ => PythonKeywords // Fallback to python/generic
        };

        bool isPython = lang is "python" or "py";

        int i = 0;
        int len = text.Length;

        while (i < len)
        {
            char c = text[i];

            // 1. Comments
            if ((isPython && c == '#') || (!isPython && c == '/' && i + 1 < len && text[i + 1] == '/'))
            {
                int start = i;
                while (i < len && text[i] != '\n' && text[i] != '\r') i++;
                inlines.Add(new Run(text[start..i]) { Foreground = CommentBrush });
                continue;
            }

            if (!isPython && c == '/' && i + 1 < len && text[i + 1] == '*')
            {
                int start = i;
                i += 2;
                while (i < len && !(text[i - 1] == '*' && text[i] == '/')) i++;
                if (i < len) i++;
                inlines.Add(new Run(text[start..Math.Min(i, len)]) { Foreground = CommentBrush });
                continue;
            }

            // 2. Strings
            if (c is '"' or '\'' or '`')
            {
                char quote = c;
                int start = i;
                i++;
                while (i < len)
                {
                    if (text[i] == '\\' && i + 1 < len)
                    {
                        i += 2;
                        continue;
                    }
                    if (text[i] == quote)
                    {
                        i++;
                        break;
                    }
                    if (text[i] == '\n') break; // single-line string guard
                    i++;
                }
                inlines.Add(new Run(text[start..Math.Min(i, len)]) { Foreground = StringBrush });
                continue;
            }

            // 3. Numbers
            if (char.IsDigit(c))
            {
                int start = i;
                while (i < len && (char.IsLetterOrDigit(text[i]) || text[i] == '.' || text[i] == '_')) i++;
                inlines.Add(new Run(text[start..i]) { Foreground = NumberBrush });
                continue;
            }

            // 4. Words / Identifiers / Keywords / Functions
            if (char.IsLetter(c) || c == '_')
            {
                int start = i;
                while (i < len && (char.IsLetterOrDigit(text[i]) || text[i] == '_')) i++;
                string word = text[start..i];

                if (keywords.Contains(word))
                {
                    inlines.Add(new Run(word) { Foreground = KeywordBrush });
                }
                else
                {
                    // Check if function call (look ahead for '(')
                    int look = i;
                    while (look < len && (text[look] == ' ' || text[look] == '\t')) look++;
                    if (look < len && text[look] == '(')
                    {
                        inlines.Add(new Run(word) { Foreground = FunctionBrush });
                    }
                    else
                    {
                        inlines.Add(new Run(word) { Foreground = DefaultBrush });
                    }
                }
                continue;
            }

            // 5. Default characters / punctuation / whitespace
            int plainStart = i;
            while (i < len)
            {
                char ch = text[i];
                if (char.IsLetterOrDigit(ch) || ch == '_' || ch is '"' or '\'' or '`' ||
                    (isPython && ch == '#') || (!isPython && ch == '/' && i + 1 < len && (text[i + 1] == '/' || text[i + 1] == '*')))
                {
                    break;
                }
                i++;
            }
            inlines.Add(new Run(text[plainStart..i]) { Foreground = DefaultBrush });
        }
    }
}
