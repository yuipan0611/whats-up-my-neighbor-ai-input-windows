using System.Security.Cryptography;

namespace NekoChat.Core.Attachments;

/// <summary>Only generated ids address files. Preallocated .part files reserve quota before any network bytes are read.</summary>
public sealed class AttachmentVault
{
    private readonly object _gate = new();
    private readonly string _root;
    private readonly long _capacity;
    public AttachmentVault(string root, long capacity = AttachmentLimits.StorageBytes)
    {
        if (capacity < 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        _root = Path.GetFullPath(root); _capacity = capacity;
        Directory.CreateDirectory(_root); CheckDirectory();
        foreach (var file in Directory.EnumerateFiles(_root, "*.part")) File.Delete(file);
    }
    private void CheckDirectory()
    {
        for (DirectoryInfo? directory = new(_root); directory is not null; directory = directory.Parent)
            if ((directory.Attributes & FileAttributes.ReparsePoint) != 0) throw new AttachmentException("unsafe_path");
    }
    public string PathFor(string id)
    {
        if (!AttachmentLimits.IsId(id)) throw new AttachmentException("invalid_id");
        CheckDirectory();
        string path = Path.Combine(_root, id + ".bin");
        if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new AttachmentException("unsafe_path");
        return path;
    }
    public bool Exists(AttachmentFile file) => File.Exists(PathFor(file.BlobId)) && new FileInfo(PathFor(file.BlobId)).Length == file.Size;
    public long UsedBytes { get { lock (_gate) return Directory.EnumerateFiles(_root).Sum(p => new FileInfo(p).Length); } }
    public FileStream Reserve(string id, long size)
    {
        if (size < 0 || size > AttachmentLimits.MaximumFileBytes) throw new AttachmentException("too_large");
        lock (_gate)
        {
            string path = PathFor(id) + ".part";
            if (UsedBytes > _capacity - size) throw new AttachmentException("storage_full");
            var file = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 65536, FileOptions.Asynchronous);
            try { file.SetLength(size); return file; }
            catch { file.Dispose(); File.Delete(path); throw; }
        }
    }
    public async Task<AttachmentFile> StageAsync(string path, CancellationToken cancellation = default)
    {
        if (Directory.Exists(path)) throw new AttachmentException("directory_not_supported");
        string name = Path.GetFileName(path);
        if (!AttachmentLimits.ValidName(name)) throw new AttachmentException("invalid_name");
        await using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await StageAsync(source, name, source.Length, cancellation).ConfigureAwait(false);
    }
    public async Task<AttachmentFile> StageAsync(Stream source, string name, long length, CancellationToken cancellation = default)
    {
        if (!AttachmentLimits.ValidName(name)) throw new AttachmentException("invalid_name");
        string id = Guid.NewGuid().ToString("N");
        try
        {
            string hash;
            await using (var target = Reserve(id, length))
            {
                using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                byte[] bytes = new byte[65536]; long written = 0; int count;
                while ((count = await source.ReadAsync(bytes, cancellation).ConfigureAwait(false)) != 0)
                {
                    if (written + count > length) throw new AttachmentException("source_changed");
                    await target.WriteAsync(bytes.AsMemory(0, count), cancellation).ConfigureAwait(false);
                    sha.AppendData(bytes, 0, count); written += count;
                }
                if (written != length) throw new AttachmentException("source_changed");
                await target.FlushAsync(cancellation).ConfigureAwait(false); target.Flush(true);
                hash = Convert.ToHexStringLower(sha.GetHashAndReset());
            }
            File.Move(PathFor(id) + ".part", PathFor(id));
            return new(id, name, length, hash);
        }
        catch { Delete(id); throw; }
    }
    public async Task CommitAsync(AttachmentFile file, CancellationToken cancellation)
    {
        string path = PathFor(file.BlobId) + ".part";
        await using (var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None, 65536, FileOptions.Asynchronous))
        {
            if (input.Length != file.Size || Convert.ToHexStringLower(await SHA256.HashDataAsync(input, cancellation).ConfigureAwait(false)) != file.Sha256)
                throw new AttachmentException("hash_mismatch");
        }
        cancellation.ThrowIfCancellationRequested(); File.Move(path, PathFor(file.BlobId), overwrite: false);
    }
    public void Delete(string id)
    {
        lock (_gate) { File.Delete(PathFor(id)); File.Delete(PathFor(id) + ".part"); }
    }
    public void Collect(IReadOnlySet<string> referenced)
    {
        lock (_gate)
            foreach (string path in Directory.EnumerateFiles(_root, "*.bin"))
            { string id = Path.GetFileNameWithoutExtension(path); if (AttachmentLimits.IsId(id) && !referenced.Contains(id)) File.Delete(path); }
    }
}
