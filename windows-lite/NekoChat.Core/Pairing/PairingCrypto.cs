using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using NekoChat.Core.V2;

namespace NekoChat.Core.Pairing;

public enum PairingRole { Initiator, Responder }

/// <summary>The custom N002P commitment/transcript formulas. This is not a Bluetooth/ZRTP implementation or security certification.</summary>
public static class PairingCrypto
{
    public static byte[] Commitment(ReadOnlySpan<byte> session, ReadOnlySpan<byte> initiator, ReadOnlySpan<byte> responder, ReadOnlySpan<byte> nb)
    {
        Require(session.Length == 16 && nb.Length == 16); Keys(initiator, responder);
        return Hash("nekochat-v2 pair commit", session, initiator, responder, nb);
    }
    public static byte[] Transcript(ReadOnlySpan<byte> session, ReadOnlySpan<byte> initiator, ReadOnlySpan<byte> responder, ReadOnlySpan<byte> na, ReadOnlySpan<byte> nb)
    {
        Require(session.Length == 16 && na.Length == 16 && nb.Length == 16); Keys(initiator, responder);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData("nekochat-v2 pair transcript"u8); hash.AppendData(session); hash.AppendData(initiator); hash.AppendData(responder); hash.AppendData(na); hash.AppendData(nb);
        return hash.GetHashAndReset();
    }
    public static string Sas(ReadOnlySpan<byte> transcript)
    {
        Require(transcript.Length == 32);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); hash.AppendData("nekochat-v2 pair sas"u8); hash.AppendData(transcript);
        return (BinaryPrimitives.ReadUInt64BigEndian(hash.GetHashAndReset()) % 1000000).ToString("D6", CultureInfo.InvariantCulture);
    }
    public static byte[] ConfirmationInput(PairingRole role, ReadOnlySpan<byte> transcript)
    {
        Require(transcript.Length == 32);
        string direction = role switch { PairingRole.Initiator => "initiator", PairingRole.Responder => "responder", _ => throw new ArgumentOutOfRangeException(nameof(role)) };
        return Encoding.ASCII.GetBytes("nekochat-v2\npair.confirm\n" + direction + "\n" + Convert.ToHexString(transcript).ToLowerInvariant());
    }
    public static bool Verify(ReadOnlySpan<byte> publicSpki, PairingRole role, ReadOnlySpan<byte> transcript, ReadOnlySpan<byte> signature)
    {
        using var key = V2Crypto.OpenCanonicalSigningKey(publicSpki);
        return key is not null && transcript.Length == 32 && V2Crypto.Verify(key, ConfirmationInput(role, transcript), signature);
    }
    public static bool MatchesCommitment(ReadOnlySpan<byte> expected, ReadOnlySpan<byte> actual) =>
        expected.Length == 32 && actual.Length == 32 && CryptographicOperations.FixedTimeEquals(expected, actual);
    private static byte[] Hash(string label, ReadOnlySpan<byte> a, ReadOnlySpan<byte> b, ReadOnlySpan<byte> c, ReadOnlySpan<byte> d)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Encoding.ASCII.GetBytes(label)); hash.AppendData(a); hash.AppendData(b); hash.AppendData(c); hash.AppendData(d); return hash.GetHashAndReset();
    }
    private static void Keys(ReadOnlySpan<byte> initiator, ReadOnlySpan<byte> responder)
    { using var i = V2Crypto.OpenCanonicalSigningKey(initiator); using var r = V2Crypto.OpenCanonicalSigningKey(responder); Require(i is not null && r is not null); }
    private static void Require(bool condition) { if (!condition) throw new ArgumentException("Invalid pairing formula input."); }
}
