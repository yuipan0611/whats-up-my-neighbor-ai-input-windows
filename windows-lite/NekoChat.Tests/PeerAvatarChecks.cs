using System.Diagnostics;
using System.Text.Json;
using NekoChat.Core.Presentation;

namespace NekoChat.Tests;

internal static class PeerAvatarChecks
{
    private const string ChildFlag = "--peer-avatar-child";
    private sealed record ChildResult(int ProcessId, int[] Indices);

    internal static bool RunChild(string[] args)
    {
        if (!args.SequenceEqual(new[] { ChildFlag })) return false;
        var seeds = JsonSerializer.Deserialize<string[]>(Console.In.ReadToEnd()) ?? throw new InvalidDataException("Missing synthetic avatar seeds.");
        Console.WriteLine(JsonSerializer.Serialize(new ChildResult(Environment.ProcessId,
            seeds.Select(seed => PeerAvatarPolicy.PresetIndex(seed, "local note", 9)).ToArray())));
        return true;
    }

    private static async Task<ChildResult> Sample(string[] seeds)
    {
        string executable = Environment.ProcessPath ?? throw new InvalidOperationException("Missing test executable.");
        var info = new ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase)) info.ArgumentList.Add(typeof(PeerAvatarChecks).Assembly.Location);
        info.ArgumentList.Add(ChildFlag);
        using var child = Process.Start(info) ?? throw new InvalidOperationException("Avatar child did not start.");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        var output = child.StandardOutput.ReadToEndAsync(); var errors = child.StandardError.ReadToEndAsync();
        try
        {
            await child.StandardInput.WriteAsync(JsonSerializer.Serialize(seeds)); child.StandardInput.Close();
            await child.WaitForExitAsync(deadline.Token);
            string text = await output, error = await errors;
            if (child.ExitCode != 0 || error.Length != 0) throw new InvalidOperationException("Avatar child failed: " + child.ExitCode + ": " + error);
            return JsonSerializer.Deserialize<ChildResult>(text) ?? throw new InvalidDataException("Missing avatar child result.");
        }
        finally
        {
            if (!child.HasExited) { child.Kill(entireProcessTree: true); await child.WaitForExitAsync(); }
        }
    }

    internal static async Task Run(Action<bool, string> check)
    {
        check(PeerAvatarPolicy.PresetIndex("contact-fixed", null, 9) == 6 && PeerAvatarPolicy.PresetIndex("聯絡人甲", null, 9) == 3,
            "avatar SHA-256 UTF-8 / big-endian fixed vectors preserve palette mapping on every platform");
        check(PeerAvatarPolicy.PresetIndex(null, null, 9) == 0 && PeerAvatarPolicy.PresetIndex(" ", "", 9) == 0,
            "missing identity and seed retain original fallback");
        check(PeerAvatarPolicy.PresetIndex("contact-fixed", "old local note", 9) == PeerAvatarPolicy.PresetIndex("contact-fixed", "new local note", 9) &&
            PeerAvatarPolicy.PresetIndex(null, "contact-fixed", 9) == 6, "known identity beats local notes; seed-only fallback uses the same stable algorithm");
        string[] seeds = Enumerable.Range(0, 128).Select(i => "synthetic-peer-" + i).Append("聯絡人甲").ToArray();
        // Start both children before waiting: their OS process identities must be distinct.
        var samples = await Task.WhenAll(Sample(seeds), Sample(seeds));
        check(samples[0].ProcessId != samples[1].ProcessId && samples.All(sample => sample.ProcessId != Environment.ProcessId), "two actual child processes computed the avatar mapping");
        check(samples[0].Indices.SequenceEqual(samples[1].Indices), "same keys select identical coats across two separate processes");
        check(samples[0].Indices.SequenceEqual(seeds.Select(seed => PeerAvatarPolicy.PresetIndex(seed, null, 9))), "child mappings equal the parent mapping for all synthetic keys");
        check(samples[0].Indices.Distinct().Count() >= 3, "different seeds distribute over multiple existing coat presets");
        check(samples.SelectMany(sample => sample.Indices).All(index => index is >= 0 and < 9), "all avatar indices remain within the existing nine-coat palette");
    }
}
