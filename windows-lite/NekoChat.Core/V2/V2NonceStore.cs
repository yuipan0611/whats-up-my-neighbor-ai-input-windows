using System.Security.Cryptography;

namespace NekoChat.Core.V2;

/// <summary>Bounded RAM-only challenges. Public consumption accepts only domain objects returned by the real verifier.</summary>
public sealed class V2NonceStore
{
    private sealed record Entry(string Sender, long IssuedAt, LinkedListNode<string> Order);
    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _pending = new(StringComparer.Ordinal);
    private readonly LinkedList<string> _order = new();
    private readonly TimeProvider _clock;
    private readonly Func<byte[]> _entropy;
    private readonly int _capacity;
    private readonly TimeSpan _ttl;

    public V2NonceStore(TimeProvider? clock = null, int capacity = 512, TimeSpan? ttl = null)
        : this(clock ?? TimeProvider.System, capacity, ttl ?? TimeSpan.FromSeconds(60), () => RandomNumberGenerator.GetBytes(16)) { }
    internal V2NonceStore(TimeProvider clock, int capacity, TimeSpan ttl, Func<byte[]> entropy)
    {
        ArgumentNullException.ThrowIfNull(clock); ArgumentNullException.ThrowIfNull(entropy);
        if (capacity is < 1 or > 65536 || ttl <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(capacity));
        _clock = clock; _capacity = capacity; _ttl = ttl; _entropy = entropy;
    }
    public string Issue(string senderId)
    {
        if (!V2Format.IsPeerId(senderId)) throw new ArgumentException("Invalid sender id.", nameof(senderId));
        lock (_gate)
        {
            long now = _clock.GetTimestamp();
            foreach (string expired in _pending.Where(p => _clock.GetElapsedTime(p.Value.IssuedAt, now) >= _ttl).Select(p => p.Key).ToArray()) Remove(expired);
            string? nonce = null;
            for (int i = 0; i < 3; i++)
            {
                byte[] bytes = _entropy();
                if (bytes.Length != 16) throw new InvalidOperationException("Nonce entropy must contain16bytes.");
                string candidate = V2Format.ToBase64Url(bytes);
                if (!_pending.ContainsKey(candidate)) { nonce = candidate; break; }
            }
            if (nonce is null) throw new InvalidOperationException("Nonce allocation failed.");
            if (_pending.Count == _capacity) Remove(_order.First!.Value);
            var node = _order.AddLast(nonce); _pending.Add(nonce, new(senderId, now, node)); return nonce;
        }
    }
    public bool TryConsume(V2VerifiedMessage message)
    { ArgumentNullException.ThrowIfNull(message); return TryConsumeVerified(message.Nonce, message.SenderId); }
    public bool TryConsume(V2VerifiedReceipt receipt)
    { ArgumentNullException.ThrowIfNull(receipt); return TryConsumeVerified(receipt.Nonce, receipt.SenderId); }
    internal bool TryConsumeVerified(string nonce, string senderId)
    {
        lock (_gate)
        {
            if (!_pending.TryGetValue(nonce, out var entry) || entry.Sender != senderId || _clock.GetElapsedTime(entry.IssuedAt) >= _ttl) return false;
            Remove(nonce); return true;
        }
    }
    public int PendingCount { get { lock (_gate) return _pending.Count; } }
    private void Remove(string nonce) { var entry = _pending[nonce]; _order.Remove(entry.Order); _pending.Remove(nonce); }
}
