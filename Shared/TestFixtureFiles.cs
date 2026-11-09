using System.IO;
using System.Security.Cryptography;

namespace NekoChat.TestFixtures;

// Test/smoke inspection only. The owner may keep SQLite/WAL or a journal open.
// Sharing permits the read; it does not freeze a concurrently changing file.
// Callers must control writes around before/after comparisons. Missing files and
// access failures deliberately propagate: never substitute an empty hash/snapshot.
internal static class TestFixtureFiles
{
    internal static FileStream OpenRead(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

    internal static byte[] ReadAllBytes(string path)
    {
        using var file = OpenRead(path);
        using var bytes = new MemoryStream();
        file.CopyTo(bytes);
        return bytes.ToArray();
    }

    internal static string Sha256(string path)
    {
        using var file = OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(file));
    }
}
