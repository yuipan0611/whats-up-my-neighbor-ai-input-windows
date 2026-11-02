namespace NekoChat.Core.Storage;

public interface IChatStore : IDisposable
{
    /// <summary>Creates the identity row if absent; if present returns its id untouched (never overwrites). Private keys are never passed in.</summary>
    string EnsureLocalIdentity(string? id = null, byte[]? publicKey = null, string? credentialRef = null);

    /// <summary>Human-confirmed flows only. Retires the current identity, supersedes open attempts, drops unsent receipts.</summary>
    ReplaceIdentityResult ReplaceLocalIdentity(string newId, byte[]? newPublicKey, string? newCredentialRef);

    /// <summary>Read-only. Reports; never rebuilds an identity.</summary>
    IdentityCheckResult CheckLocalIdentity(IIdentityKeyProbe probe, Func<byte[], string>? deriveId = null);

    /// <summary>pairingState is required (no default). Existing peers are returned unchanged (idempotent).</summary>
    RegisterPeerResult RegisterPeer(string peerId, PairingState pairingState, byte[]? publicKey = null);

    void SetPairingState(string peerId, PairingState pairingState);

    /// <summary>Handshake/verified-signature path: at most one write per 30 s per peer, with immediate-write exceptions.</summary>
    PeerSeenResult TouchPeerSeen(string peerId, string sourceEndpoint);

    /// <summary>Caller has verified identity, signature and nonce. Returns Stored only after the transaction commits.</summary>
    ReceiveResult ReceiveVerifiedMessage(IncomingMessage message);
    int GetUnreadCount(string conversationId);
    PresenceWriteResult ApplyPresenceEvidence(string peerId, string sourceEndpoint, bool installEvidenceAppVerified);

    DraftSaveResult SaveDraft(string conversationId, string rawText, long draftEpoch, long draftRevision);
    DraftState? GetDraftState(string conversationId);
    BeginSendResult BeginManualSend(string conversationId, string rawText, long draftEpoch, long draftRevision, string kind = "text", string? reaction = null);
    BeginSendResult BeginManualRetry(string messageId, string currentText, string currentConversationId, long draftEpoch, long draftRevision, string kind = "text", string? reaction = null);
    PreparedSend? GetPreparedSend(string messageId);
    CompleteResult CompleteAttempt(string messageId, AttemptOutcome outcome, int? expectedTryCount = null);
    CancelResult CancelAttempt(string messageId, int? expectedTryCount = null);
    ReceiptApplyResult ApplyRemoteReceipt(string verifiedPeerId, string messageId, RemoteReceiptKind kind, string? originalSenderId = null);
    MessagePage ListMessages(string conversationId, long? beforeLocalSeq = null, int pageSize = 50);
    ClearResult ClearConversation(string conversationId);
    ReadReceiptPolicy GetReadReceiptPolicy();
    ReadPolicyChange SetReadReceiptPolicy(bool enabled);
    VisibleReadResult MarkVisibleRead(string conversationId, IReadOnlyList<long> visibleLocalSeqs, VisibleReadContext context, Func<bool>? visibilityStillCurrent = null);
    IReadOnlyList<PendingReadReceipt> ListPendingReadReceipts(int limit = 50);
    bool CompleteReadReceipt(PendingReadReceipt receipt);
    IReadOnlyList<NekoChat.Core.Roster.RosterSnapshotRow> GetRosterSnapshot();
    RosterMutationResult SaveManualRosterEntry(string? entryId, RosterEdit edit, string expectedSnapshotToken);
    RosterMutationResult DeleteRosterEntry(string entryId, string expectedSnapshotToken);
    RosterMutationResult BindPairedRosterPeer(string entryId, string? peerId, string expectedSnapshotToken);
    RosterApplyResult ApplyRosterPreview(NekoChat.Core.Roster.RosterImportPreview preview);

    StoreDiagnostics GetDiagnostics();
}
