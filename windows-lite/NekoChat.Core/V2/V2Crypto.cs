using System.Security.Cryptography;

namespace NekoChat.Core.V2;

public static class V2Crypto
{
    public static string PeerId(ReadOnlySpan<byte> canonicalSpki) => V2Format.ToBase64Url(SHA256.HashData(canonicalSpki));
    public static ECDsa? OpenCanonicalSigningKey(ReadOnlySpan<byte> spki)
    {
        if (spki.Length != 91) return null;
        ECDsa? key = null;
        try
        {
            key = ECDsa.Create(); key.ImportSubjectPublicKeyInfo(spki, out int consumed);
            var parameters = key.ExportParameters(false);
            if (consumed != spki.Length || parameters.Curve.Oid.Value != "1.2.840.10045.3.1.7" || parameters.Q.X?.Length != 32 || parameters.Q.Y?.Length != 32 ||
                !key.ExportSubjectPublicKeyInfo().AsSpan().SequenceEqual(spki)) return null;
            var result = key; key = null; return result;
        }
        catch (Exception e) when (e is CryptographicException or ArgumentException) { return null; }
        finally { key?.Dispose(); }
    }
    public static bool IsCanonicalKeyExchange(ReadOnlySpan<byte> spki)
    {
        if (spki.Length != 91) return false;
        try
        {
            using var key = ECDiffieHellman.Create(); key.ImportSubjectPublicKeyInfo(spki, out int consumed);
            var parameters = key.ExportParameters(false);
            return consumed == spki.Length && parameters.Curve.Oid.Value == "1.2.840.10045.3.1.7" && parameters.Q.X?.Length == 32 && parameters.Q.Y?.Length == 32 &&
                key.ExportSubjectPublicKeyInfo().AsSpan().SequenceEqual(spki);
        }
        catch (Exception e) when (e is CryptographicException or ArgumentException) { return false; }
    }
    public static bool Verify(ECDsa key, ReadOnlySpan<byte> input, ReadOnlySpan<byte> signature)
    {
        if (signature.Length != 64) return false;
        try { return key.VerifyData(input, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation); }
        catch (CryptographicException) { return false; }
    }
    /// <summary>The caller owns the private key and serializes concurrent access; signature bytes are never identifiers.</summary>
    public static byte[] Sign(ECDsa key, ReadOnlySpan<byte> input)
    {
        using var check = OpenCanonicalSigningKey(key.ExportSubjectPublicKeyInfo());
        if (check is null) throw new ArgumentException("A named P-256 key is required.", nameof(key));
        return key.SignData(input, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
    }
}
