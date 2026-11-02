-- 全部在 migration 1 的同一個 BEGIN IMMEDIATE 交易內執行；最後 PRAGMA user_version = 1。

CREATE TABLE SchemaVersion(              -- 稽核紀錄；權威值是 PRAGMA user_version
  version     INTEGER PRIMARY KEY,
  appliedUtc  TEXT NOT NULL,
  appVersion  TEXT NOT NULL
) STRICT;

CREATE TABLE LocalIdentity(              -- 每個資料區一份（現行身分）
  singleton     INTEGER PRIMARY KEY CHECK (singleton = 1),
  id            TEXT NOT NULL UNIQUE CHECK (length(id) BETWEEN 1 AND 64 AND id NOT GLOB '*[^A-Za-z0-9_-]*'),
  publicKey     BLOB,                    -- 本機公鑰 SPKI（公開資訊）。D-04 之前為 NULL
  credentialRef TEXT,                    -- 私鑰保管「參照」。★私鑰本身絕不進 DB
  createdUtc    TEXT NOT NULL
) STRICT;

CREATE TABLE LocalIdentityHistory(       -- ★v4：被 ReplaceLocalIdentity 退役的身分（只增不改不刪）
  retiredSeq INTEGER PRIMARY KEY AUTOINCREMENT,
  id         TEXT NOT NULL UNIQUE CHECK (length(id) BETWEEN 1 AND 64 AND id NOT GLOB '*[^A-Za-z0-9_-]*'),   -- UNIQUE：退役 id 永不再當現行 id
  publicKey  BLOB,
  createdUtc TEXT NOT NULL,
  retiredUtc TEXT NOT NULL,
  reason     TEXT NOT NULL CHECK (length(reason) BETWEEN 1 AND 32)      -- 'replaced'
) STRICT;
CREATE TRIGGER LocalIdentityHistory_no_update BEFORE UPDATE ON LocalIdentityHistory BEGIN SELECT RAISE(ABORT, 'LocalIdentityHistory is append-only'); END;
CREATE TRIGGER LocalIdentityHistory_no_delete BEFORE DELETE ON LocalIdentityHistory BEGIN SELECT RAISE(ABORT, 'LocalIdentityHistory is append-only'); END;

CREATE TRIGGER LocalIdentity_no_reuse_ins BEFORE INSERT ON LocalIdentity
WHEN EXISTS (SELECT 1 FROM LocalIdentityHistory WHERE id = NEW.id)
BEGIN SELECT RAISE(ABORT, 'Retired identity id cannot become current again'); END;
CREATE TRIGGER LocalIdentity_no_reuse_upd BEFORE UPDATE OF id ON LocalIdentity
WHEN EXISTS (SELECT 1 FROM LocalIdentityHistory WHERE id = NEW.id)
BEGIN SELECT RAISE(ABORT, 'Retired identity id cannot become current again'); END;

CREATE TABLE Peer(                       -- 存在於此表且 pairingState='paired' 才算可信；否則 UntrustedPeer
  id               TEXT PRIMARY KEY CHECK (length(id) BETWEEN 1 AND 64 AND id NOT GLOB '*[^A-Za-z0-9_-]*'),
  publicKey        BLOB,                 -- 釘住的對方公鑰 SPKI（P1 的 pin）；設定後不可改（見 Peer_pin_immutable）
  pairingState     TEXT NOT NULL CHECK (length(pairingState) BETWEEN 1 AND 16),   -- ★v4：無預設值。集合 unpaired|pending|paired|suspended|revoked 由程式驗證；讀到未知值一律視為不可信
  pairedUtc        TEXT,
  certificate      BLOB,                 -- 預留給 P2；P1 一律 NULL
  verificationRef  TEXT,
  lastSeenEndpoint TEXT,                 -- 最近通過簽章驗證的來源位址字串（映像衝突偵測；不當身分）
  lastSeenUtc      TEXT,
  installEvidence    TEXT CHECK (installEvidence IS NULL OR length(installEvidence) BETWEEN 1 AND 24),
  installEvidenceUtc TEXT,
  createdUtc       TEXT NOT NULL
) STRICT;
CREATE TRIGGER Peer_pin_immutable BEFORE UPDATE OF publicKey ON Peer
WHEN OLD.publicKey IS NOT NULL AND NEW.publicKey IS NOT OLD.publicKey
BEGIN SELECT RAISE(ABORT, 'Peer pinned key is immutable'); END;

