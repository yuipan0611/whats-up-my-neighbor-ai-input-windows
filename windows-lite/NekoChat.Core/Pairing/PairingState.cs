using System.Text.Json;
using NekoChat.Core.V2;

namespace NekoChat.Core.Pairing;

public enum PairingStage { Closed, Listening, Requested, Committed, SasShown, Waiting, Paired, Ended }

/// <summary>One user action for the exact actor/session/SAS that was rendered. It does not itself establish a peer's consent.</summary>
public sealed class PairingConsent
{
    internal PairingConsent(object owner, long epoch, string session, string peer, string sas)
    { Owner = owner; Epoch = epoch; SessionId = session; PeerId = peer; Sas = sas; }
    internal object Owner { get; }
    internal long Epoch { get; }
    internal int Consumed;
    public string SessionId { get; }
    public string PeerId { get; }
    public string Sas { get; }
}
public sealed record PairingView(PairingStage Stage, string Reason = "", string? SessionId = null, string? PeerId = null,
    string? Sas = null, bool LocalConfirmed = false, bool RemoteConfirmed = false, PairingConsent? Consent = null,
    TimeSpan Remaining = default, bool MayBeAsymmetric = false);

public static class PairingTiming
{
    public static readonly TimeSpan Window = TimeSpan.FromSeconds(120), Session = TimeSpan.FromSeconds(120), Idle = TimeSpan.FromSeconds(20),
        ConfirmSpacing = TimeSpan.FromSeconds(1), Poll = TimeSpan.FromSeconds(5), History = TimeSpan.FromMinutes(10);
    public const int MaxReveals = 3, MaxRequests = 200, MaxHistory = 64;
}
internal static class PairingResponses
{
    internal static V2HttpResponse Ok(byte[] bytes) => new(200, bytes.ToArray());
    internal static V2HttpResponse Error(string code) => new(code switch { "pair_unavailable" => 403, "pair_state" or "pair_cancelled" => 409,
        "storage_unavailable" or "receiver_not_ready" => 503, _ => 400 },
        JsonSerializer.SerializeToUtf8Bytes(new { protocol = "nekochat", version = 2, kind = "error", code }));
}
