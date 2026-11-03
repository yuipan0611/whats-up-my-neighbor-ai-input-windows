using System.Security.Cryptography;
using NekoChat.Core.V2;

namespace NekoChat.Core.Classroom.Wire;

public enum ClassroomSessionAdd { Issued, Collision }

/// <summary>
/// RAM-only session table (contract §6). TTL 60 s with an inclusive-expiry boundary (age ≥ TTL is expired), global capacity 512, 16 per source
/// (a full source loses its own oldest first), one-shot atomic consume bound to the issuing source, whole-table <see cref="Clear"/>.
/// Nothing is written to disk and nothing survives a restart. A wrong source neither succeeds nor burns the session.
/// </summary>
public sealed class ClassroomSessionTable : IClassroomSessionLookup
{
    private readonly object _gate = new();
    private readonly List<Entry> _entries = new();    // insertion order = age order
    private readonly TimeProvider _clock;
    private readonly int _capacity, _perSource;
    private readonly TimeSpan _ttl;
    private readonly Func<string> _newId;
    private readonly record struct Entry(string Id, string Source, long Issued);

    public ClassroomSessionTable(TimeProvider? clock = null, int capacity = ClassroomWire.SessionCapacity, int perSource = ClassroomWire.SessionPerSource, TimeSpan? ttl = null, Func<string>? newId = null)
    {
        if (capacity < 1 || perSource < 1) throw new ArgumentOutOfRangeException(nameof(capacity));
        _clock = clock ?? TimeProvider.System; _capacity = capacity; _perSource = perSource; _ttl = ttl ?? ClassroomWire.SessionTtl;
        if (_ttl <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(ttl));
        _newId = newId ?? (() => V2Format.ToBase64Url(RandomNumberGenerator.GetBytes(16)));
    }

    public int Count { get { lock (_gate) return _entries.Count; } }

    private bool Expired(Entry e, long now) => _clock.GetElapsedTime(e.Issued, now) >= _ttl;
    private void Purge(long now) => _entries.RemoveAll(e => Expired(e, now));

    /// <summary>Issues a fresh random session for <paramref name="source"/>. Entropy collisions retry a bounded number of times.</summary>
    public string Issue(string source)
    {
        for (int attempt = 0; attempt < 4; attempt++)
        {
            string id = _newId();
            if (Add(id, source) == ClassroomSessionAdd.Issued) return id;
        }
        throw new InvalidOperationException("Session identifier source is not producing distinct values.");
    }

    /// <summary>Explicit-id form (vectors, tests). An existing id is never overwritten.</summary>
    public ClassroomSessionAdd Add(string id, string source)
    {
        if (!V2Format.IsNonce22(id)) throw new ArgumentException("Invalid session id.", nameof(id));
        ArgumentException.ThrowIfNullOrEmpty(source);
        lock (_gate)
        {
            long now = _clock.GetTimestamp(); Purge(now);
            if (_entries.Any(e => e.Id == id)) return ClassroomSessionAdd.Collision;
            while (_entries.Count(e => e.Source == source) >= _perSource) _entries.RemoveAt(_entries.FindIndex(e => e.Source == source));
            while (_entries.Count >= _capacity) _entries.RemoveAt(0);
            _entries.Add(new(id, source, now)); return ClassroomSessionAdd.Issued;
        }
    }

    /// <summary>Atomic compare-and-delete: true exactly once, and only for the source it was issued to.</summary>
    public bool TryConsume(string id, string source)
    {
        lock (_gate)
        {
            Purge(_clock.GetTimestamp());
            int index = _entries.FindIndex(e => e.Id == id && e.Source == source);
            if (index < 0) return false;
            _entries.RemoveAt(index); return true;
        }
    }

    /// <summary>Read-only view for validation; does not consume. Expired entries are reported as absent.</summary>
    public bool TryGet(string session, out ClassroomSessionState state)
    {
        lock (_gate)
        {
            long now = _clock.GetTimestamp(); Purge(now);
            int index = _entries.FindIndex(e => e.Id == session);
            if (index < 0) { state = default; return false; }
            state = new(_entries[index].Source, _clock.GetElapsedTime(_entries[index].Issued, now)); return true;
        }
    }

    /// <summary>Listener stop, ready toggle, resume, restart (contract §6.8).</summary>
    public void Clear() { lock (_gate) _entries.Clear(); }
}
