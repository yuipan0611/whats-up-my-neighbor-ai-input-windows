using System.IO.Compression;
using System.Text.Json;

namespace NekoChat.Animation;

/// One trusted embedded package keeps the installer file-count guard unchanged.
public sealed class AnimationAssets : IDisposable
{
    private readonly Stream resource;
    private readonly ZipArchive archive;
    public Manifest Manifest { get; }
    public AnimationAssets()
    {
        resource=typeof(AnimationAssets).Assembly.GetManifestResourceStream("NekoChat.Animation.assets.zip")
            ?? throw new InvalidDataException("缺少貓咪動畫素材。");
        archive=new ZipArchive(resource,ZipArchiveMode.Read);
        using var manifest=Open("manifest.json");
        Manifest=JsonSerializer.Deserialize<Manifest>(manifest,new JsonSerializerOptions{PropertyNameCaseInsensitive=true})
            ?? throw new InvalidDataException("貓咪動畫設定不完整。");
    }
    private Stream Open(string path)=>(archive.GetEntry(path)??throw new InvalidDataException("貓咪動畫影格不完整。")).Open();
    public Stream OpenFrame(string coat,string frame)
    {
        if(!Manifest.Coats.Contains(coat) || frame.Contains('/') || frame.Contains('\\') || !frame.EndsWith(".png",StringComparison.Ordinal))
            throw new ArgumentException("未知的貓咪動畫。");
        return Open(coat+"/"+frame);
    }
    public void Dispose(){archive.Dispose();resource.Dispose();}
}
