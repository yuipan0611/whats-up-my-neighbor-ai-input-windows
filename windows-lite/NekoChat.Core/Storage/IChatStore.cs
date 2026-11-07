namespace NekoChat.Core.Storage;

public interface IChatStore : IDisposable
{
    InboxSnapshot GetInboxSnapshot();
    TeacherFilePage ListTeacherFiles(long? beforeLocalSeq = null, int pageSize = 50);
    LocalIdentitySnapshot GetLocalIdentitySnapshot();
    bool ProvisionLocalIdentityIfCurrent(LocalIdentitySnapshot expected, string id, byte[] publicKey, string credentialRef, Func<bool> stillCurrent);
    ReplaceIdentityResult ReplaceLocalIdentityIfCurrent(LocalIdentitySnapshot expected, string newId, byte[] newPublicKey, string newCredentialRef, Func<bool> stillCurrent);
    /// <summary>Explicit lost-identity recovery only: atomically replace, suspend paired peers, turn outbound read receipts off.</summary>
    ReplaceIdentityResult RecoverLocalIdentityIfCurrent(LocalIdentitySnapshot expected, string newId, byte[] newPublicKey, string newCredentialRef, Func<bool> stillCurrent);
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
    event Action<string>? ClassroomCommunicationSucceeded;
    void RecordClassroomCommunicationSuccess(string sourceIp);
    bool IsSourceBlocked(string ip);
    IReadOnlyList<BlockedContact> GetBlockedContacts();
    RosterMutationResult SetSourceBlocked(string ip, bool blocked, string? expectedSnapshotToken = null);
    ReadReceiptPreferences GetReadReceiptPreferences();
    void AcknowledgeReadReceiptNotice();
    string? GetClassroomTeacherIp();
    RosterMutationResult ConfirmAutomaticContact(string entryId, string expectedSnapshotToken);
    RosterMutationResult SetClassroomTeacher(string entryId, string expectedSnapshotToken);
    RosterMutationResult SetClassroomTeacherByIp(string ip, string expectedSnapshotToken);
    RosterMutationResult ClearClassroomTeacher(string entryId, string expectedSnapshotToken);
    bool CanAutoReceiveAttachments(ClassroomPrincipalKey principal);
    ReadReceiptPolicy GetReadReceiptPolicy();
    ReadPolicyChange SetReadReceiptPolicy(bool enabled);
    VisibleReadResult MarkVisibleRead(string conversationId, IReadOnlyList<long> visibleLocalSeqs, VisibleReadContext context, Func<bool>? visibilityStillCurrent = null);
    IReadOnlyList<PendingReadReceipt> ListPendingReadReceipts(int limit = 50);
    bool CompleteReadReceipt(PendingReadReceipt receipt);
    IReadOnlyList<NekoChat.Core.Roster.RosterSnapshotRow> GetRosterSnapshot();
    RosterMutationResult SaveManualRosterEntry(string? entryId, RosterEdit edit, string expectedSnapshotToken);
    RosterSaveResult SaveManualRosterEntry(string? entryId, RosterEdit edit, string expectedSnapshotToken, LegacyMergeConsent? mergeConsent);
    RosterEditPreview PreviewManualRosterEntry(string? entryId, RosterEdit edit);
    RosterSendTarget ResolveRosterSendTarget(string rowId);
    RosterDeletePreview PreviewRosterDelete(string entryId);
    RosterMutationResult DeleteRosterEntry(string entryId, string expectedSnapshotToken, RosterDeleteConfirmation confirmation);
    IReadOnlyList<RosterImportMergeNotice> GetRosterImportMergeNotices(NekoChat.Core.Roster.RosterImportPreview preview);
    RosterApplyResult ApplyRosterPreview(NekoChat.Core.Roster.RosterImportPreview preview, IReadOnlyList<RosterImportMergeNotice>? confirmedMerges);
    ClassroomPrincipalKey? ResolveClassroomPrincipal(string sourceIp);
    ClassroomReceiveResult ReceiveClassroomMessage(string observedSourceIp, ClassroomIncoming message, Func<bool> stillCurrent);
    DraftState GetClassroomDraftState(ClassroomPrincipalKey principal);
    DraftSaveResult SaveClassroomDraft(ClassroomPrincipalKey principal, string rawText, long draftEpoch, long draftRevision);
    BeginSendResult BeginClassroomSend(ClassroomPrincipalKey principal, string rawText, long draftEpoch, long draftRevision, string kind = "text", string? reaction = null);
    BeginSendResult BeginClassroomRetry(string messageId, ClassroomPrincipalKey principal, string currentText, long draftEpoch, long draftRevision, string kind = "text", string? reaction = null);
    ClassroomPreparedSend? GetClassroomPreparedSend(string messageId);
    CompleteResult CompleteClassroomAttempt(string messageId, AttemptOutcome outcome, string? ackSourceIp = null, int? expectedTryCount = null);
    ClassroomRouteResult RecordClassroomRoute(string messageId, int tryCount, ClassroomRoute route, DateTime helloObservedUtc);
    CompleteResult CompleteClassroomLegacyDelivered(string messageId, string deliveredFromIp, int? expectedTryCount = null);
    CancelResult CancelClassroomAttempt(string messageId, int? expectedTryCount = null);
    ReceiptApplyResult ApplyClassroomReceipt(string sourceIp, string messageId, RemoteReceiptKind kind);
    VisibleReadResult MarkClassroomVisibleRead(ClassroomPrincipalKey principal, IReadOnlyList<long> visibleLocalSeqs, VisibleReadContext context, Func<bool>? visibilityStillCurrent = null);
    IReadOnlyList<PendingClassroomReceipt> ListPendingClassroomReceipts(int limit = 50, long? afterLocalSeq = null);
    bool CompleteClassroomReadReceipt(PendingClassroomReceipt receipt);
    ClassroomMessagePage ListClassroomMessages(ClassroomPrincipalKey principal, long? beforeLocalSeq = null, int pageSize = 50);
    IReadOnlyList<ClassroomConversationSummary> ListClassroomConversations(int limit = 200);
    ClassroomClearResult ClearClassroomConversation(ClassroomPrincipalKey principal);
    IReadOnlyList<ClassroomPendingMessage> ListClassroomPendingMessages(ClassroomPrincipalKey principal, int limit = 200, long? beforeSequence = null);
    LocalDeleteResult DeleteClassroomMessage(ClassroomPrincipalKey principal, long sequence);
    LocalDeleteResult DeleteClassroomPendingMessage(ClassroomPrincipalKey principal, string messageId, int expectedTryCount);
    LocalDeleteResult DeleteLegacyMessage(LegacyHistoryScope scope, long sequence);
    ClassroomInboxSnapshot GetClassroomInboxSnapshot();
    RosterMutationResult DeleteRosterEntry(string entryId, string expectedSnapshotToken);
    RosterMutationResult BindPairedRosterPeer(string entryId, string? peerId, string expectedSnapshotToken);
    RosterApplyResult ApplyRosterPreview(NekoChat.Core.Roster.RosterImportPreview preview);
    bool LegacyRosterImportCompleted();
    LegacyRosterImportResult ImportLegacyRoster(IReadOnlyList<LegacyRosterContact> contacts);
    IReadOnlyList<StoreBackupInfo> ListBackups();
    BackupMaintenanceResult MaintainBackups();
    IReadOnlyList<NekoChat.Core.Presence.PresenceTarget> GetPresenceTargets();
    NekoChat.Core.Presence.PresenceTarget? GetPresenceTarget(string rowId);
    PresenceWriteResult ApplyRosterPresenceEvidence(NekoChat.Core.Presence.PresenceWriteJob job, Func<bool> stillCurrent);
    PeerSecuritySnapshot GetPeerSecuritySnapshot(string peerId);
    /// <summary>Capture a candidate and its pin atomically; never grants trust. Expected snapshot and session guard are mandatory.</summary>
    PairingCandidateResult BeginPairingCandidateIfCurrent(PeerSecuritySnapshot expected, byte[] canonicalPublicKey, Func<bool> stillCurrent);
    PairingRosterTarget? CapturePairingRosterTarget(string rowId);
    bool IsPairingRosterTargetCurrent(PairingRosterTarget target);
    PairingCandidateResult BeginRosterPairingCandidateIfCurrent(PeerSecuritySnapshot expected, byte[] canonicalPublicKey, PairingRosterTarget target, Func<bool> stillCurrent);
    RosterPairingResult CompleteRosterPairingIfCurrent(PeerSecuritySnapshot expected, PairingRosterTarget target, Func<bool> stillCurrent);
    RosterPeerActionResult ChangeSelectedPeerIfCurrent(PairingRosterTarget target, PairingState? next, Func<bool> stillCurrent);
    PeerVerificationContext GetPeerVerificationContext(string peerId);
    LegacyReceiveContext GetLegacyReceiveContext(string sourceIp);
    LegacyReceiveResult ReceiveLegacyMessage(DirectMessage message, LegacyReceiveContext expected, Func<bool> stillCurrent);
    IReadOnlyList<LegacyHistorySummary> ListLegacyHistories(long? beforeLastLegacySeq = null, int limit = 200);
    LegacyMessagePage ListLegacyMessages(LegacyHistoryScope scope, long? beforeLegacySeq = null, int pageSize = 50);
    VisibleReadResult MarkLegacyVisibleRead(LegacyHistoryScope scope, IReadOnlyList<long> visibleLegacySeqs, VisibleReadContext context, Func<bool> stillCurrent);
    LegacyClearResult ClearLegacyHistory(LegacyHistoryScope scope);
    ReceiveResult ReceiveV2Message(NekoChat.Core.V2.V2VerifiedMessage message, PeerSecuritySnapshot expected, Func<bool> stillCurrent, string? sourceEndpoint = null);
    ReceiptApplyResult ApplyV2Receipt(NekoChat.Core.V2.V2VerifiedReceipt receipt, PeerSecuritySnapshot expected, Func<bool> stillCurrent);
    CompleteResult CompleteV2Send(NekoChat.Core.V2.V2VerifiedAck ack, PeerSecuritySnapshot expected, Func<bool> stillCurrent);
    bool CompleteV2ReadReceipt(PendingReadReceipt receipt, NekoChat.Core.V2.V2VerifiedAck ack, PeerSecuritySnapshot expected, Func<bool> stillCurrent);
    /// <param name="stillCurrent">Runs inside the write transaction under the store's writer lock: keep it fast, side-effect free and non-reentrant (never call this store from it). See <c>ChatStore.Write</c>.</param>
    PeerSecurityChange SetPairingStateIfCurrent(PeerSecuritySnapshot expected, PairingState next, Func<bool> stillCurrent);

    StoreDiagnostics GetDiagnostics();
}