CREATE TABLE RosterEntry(                -- v1 只建表，N001 不提供 API（N003 才用）
  id            TEXT PRIMARY KEY CHECK (length(id) BETWEEN 1 AND 64),
  displayName   TEXT,
  studentNumber TEXT CHECK (studentNumber IS NULL OR (studentNumber <> '' AND length(studentNumber) <= 12 AND studentNumber NOT GLOB '*[^0-9]*')),
  studentNumberKey TEXT GENERATED ALWAYS AS (CASE WHEN studentNumber IS NULL THEN NULL WHEN ltrim(studentNumber,'0') = '' THEN '0' ELSE ltrim(studentNumber,'0') END) STORED,
  isTeacher     INTEGER NOT NULL DEFAULT 0 CHECK (isTeacher IN (0,1)),
  peerId        TEXT REFERENCES Peer(id) ON DELETE SET NULL,
  endpoint      TEXT CHECK (endpoint IS NULL OR (endpoint <> '' AND length(endpoint) <= 15 AND endpoint NOT GLOB '*[^0-9.]*')),
  source        TEXT CHECK (source IS NULL OR length(source) BETWEEN 1 AND 24),
  importBatchId TEXT
) STRICT;
CREATE UNIQUE INDEX RosterEntry_student  ON RosterEntry(studentNumberKey) WHERE studentNumberKey IS NOT NULL;
CREATE UNIQUE INDEX RosterEntry_endpoint ON RosterEntry(endpoint)         WHERE endpoint IS NOT NULL;
CREATE INDEX RosterEntry_peer ON RosterEntry(peerId) WHERE peerId IS NOT NULL;

CREATE TABLE Conversation(               -- M1 一對一：peer 唯一
  id            TEXT PRIMARY KEY CHECK (length(id) BETWEEN 1 AND 64),
  peerId        TEXT NOT NULL UNIQUE REFERENCES Peer(id) ON DELETE RESTRICT,
  draftEpoch    INTEGER NOT NULL DEFAULT 0 CHECK (draftEpoch >= 0),       -- ★v4：草稿「世代」。ACK 清稿與 ClearConversation 時 +1，使先前發出的存稿全部失效
  draftRevision INTEGER NOT NULL DEFAULT 0 CHECK (draftRevision >= 0),    -- ★v4：已接受的最高存稿 revision（單調、跨世代不歸零；草稿被刪也保留）
  createdUtc    TEXT NOT NULL
) STRICT;
CREATE TRIGGER Conversation_draftguard_monotonic
BEFORE UPDATE OF draftEpoch, draftRevision ON Conversation
WHEN NEW.draftEpoch < OLD.draftEpoch OR NEW.draftRevision < OLD.draftRevision
BEGIN SELECT RAISE(ABORT, 'Draft guard must not move backwards'); END;
CREATE TRIGGER Conversation_no_delete BEFORE DELETE ON Conversation   -- 刪列會連同草稿守衛一起消失；v1 沒有任何刪除對話的功能
BEGIN SELECT RAISE(ABORT, 'Conversation rows are never deleted (draft guard lives here)'); END;

CREATE TABLE Message(
  localSeq          INTEGER PRIMARY KEY AUTOINCREMENT,
  conversationId    TEXT NOT NULL REFERENCES Conversation(id) ON DELETE CASCADE,
  direction         TEXT NOT NULL CHECK (direction IN ('incoming','outgoing')),
  senderId          TEXT NOT NULL,                       -- incoming=Peer.id；outgoing=發送當下的 LocalIdentity.id（身分替換後仍保留原值）
  recipientId       TEXT NOT NULL,
  messageId         TEXT NOT NULL CHECK (length(messageId) BETWEEN 1 AND 64),
  rawText           TEXT NOT NULL CHECK (length(rawText) > 0),
  kind              TEXT NOT NULL DEFAULT 'text' CHECK (length(kind) > 0),
  reaction          TEXT,
  fingerprintAlg    TEXT NOT NULL,
  contentFingerprint BLOB NOT NULL CHECK (length(contentFingerprint) = 32),
  createdUtc        TEXT NOT NULL,
  localReadAt       TEXT,
  remoteStoredAt    TEXT,
  remoteReadAt      TEXT,
  UNIQUE (senderId, messageId),
  CHECK (direction = 'outgoing' OR (remoteStoredAt IS NULL AND remoteReadAt IS NULL)),
  CHECK (direction = 'incoming' OR localReadAt IS NULL)
) STRICT;
CREATE INDEX Message_conv_seq ON Message(conversationId, localSeq);
CREATE INDEX Message_unread   ON Message(conversationId) WHERE direction = 'incoming' AND localReadAt IS NULL;

CREATE TRIGGER Message_immutable
BEFORE UPDATE OF localSeq, conversationId, direction, senderId, recipientId, messageId,
                 rawText, kind, reaction, fingerprintAlg, contentFingerprint, createdUtc ON Message
