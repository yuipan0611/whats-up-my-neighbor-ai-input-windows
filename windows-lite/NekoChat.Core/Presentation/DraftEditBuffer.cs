using NekoChat.Core.Storage;

namespace NekoChat.Core.Presentation;

public sealed class DraftWriteSnapshot
{
    internal object Owner { get; }
    internal long Generation { get; }
    public string Text { get; }
    public long Epoch { get; }
    public long Revision { get; }
    public long EditNumber { get; }
    internal DraftWriteSnapshot(object owner, long generation, string text, long epoch, long revision, long edit)
    { Owner = owner; Generation = generation; Text = text; Epoch = epoch; Revision = revision; EditNumber = edit; }
    public DraftSaveResult Persist(IChatStore store, string conversationId)
    {
        ArgumentNullException.ThrowIfNull(store); var current = store.GetDraftState(conversationId);
        if (current is not null && current.Epoch == Epoch && current.Revision == Revision && (current.RawText ?? "") == Text)
            return new(true, "already-current", Epoch, Revision);
        return store.SaveDraft(conversationId, Text, Epoch, Revision);
    }
}
public sealed class DraftClearTicket
{
    internal object Owner { get; }
    internal long Generation { get; }
    internal DraftClearTicket(object owner, long generation) { Owner = owner; Generation = generation; }
}
public sealed record DraftAckUpdate(bool Applied, DraftWriteSnapshot? PreserveEdit = null);

/// <summary>User edits and durable epochs are separate. A stale callback never rebases an old draft automatically.</summary>
public sealed class DraftEditBuffer
{
    private readonly object _gate = new();
    private long _generation, _edit, _epoch, _revision;
    private string _text;
    private bool _dirty, _clearing;
    public string Text { get { lock (_gate) return _text; } }
    public bool Dirty { get { lock (_gate) return _dirty; } }
    public bool Clearing { get { lock (_gate) return _clearing; } }
    public DraftEditBuffer(DraftState state)
    { ArgumentNullException.ThrowIfNull(state); _text = state.RawText ?? ""; _epoch = state.Epoch; _revision = state.Revision; }
    public DraftWriteSnapshot Edit(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        lock (_gate)
        {
            if (_clearing) throw new InvalidOperationException("Draft clear is in progress.");
            if (_text != text) { _text = text; _edit = checked(_edit + 1); _revision = checked(_revision + 1); _dirty = true; }
            return CaptureLocked();
        }
    }
    public DraftWriteSnapshot Capture() { lock (_gate) return CaptureLocked(); }
    public bool IsCurrent(DraftWriteSnapshot ticket)
    { lock (_gate) return Owns(ticket) && ticket.EditNumber == _edit && ticket.Revision == _revision && !_clearing; }
    public bool CompleteWrite(DraftWriteSnapshot ticket, DraftSaveResult result)
    {
        ArgumentNullException.ThrowIfNull(ticket); ArgumentNullException.ThrowIfNull(result);
        lock (_gate)
        {
            if (!Owns(ticket) || ticket.EditNumber != _edit || ticket.Revision != _revision || _clearing) return false;
            if (result.Saved && result.CurrentEpoch == ticket.Epoch && result.CurrentRevision == ticket.Revision) _dirty = false;
            return !_dirty;
        }
    }
    /// <summary>Only call after this exact manual send has a verified persisted ACK that actually cleared its draft.</summary>
    public DraftAckUpdate ApplyOwnAckClear(DraftWriteSnapshot sent, DraftState fresh)
    {
        ArgumentNullException.ThrowIfNull(sent); ArgumentNullException.ThrowIfNull(fresh);
        lock (_gate)
        {
            if (!Owns(sent) || _clearing || sent.Epoch == long.MaxValue || fresh.Epoch != sent.Epoch + 1 || fresh.Revision != sent.Revision || fresh.RawText is not null)
                return new(false);
            bool editedAfterSend = _edit > sent.EditNumber; _epoch = fresh.Epoch; _generation = checked(_generation + 1);
            if (!editedAfterSend) { _text = ""; _revision = fresh.Revision; _dirty = false; return new(true); }
            _revision = checked(Math.Max(_revision, fresh.Revision) + 1); _dirty = true;
            return new(true, CaptureLocked());
        }
    }
    public DraftClearTicket BeginClear()
    {
        lock (_gate) { if (_clearing) throw new InvalidOperationException("Draft clear is in progress."); _generation = checked(_generation + 1); _clearing = true; return new(this, _generation); }
    }
    public bool CompleteClear(DraftClearTicket ticket, DraftState? fresh)
    {
        ArgumentNullException.ThrowIfNull(ticket);
        lock (_gate)
        {
            if (!ReferenceEquals(ticket.Owner, this) || ticket.Generation != _generation || !_clearing) return false;
            _clearing = false;
            if (fresh is null) return false;
            _text = fresh.RawText ?? ""; _epoch = fresh.Epoch; _revision = fresh.Revision; _dirty = false; return true;
        }
    }
    private bool Owns(DraftWriteSnapshot ticket) => ReferenceEquals(ticket.Owner, this) && ticket.Generation == _generation && ticket.Epoch == _epoch;
    private DraftWriteSnapshot CaptureLocked() => new(this, _generation, _text, _epoch, _revision, _edit);
}
