namespace NekoChat.Windows;

public partial class ChatWindow
{
    private sealed record RecipientProtocolHint(ChatRuntime Owner, long Generation);
    private readonly Dictionary<string, RecipientProtocolHint> _legacyRecipientHints = new(StringComparer.Ordinal);

    /// <summary>
    /// UI adapter entry for the classroom client: call only after CM-14's exact legacy hello response or a
    /// validated classroom hello. History rows and a legacy delivered reply never establish peer version.
    /// The transport still probes before every send; this hint never authorizes fallback or changes drafts.
    /// </summary>
    internal void ReportHelloProtocolHint(ChatRuntime owner, long observedGeneration, string endpoint, bool isLegacy)
    {
        Dispatcher.VerifyAccess();
        if (!ReferenceEquals(_runtime, owner) || owner.IsStopping || owner.WireGeneration != observedGeneration || _closingRequested) return;
        if (isLegacy) _legacyRecipientHints[endpoint] = new(owner, observedGeneration); else _legacyRecipientHints.Remove(endpoint);
        if (_selected?.Endpoint == endpoint) { UpdateHeader(); UpdateComposer(); }
    }

    internal void ResetRecipientProtocolHints()
    {
        Dispatcher.VerifyAccess(); _legacyRecipientHints.Clear();
        if (_initializedInbox) { UpdateHeader(); UpdateComposer(); }
    }

    private bool CurrentRecipientIsLegacy => _runtime is { } owner && _selected?.Endpoint is { } endpoint &&
        _legacyRecipientHints.TryGetValue(endpoint, out var hint) && ReferenceEquals(hint.Owner, owner) &&
        !owner.IsStopping && hint.Generation == owner.WireGeneration;

    private string LegacyIncomingLabel => _runtime?.ArchivedIdentityFixture == true ? "舊版・未驗證" : "舊版";
    private const string LegacyDeliveredStatus = "舊版・已送出；接收確認不代表對方已儲存或已讀。";
    internal void ShowLegacyDeliveredStatus() => SetStatus(LegacyDeliveredStatus);
}
