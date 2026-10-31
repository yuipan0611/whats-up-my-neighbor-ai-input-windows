using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
#if NETFRAMEWORK
using System.Web.Script.Serialization;
#else
using System.Text.Json;
#endif

namespace NekoChat.Animation;

/// One trusted embedded package keeps the installer file-count guard unchanged.
public sealed class AnimationAssets : IDisposable
{
    private readonly Stream resource;
    private readonly ZipArchive archive;
    public Manifest Manifest { get; }
    public AnimationAssets()
    {
        resource = typeof(AnimationAssets).Assembly.GetManifestResourceStream("NekoChat.Animation.assets.zip")
            ?? throw new InvalidDataException("缺少貓咪動畫素材。");
        archive = new ZipArchive(resource, ZipArchiveMode.Read);
#if NETFRAMEWORK
        using (var reader = new StreamReader(Open("manifest.json")))
        {
            var dict = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(reader.ReadToEnd());
            Manifest = ParseManifest(dict);
        }
#else
        using var manifest = Open("manifest.json");
        Manifest = JsonSerializer.Deserialize<Manifest>(manifest, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidDataException("貓咪動畫設定不完整。");
#endif
    }

#if NETFRAMEWORK
    private static Manifest ParseManifest(Dictionary<string, object> dict)
    {
        int version = dict.TryGetValue("version", out var v) && v is int iv ? iv : 1;
        int carry = dict.TryGetValue("carryIndex", out var ci) && ci is int cii ? cii : 19;

        var timesList = Items(dict["times"]);
        var times = timesList.Select(Convert.ToDouble).ToArray();

        var sourcesList = Items(dict["sources"]);
        var sources = sourcesList.Select(Convert.ToInt32).ToArray();

        var coatsList = Items(dict["coats"]);
        var coats = coatsList.Select(Convert.ToString).ToArray();

        var coatNamesList = Items(dict["coatNames"]);
        var coatNames = coatNamesList.Select(Convert.ToString).ToArray();

        var clipsDict = (Dictionary<string, object>)dict["clips"];
        var clips = new Dictionary<string, Clip>();
        foreach (var kvp in clipsDict)
        {
            var c = (Dictionary<string, object>)kvp.Value;
            double duration = Convert.ToDouble(c["duration"]);
            int count = Convert.ToInt32(c["count"]);
            bool loop = Convert.ToBoolean(c["loop"]);
            clips[kvp.Key] = new Clip(duration, count, loop);
        }
        return new Manifest(version, times, sources, carry, coats, coatNames, clips);
    }

    // JavaScriptSerializer.Deserialize<Dictionary<string, object>> materialises JSON arrays as
    // ArrayList (DeserializeObject would give object[]); accept any list rather than one concrete type.
    private static IEnumerable<object> Items(object value) =>
        value is System.Collections.IEnumerable list && !(value is string)
            ? list.Cast<object>()
            : throw new InvalidDataException("貓咪動畫設定不完整。");
#endif

    private Stream Open(string path) => (archive.GetEntry(path) ?? throw new InvalidDataException("貓咪動畫影格不完整。")).Open();
    public Stream OpenFrame(string coat, string frame)
    {
        if (!Manifest.Coats.Contains(coat) || frame.Contains('/') || frame.Contains('\\') || !frame.EndsWith(".png", StringComparison.Ordinal))
            throw new ArgumentException("未知的貓咪動畫。");
        return Open(coat + "/" + frame);
    }
    public void Dispose() { archive.Dispose(); resource.Dispose(); }
}