BEGIN SELECT RAISE(ABORT, 'Message content is immutable'); END;
CREATE TRIGGER Message_set_once
BEFORE UPDATE OF localReadAt, remoteStoredAt, remoteReadAt ON Message
WHEN (OLD.localReadAt    IS NOT NULL AND NEW.localReadAt    IS NOT OLD.localReadAt)
  OR (OLD.remoteStoredAt IS NOT NULL AND NEW.remoteStoredAt IS NOT OLD.remoteStoredAt)
  OR (OLD.remoteReadAt   IS NOT NULL AND NEW.remoteReadAt   IS NOT OLD.remoteReadAt)
BEGIN SELECT RAISE(ABORT, 'Message timestamps are set-once'); END;

CREATE TABLE Draft(                      -- 只放目前草稿文字；版本守衛在 Conversation，刪列不會讓守衛消失
  conversationId TEXT PRIMARY KEY REFERENCES Conversation(id) ON DELETE CASCADE,
  rawText        TEXT NOT NULL CHECK (length(rawText) > 0),                  -- 清空草稿＝刪列（守衛仍推進）
  updatedUtc     TEXT NOT NULL
) STRICT;

CREATE TABLE ManualSendAttempt(          -- 不是佇列：沒有任何背景程式會讀它來發訊（D-02）
  messageId          TEXT PRIMARY KEY CHECK (length(messageId) BETWEEN 1 AND 64),
  conversationId     TEXT NOT NULL REFERENCES Conversation(id) ON DELETE CASCADE,
  senderId           TEXT NOT NULL CHECK (length(senderId) BETWEEN 1 AND 64 AND senderId NOT GLOB '*[^A-Za-z0-9_-]*'),  -- ★v4：Begin 當下的 LocalIdentity.id，不可變
  recipientId        TEXT NOT NULL REFERENCES Peer(id) ON DELETE RESTRICT,
  rawSnapshot        TEXT NOT NULL CHECK (length(rawSnapshot) > 0),
  kind               TEXT NOT NULL DEFAULT 'text' CHECK (length(kind) > 0),
  reaction           TEXT,
  fingerprintAlg     TEXT NOT NULL,
  contentFingerprint BLOB NOT NULL CHECK (length(contentFingerprint) = 32),
  draftEpoch         INTEGER NOT NULL CHECK (draftEpoch >= 0),               -- ★v4：這次嘗試所對應的草稿（世代，revision）；只有人按重送時可更新
  draftRevision      INTEGER NOT NULL CHECK (draftRevision >= 0),
  attemptState       TEXT NOT NULL CHECK (attemptState IN ('sending','unconfirmed','failed','cancelled','confirmed','superseded')),
  tryCount           INTEGER NOT NULL DEFAULT 1 CHECK (tryCount >= 1),
  createdUtc         TEXT NOT NULL,
  updatedUtc         TEXT NOT NULL
) STRICT;
CREATE UNIQUE INDEX Attempt_one_sending ON ManualSendAttempt(conversationId) WHERE attemptState = 'sending';
CREATE TRIGGER Attempt_immutable
BEFORE UPDATE OF messageId, conversationId, senderId, recipientId, rawSnapshot, kind, reaction, fingerprintAlg, contentFingerprint, createdUtc ON ManualSendAttempt
BEGIN SELECT RAISE(ABORT, 'Attempt snapshot is immutable'); END;
CREATE TRIGGER Attempt_draftbind
BEFORE UPDATE OF draftEpoch, draftRevision ON ManualSendAttempt
WHEN NOT (NEW.attemptState = 'sending' AND OLD.attemptState IN ('unconfirmed','failed','cancelled') AND NEW.tryCount = OLD.tryCount + 1)
BEGIN SELECT RAISE(ABORT, 'Attempt draft binding changes only on a manual retry'); END;
CREATE TRIGGER Attempt_terminal
BEFORE UPDATE OF attemptState ON ManualSendAttempt
WHEN OLD.attemptState IN ('confirmed','superseded') AND NEW.attemptState <> OLD.attemptState
BEGIN SELECT RAISE(ABORT, 'Confirmed or superseded attempt is terminal'); END;

CREATE TABLE ReceiptOutbox(              -- 只放「允許發送」的回條；D-10 禁用時不寫入
  originalLocalSeq INTEGER PRIMARY KEY REFERENCES Message(localSeq) ON DELETE CASCADE,
  conversationId   TEXT NOT NULL REFERENCES Conversation(id) ON DELETE CASCADE,
  recipientId      TEXT NOT NULL REFERENCES Peer(id) ON DELETE RESTRICT,
  receiptKind      TEXT NOT NULL CHECK (receiptKind = 'read'),   -- ★v4：Emily 裁定只有 read；新增種類＝以「重建表」migration 處理（本表是葉節點，沒有其他表參照它）
  createdUtc       TEXT NOT NULL
) STRICT;
CREATE INDEX ReceiptOutbox_conv ON ReceiptOutbox(conversationId);

INSERT INTO SchemaVersion(version, appliedUtc, appVersion) VALUES (1, @now, @appVersion);
PRAGMA user_version = 1;
