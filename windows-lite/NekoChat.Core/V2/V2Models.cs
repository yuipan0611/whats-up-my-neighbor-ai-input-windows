namespace NekoChat.Core.V2;

public sealed record V2Peer(string? State, string? PublicSpkiBase64);
public sealed record V2HelloContext(string ClientNonce, V2Peer Peer, IReadOnlyCollection<string> RequiredCapabilities);
public sealed record V2IncomingContext(string LocalRecipientId, V2Peer Peer);
public sealed record V2AckContext(string ExpectedMessageId, string ExpectedSenderId, string PinnedReceiverSpkiBase64, string? ExpectedFingerprint = null);
public sealed record V2Verification<T>(string Code, T? Value = null) where T : class
{ public bool Success => Code == "ok" && Value is not null; }
public sealed record V2HelloRequest(string SenderId, string ClientNonce);
public sealed class V2HelloInfo
{
    public string ReceiverId { get; }
    public string PublicSpkiBase64 { get; }
    public string Nonce { get; }
    public IReadOnlyList<string> Capabilities { get; }
    public int MaxTextBytes { get; }
    public string? KeyExchangeSpkiBase64 { get; }
    public string EvidenceLevel { get; }
    internal V2HelloInfo(string receiver, string spki, string nonce, string[] capabilities, int max, string? kex, bool paired)
    { ReceiverId = receiver; PublicSpkiBase64 = spki; Nonce = nonce; Capabilities = Array.AsReadOnly(capabilities.ToArray()); MaxTextBytes = max; KeyExchangeSpkiBase64 = kex; EvidenceLevel = paired ? "E2" : "E1"; }
}
public sealed class V2VerifiedMessage
{
    public string MessageId { get; }
    public string SenderId { get; }
    public string RecipientId { get; }
    public string Nonce { get; }
    public string Text { get; }
    public string? Reaction { get; }
    public string ContentFingerprint { get; }
    internal V2VerifiedMessage(string id, string sender, string recipient, string nonce, string text, string? reaction)
    { MessageId = id; SenderId = sender; RecipientId = recipient; Nonce = nonce; Text = text; Reaction = reaction; ContentFingerprint = V2Signing.Fingerprint(recipient, text, reaction); }
}
public sealed class V2VerifiedReceipt
{
    public string MessageId { get; }
    public string SenderId { get; }
    public string RecipientId { get; }
    public string Nonce { get; }
    internal V2VerifiedReceipt(string id, string sender, string recipient, string nonce)
    { MessageId = id; SenderId = sender; RecipientId = recipient; Nonce = nonce; }
}
public sealed class V2VerifiedAck
{
    public string Status { get; }
    public string MessageId { get; }
    public string SenderId { get; }
    public string ReceiverId { get; }
    public string? ContentFingerprint { get; }
    internal V2VerifiedAck(string status, string id, string sender, string receiver, string? fp)
    { Status = status; MessageId = id; SenderId = sender; ReceiverId = receiver; ContentFingerprint = fp; }
}
