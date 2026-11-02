using NekoChat.Core.Presentation;

namespace NekoChat.Tests;

internal static class MessageFormattingChecks
{
    public static int Run()
    {
        int checks = 0;
        void Check(bool result, string description)
        {
            checks++;
            if (!result) throw new Exception("Message formatting: " + description);
        }

        MessageDocument Parse(string source)
        {
            var doc = MessageDocument.Parse(source);
            int cursor = 0;
            bool validRanges = true;
            foreach (var block in doc.Blocks)
            {
                validRanges &= block.RawRange.Start == cursor && block.RawRange.Length > 0 &&
                    block.ContentRange.Start >= cursor && block.ContentRange.Length >= 0 &&
                    block.ContentRange.Start + block.ContentRange.Length <= cursor + block.RawRange.Length;
                cursor += block.RawRange.Length;
            }
            Check(validRanges, "raw ranges partition the message and contain their content ranges");
            Check(cursor == source.Length && string.Concat(Enumerable.Range(0, doc.Blocks.Count)
                .Select(doc.GetRawText)) == source, "all original text round trips");
            return doc;
        }

        Check(Parse("").Blocks.Count == 0, "empty message is empty");
        foreach (var source in new[] { "你好 🐈\r\n  原文\n", "`inline` and **literal**", "    ```py\nx=1\n    ```",
            "<script>alert(1)</script> ![image](https://example.test/private) <Run/>" })
        {
            var doc = Parse(source);
            Check(doc.Blocks.Count == 1 && doc.Blocks[0].Kind == MessageBlockKind.Text &&
                doc.GetContent(0) == source, "non-fenced syntax remains literal text");
        }

        foreach (var newline in new[] { "\n", "\r\n", "\r" })
        {
            string code = "# 中文註解 🐈" + newline + "  x = 1" + newline + newline + "\tprint(x)" + newline;
            var doc = Parse("之前" + newline + "```Python" + newline + code + "```" + newline + "之後");
            Check(doc.Blocks.Count == 3 && doc.Blocks[1].Kind == MessageBlockKind.Code,
                "fenced code separates surrounding prose");
            Check(doc.Blocks[1].Language == "python" && doc.Blocks[1].IsClosed && doc.CopyCode(1) == code,
                "copy preserves Unicode, indentation, blank lines and original newline bytes");
        }

        foreach (var (source, expected, language) in new (string, string, string?)[]
        {
            ("```c#\nx = 1\n```", "x = 1\n", "csharp"),
            ("~~~unknown-language\n  原文\n~~~", "  原文\n", null),
            ("```\n```", "", null),
            ("   ~~~json\n{}\n   ~~~~~ \t", "{}\n", "json"),
            ("````js\n```\nalert(1)\n````", "```\nalert(1)\n", "javascript"),
            ("```html\n<script>run()</script>\n```", "<script>run()</script>\n", "html"),
            ("~~~\n```\n~~~", "```\n", null),
            ("```py extra-caption\nx\n```", "x\n", "python"),
            ("```py\nx\n``` trailing text\ny\n```", "x\n``` trailing text\ny\n", "python")
        })
        {
            var doc = Parse(source);
            Check(doc.Blocks.Count == 1 && doc.Blocks[0].Kind == MessageBlockKind.Code &&
                doc.Blocks[0].Language == language && doc.CopyCode(0) == expected,
                "code delimiter and language fixture");
        }

        foreach (var (source, body) in new[] { ("```py\nx = 1", "x = 1"), ("intro\n```", ""),
            ("~~~py\nx\n```", "x\n```"), ("````py\nx\n```", "x\n```"), ("```", ""), ("~~~py\rx\ry", "x\ry") })
        {
            var doc = Parse(source);
            int index = doc.Blocks.Count - 1;
            Check(doc.Blocks[index].Kind == MessageBlockKind.Code && !doc.Blocks[index].IsClosed && doc.CopyCode(index) == body,
                "unclosed fence stays code with an explicit flag and exact body");
        }
        foreach (var source in new[] { "```bad`label\nx\nplain", "`` not a fence\nx", "\t```py\nx\n\t```", "    ~~~\nx\n    ~~~" })
        {
            var doc = Parse(source);
            Check(doc.Blocks.All(x => x.Kind == MessageBlockKind.Text) && doc.GetContent(0) == source,
                "unsupported fence syntax preserves its entire original text");
        }

        foreach (var (alias, language) in new[] { ("cs", "csharp"), ("CSHARP", "csharp"), ("c++", "cpp"), ("cpp", "cpp"),
            ("py", "python"), ("PYTHON", "python"), ("js", "javascript"), ("ts", "typescript"), ("ps1", "powershell"),
            ("sh", "bash"), ("txt", "plaintext"), ("text", "plaintext"), ("plaintext", "plaintext") })
        {
            var doc = Parse("```" + alias + "\nx\n```");
            Check(doc.Blocks.Single().Language == language && doc.Blocks.Single().IsClosed, "language alias fixture");
        }
        var emptyInfo = Parse("``` \t \nx\n```");
        Check(emptyInfo.Blocks.Single().Language is null && emptyInfo.CopyCode(0) == "x\n", "whitespace-only info string");
        string mixed = "A\r\nB\rC\n\nD\u0085E\u2028F\n";
        var copy = Parse("```text\n" + mixed + "```");
        Check(copy.CopyCode(0) == mixed && copy.CopyCode(0, CodeCopyNewlines.Preserve) == mixed, "default copy preserves all original newline characters");
        Check(copy.CopyCode(0, CodeCopyNewlines.Lf) == "A\nB\nC\n\nD\u0085E\u2028F\n", "explicit LF changes only CR/LF");
        Check(copy.CopyCode(0, CodeCopyNewlines.CrLf) == "A\r\nB\r\nC\r\n\r\nD\u0085E\u2028F\r\n", "explicit CRLF changes only CR/LF");
        bool invalidPolicy = false;
        try { copy.CopyCode(0, (CodeCopyNewlines)999); } catch (ArgumentOutOfRangeException) { invalidPolicy = true; }
        Check(invalidPolicy, "unknown copy policy rejected");

        var multiple = Parse("```py\na\n```\n```sql\nselect 1;\n```\ntrailing");
        Check(multiple.Blocks.Count == 3 && multiple.CopyCode(0) == "a\n" &&
            multiple.CopyCode(1) == "select 1;\n" && multiple.GetContent(2) == "trailing",
            "each code block copies only its own original body");
        bool rejected = false;
        try { multiple.CopyCode(2); } catch (InvalidOperationException) { rejected = true; }
        Check(rejected, "plain text cannot be mistaken for a code copy target");

        var stress = Parse(string.Concat(Enumerable.Repeat("```py\nx\n```\n", 1000)));
        Check(stress.Blocks.Count == 1000 && stress.CopyCode(999) == "x\n",
            "many blocks remain ordered without dropping text");
        var longUnclosed = "```py\n" + string.Concat(Enumerable.Repeat("`` not closed\n", 10000));
        Check(Parse(longUnclosed).CopyCode(0) == longUnclosed[6..], "unclosed code input is handled without rescanning suffixes");
        Console.WriteLine($"Message formatting: {checks} behavior checks passed.");
        return checks;
    }
}
