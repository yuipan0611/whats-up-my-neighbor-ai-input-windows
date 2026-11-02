using System.IO;
using System.Security.Cryptography;
using System.Text;
using NekoChat.Core.Identity;
using NekoChat.Core.V2;

namespace NekoChat.Windows.Identity;

internal interface IIdentityProtector
{
    byte[] Protect(byte[] privateKey, byte[] entropy);
    byte[] Unprotect(byte[] protectedKey, byte[] entropy);
}

/// <summary>The production factory supplies CurrentUser DPAPI. The injectable protector is for isolated file/lifecycle checks.</summary>
internal sealed class IdentityFileVault(string directory, IIdentityProtector protector) : IIdentityKeyVault
{
    private const string Prefix = "dpapi-v1:";
    private static ReadOnlySpan<byte> Magic => "NEKO-K1\0"u8;
    private readonly string _directory = Path.GetFullPath(directory);
    public IdentityCredential CreateCredential()
    {
        CheckPath(_directory); Directory.CreateDirectory(_directory); CheckPath(_directory);
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256); byte[] spki = key.ExportSubjectPublicKeyInfo();
        string reference = Prefix + Guid.NewGuid().ToString("D"), final = CredentialPath(reference);
        string temporary = final + "." + Guid.NewGuid().ToString("N") + ".tmp";
        byte[]? raw = null, encrypted = null; bool owned = false;
        try
        {
            raw = key.ExportPkcs8PrivateKey(); encrypted = protector.Protect(raw, Entropy(reference));
            if (encrypted.Length is < 1 or > 16384) throw new CryptographicException("Invalid protected identity size.");
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            { owned = true; file.Write(Magic); file.Write(encrypted); file.Flush(flushToDisk: true); }
            CheckPath(_directory); File.Move(temporary, final, overwrite: false);
            return new(V2Crypto.PeerId(spki), Convert.ToBase64String(spki), reference);
        }
        finally
        {
            if (raw is not null) CryptographicOperations.ZeroMemory(raw);
            if (encrypted is not null) CryptographicOperations.ZeroMemory(encrypted);
            // Only this invocation's unpublished temporary file; never remove a current or orphaned credential.
            try { if (owned) File.Delete(temporary); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }
    public ECDsa OpenSigningKey(string credentialRef)
    {
        string path = CredentialPath(credentialRef); CheckPath(path);
        byte[]? raw = null, encrypted = null; ECDsa? key = null;
        try
        {
            using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (file.Length < Magic.Length + 1 || file.Length > Magic.Length + 16384) throw new CryptographicException("Invalid protected identity file.");
                byte[] header = new byte[Magic.Length]; file.ReadExactly(header);
                if (!header.AsSpan().SequenceEqual(Magic)) throw new CryptographicException("Unsupported identity file.");
                encrypted = new byte[checked((int)file.Length - Magic.Length)]; file.ReadExactly(encrypted);
                if (file.ReadByte() != -1) throw new CryptographicException("Invalid protected identity file.");
            }
            raw = protector.Unprotect(encrypted, Entropy(credentialRef));
            if (raw.Length is < 64 or > 1024) throw new CryptographicException("Invalid identity key size.");
            key = ECDsa.Create(); key.ImportPkcs8PrivateKey(raw, out int consumed);
            using var canonical = V2Crypto.OpenCanonicalSigningKey(key.ExportSubjectPublicKeyInfo());
            if (consumed != raw.Length || canonical is null || !V2Crypto.Verify(canonical, "nekochat-identity-vault-v1"u8, V2Crypto.Sign(key, "nekochat-identity-vault-v1"u8)))
                throw new CryptographicException("Invalid identity key.");
            var result = key; key = null; return result;
        }
        finally
        {
            key?.Dispose(); if (raw is not null) CryptographicOperations.ZeroMemory(raw);
            if (encrypted is not null) CryptographicOperations.ZeroMemory(encrypted);
        }
    }
    public bool KeyExists(string credentialRef)
    {
        try { string path = CredentialPath(credentialRef); CheckPath(path); return File.Exists(path) && (File.GetAttributes(path) & FileAttributes.Directory) == 0; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { return false; }
    }
    public bool TryGetPublicKey(string credentialRef, out byte[] spki)
    {
        spki = [];
        try { using var key = OpenSigningKey(credentialRef); spki = key.ExportSubjectPublicKeyInfo(); return true; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or CryptographicException) { return false; }
    }
    public bool CanSign(string credentialRef)
    {
        try { using var key = OpenSigningKey(credentialRef); return V2Crypto.Verify(key, "nekochat-identity-probe-v1"u8, V2Crypto.Sign(key, "nekochat-identity-probe-v1"u8)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or CryptographicException) { return false; }
    }
    private string CredentialPath(string reference)
    {
        if (reference is null || !reference.StartsWith(Prefix, StringComparison.Ordinal) ||
            !V2Format.IsUuid(reference[Prefix.Length..])) throw new ArgumentException("Invalid identity reference.", nameof(reference));
        return Path.Combine(_directory, reference[Prefix.Length..] + ".dpapi");
    }
    private static byte[] Entropy(string reference) => Encoding.UTF8.GetBytes("nekochat-windowslite-identity-v1\0" + reference);
    private static void CheckPath(string path)
    {
        string? current = Path.GetFullPath(path);
        while (current is not null)
        {
            try { if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new IOException("Identity links are not supported."); }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
            current = Path.GetDirectoryName(current);
        }
    }
}
