using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace NekoChat.Tests;

/// <summary>N001A-v4 §3.2/§12-3 rules, enforced on the Storage sources themselves (review item A).</summary>
internal static class StorageFix_SourceRules
{
    private static string StorageDir([CallerFilePath] string here = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "NekoChat.Core", "Storage"));

    internal static int Run()
    {
        var k = new StorageFixKit.Counter();
        var files = Directory.GetFiles(StorageDir(), "*.cs");
        k.Check(files.Length >= 15, "Storage sources found");
        int begins = 0, catches = 0;
        foreach (var file in files)
        {
            string name = Path.GetFileName(file);
            string text = File.ReadAllText(file);
            string noComments = Regex.Replace(Regex.Replace(text, @"/\*.*?\*/", "", RegexOptions.Singleline), @"//[^\n]*", "");
            foreach (Match m in Regex.Matches(noComments, @"\bcatch\b[^{;]*\{(?<body>[^{}]*)\}"))
            {
                catches++;
                k.Check(m.Groups["body"].Value.Contains(';'), $"{name}: catch block must do something (no empty catch): {m.Value.Trim()[..Math.Min(60, m.Value.Trim().Length)]}");
            }
            begins += Regex.Matches(noComments, @"BeginTransaction\(").Count;
            k.Check(!Regex.IsMatch(noComments, @"BeginTransaction\((?!deferred: false)"), name + ": every BeginTransaction names deferred: false");
            k.Check(!Regex.IsMatch(noComments, "\"\\s*BEGIN(\\s+(IMMEDIATE|DEFERRED|EXCLUSIVE|TRANSACTION))?\\s*[;\"]", RegexOptions.IgnoreCase), name + ": no hand-written BEGIN command");
            k.Check(!noComments.Contains("System.Net"), name + ": no System.Net in Storage");
            k.Check(!Regex.IsMatch(noComments, @"busy_timeout\s*=", RegexOptions.IgnoreCase), name + ": Default Timeout is the only lock-wait source");
            k.Check(!Regex.IsMatch(noComments, @"\bConsole\.|Debug\.Write|Trace\.Write"), name + ": storage does not log to console");
        }
        k.Check(begins >= 2, "BeginTransaction call sites found (" + begins + ")");
        k.Check(catches >= 10, "catch blocks were actually scanned (" + catches + ")");
        return k.Count;
    }
}
