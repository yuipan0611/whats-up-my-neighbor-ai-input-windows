# Codex verification

## 2026-11-07 B1-a — S0 and implementation plan

Scope: local branch codex/lite-chat-ux-b1-20261107, starting 0651128; no WPF, push, PR, release or installation.

Impact (high risk): SQLite migration/restore attribution, classroom receive and attachment/legacy ingress, roster merge/delete/teacher, receipt scheduling. Frozen wire and SchemaV1 remain unchanged. Existing epoch/revision, socket attribution, dedup/clear tombstones, immutable send snapshots and policy generation must survive.

Plan: (1) clarify four BDD specs, (2) additive v14 with pre-upgrade backup and Core contact/teacher/read preference APIs, (3) receive/merge/receipt integration plus ID-prefixed scenarios, (4) focused and required regressions, win-x64 cross-build, reviewable local commits. RR-02 migration follows Emily's 2026-11-07 ruling: disabled generation=0 becomes enabled, disabled generation>0 stays off; record explicit choices going forward. SQLite teacher state is authoritative; JSON projection is B1-b.

Baseline outbox passed: 63 durable SQLite/real TCP checks in isolated build/tmp; initial sandboxed SDK launch was terminated before output and restarted with loopback/build access. Evidence logs: build/b1-a-evidence/ (untracked build output).

### S0 changes and B1-b handoff

- CT-05/06: only recoverable contact-local failure falls back via SAVEPOINT; capacity still rejects, failed database commit never ACKs stored. Automatic source/message/byte quotas remain effective after roster promotion.
- CT-02/15: retain original targetIp/UUID/localSeq, move v13 outbox principal, reject conflicting nonempty drafts/messages/active sends, preserve late-ACK/read-before-ACK and accepted-socket attribution fences. Multihomed IPs are distinct; names never merge identity.
- CT-07/13: v14 archive snapshots retain endpoint/name; restored contact reuses id as auto/student, no peer/teacher restoration. Pre-v14 deleted snapshots lack endpoint and are not guessed. Archive legacy rows remain with original scope rather than being attributed to a new person at the same address.
- TC-03/06: SQLite is authoritative and enforces at most one teacher. B1-b must pass legacy Settings.TeacherIp into ChatStoreOptions.LegacyTeacherIp before Open, then project GetClassroomTeacherIp back to Settings after commit and at startup; projection failure is retryable. Older rows have no reliable modification timestamp: choose matching existing teacher, otherwise ordinal roster id. Conflicting old unknown histories remain intact and diagnosed.
- RR-02: PM ruling (message 8405ea60-09a2-4233-b016-84907704fdb9): disabled generation=0 becomes on; disabled generation>0 stays off. A legacy no-op click cannot be distinguished and is an accepted boundary. v14 records explicitChoice even for a no-op choice, and noticeShown; only a changed legacy default needs the upgrade notice. B1-b must show and then acknowledge it.
- MD: forbid an actual recall operation this batch, allow explanatory “does not recall” text; pending deletion requires a stopped/idle exact attempt, deletes its retry work atomically, rejects stale count, preserves newer draft and other items.
- CT-08 added per Emily (message following 69831058): auto contacts have no automatic attachment download or classroom-file node/access rights. Label/IP edits remain auto. ConfirmAutomaticContact is an explicit, snapshot-fenced adoption; setting teacher is also explicit. Neither creates cryptographic identity/trust. B1-b must expose that confirmation and explain its effect.

### Existing-test adaptations (review required)

All attribution/default changes below are caused by D-27 or D-28; original outcome assertions are retained. No failure from another cause is waived. Emily separately approved the explicit schema-version update below (message 17807a8f-f6af-4c81-b303-5feddfa6f8e1).

| Test location / changed symbol | Old fixture or assertion | New fixture / assertion | Decision | Retained old migration contract? |
|---|---|---|---|---|
| ReadThroughChecks.cs:22,29,34,56,67,77 — initial store / Owner / actual received principals | Fixed ip principal and implicit receipt-off default | Explicit policy-off for the first phase; bind actual receive principal, legacy scope and visibility context; original 22 read/receipt/epoch assertions retained on v14 | D-27, D-28 | No; upgraded to v14 |
| ClassroomSenderOutcomeIndependentChecks.cs:116,459 — ExerciseReceipt and keep-alive receipt | Fixed ip principal for newly received message | Use stored.Principal; all transport/ACK/keep-alive expectations unchanged | D-27 | No; v14 |
| ClassroomSendChecks.cs:358 — Check 7b receipt seed | Fixed outgoing ip principal reused for received row | Rebind principal from received row before visible read; preserve all resolver/backoff/8-attempt assertions | D-27 | No; v14 |
| ClassroomReceiptChecks.cs:234 — first incoming receipt seed | Fixed keyA after receive | Set keyA from ClassroomStored.Principal; original 60 retry, ACK, policy and scope assertions retained | D-27 | No; v14 |
| ClassroomStoreChecks.cs:552 — Fixture.Options | CM-10 assertions explicitly expect no automatic roster creation and consent-only merge | Keep those assertions unchanged with Migrations.Take(13) | D-27 | Yes, 122 checks; new v14 paths in ContactExperienceChecks |
| ClassroomInboxChecks.cs:164 — Fixture.Options | CM-02/17 explicitly expects unknown ip conversations and old orphaned-legacy projection | Keep old assertions unchanged with Migrations.Take(13); v14 archive semantics tested in CT-07/13 | D-27 (old projection replaced with automatic contacts) | Yes, 161 checks |
| MessageRecoveryChecks.cs:18,103 — root Open and root reopen | Receive midway through an ip-principal deletion fixture was expected to keep ip ownership | Root fixture stays on v13, preserving all deletion/nickname assertions; its separate pagination/post-ACK-edit stores still use latest migration | D-27 | Yes, root fixture only; v14 MD-02/04 added |
| AttachmentChecks.cs:112 — additive upgrade check | LatestVersion == 13; ListBackups().Count != 0 | LatestVersion == 14; backup condition unchanged | Migration version advance, separately approved by PM | No; v10 to v14 upgrade |

### Scenario mapping

All scenarios below execute through `--contacts`; `--teacher` selects TC and `--receipts` selects RR. WPF presentation, JSON projection and physical devices are not covered by these Core scenarios.

| Spec | Scenario | Source | Result |
|---|---|---|---|
| CT-01 | automatic first contact and message commit together | ContactExperienceChecks.cs:75 | PASS |
| CT-02 | migrate all conversation ownership including v13 outbox | ContactExperienceChecks.cs:95 | PASS |
| CT-03 | no automatic authority or file privileges | ContactExperienceChecks.cs:139 | PASS |
| CT-04 | concurrent and duplicate ingress is idempotent | ContactExperienceChecks.cs:147 | PASS |
| CT-05 | recoverable contact failure falls back without lying about storage | ContactExperienceChecks.cs:153 | PASS |
| CT-06 | automatic contacts retain source and content budgets | ContactExperienceChecks.cs:161 | PASS |
| CT-07 | removed sender restores the same student contact | ContactExperienceChecks.cs:170 | PASS |
| CT-08 | automatic contact confirmation is explicit and does not authenticate identity | ContactExperienceChecks.cs:178 | PASS |
| CT-13 | removal archives readable history without granting a send route | ContactExperienceChecks.cs:193 | PASS |
| CT-15 | IP edits fence old routes and never infer multihomed identity | ContactExperienceChecks.cs:202 | PASS |
| TC-01 | promote existing teacher IP and merge its old conversation | ContactExperienceChecks.cs:214 | PASS |
| TC-02 | teacher replacement is atomic and keeps the old student | ContactExperienceChecks.cs:219 | PASS |
| TC-03 | SQLite alone owns the projected teacher address | ContactExperienceChecks.cs:225 | PASS |
| TC-04 | clear teacher retains student history | ContactExperienceChecks.cs:230 | PASS |
| TC-05 | removed teacher is archived not a regular student | ContactExperienceChecks.cs:235 | PASS |
| TC-06 | v13 upgrade backs up and normalizes teachers without data loss | ContactExperienceChecks.cs:240 | PASS |
| TC-07 | CSV seed chooses only the first nonlocal teacher | ContactExperienceChecks.cs:253 | PASS |
| RR-01 | new installations enable receipts without claiming a manual choice | ContactExperienceChecks.cs:262 | PASS |
| RR-02 | migration respects generations and persists one-time notice | ContactExperienceChecks.cs:266 | PASS |
| RR-03 | valid same-peer communication rearms exhausted receipts | ContactExperienceChecks.cs:274 | PASS |
| RR-04 | paging and per-target admission avoid head-of-line starvation | ContactExperienceChecks.cs:290 | PASS |
| RR-05 | disabling drops unsent receipts and prevents historical backfill | ContactExperienceChecks.cs:298 | PASS |
| MD-02 | local deletion keeps remote data and dedup tombstones | ContactExperienceChecks.cs:305 | PASS |
| MD-04 | stop and delete is scoped atomic and rejects active work | ContactExperienceChecks.cs:312 | PASS |

Focused result: 24 scenarios / 124 assertions passed. Required regressions and win-x64 cross-build all passed (table below).

### Final review notes

- Duplicate text messages retain monotone local/remote read and stored timestamps and remap queued receipts before removing the extra row. Conflicting legacy delivery proof or active/different attachment states are retained as merge conflicts, not silently discarded.
- General CSV import preserves the existing selected teacher; use the explicit teacher operation to replace/clear it. Fresh CSV seed takes the first nonlocal teacher and diagnoses additional ones.
- New CanAutoReceiveAttachments uses a scoped id/source SQL query, preserving the original known-roster loopback fixture without hydrating unrelated roster rows. No test-only product assembly/namespace exceptions were introduced.
- Pending work retains original targetIp when an IP changes. B1-b should explain a stale old route and offer stop/delete/recompose; it must not silently send old queued text to the new IP.
- No WPF sources, frozen Wire codecs, SchemaV1, artwork, PM DECISIONS/STATUS or other team verification files changed.

### Final verification — Core eedbcd237107bd9279b1eba6063aa5d7599b7c21

S0 spec commit: 92865f9. All following checks ran on the final Core content, with TMPDIR set to this worktree's real `build/tmp`. Original baseline `--outbox`: 63 checks passed before implementation.

| Check | Exit | Evidence |
|---|---|---|
| --contacts | 0 | build/b1-a-evidence/contacts.log (0.96s) |
| --classroom-all | 0 | build/b1-a-evidence/classroom-all.log (44.15s) |
| --outbox | 0 | build/b1-a-evidence/outbox.log (5.71s) |
| --diagnostics | 0 | build/b1-a-evidence/diagnostics.log (0.58s) |
| --diagnostic-journal | 0 | build/b1-a-evidence/diagnostic-journal.log (0.58s) |
| --diagnostic-recovery | 0 | build/b1-a-evidence/diagnostic-recovery.log (0.06s) |
| --diag-independent | 0 | build/b1-a-evidence/diag-independent.log (3.09s) |
| win-x64-build | 0 | build/b1-a-evidence/win-x64-build.log (4.5s) |

- `--contacts`: 24 ID-prefixed scenarios / 124 assertions; all PASS.
- `--classroom-all`: prelude suites (read-through 22, outbox 63, deletion 32, UX 26, teacher files 14, shared files 53) plus all 15 focused suites PASS. Attachments 70 real TCP/SQLite/bytes/hash checks; cleanup completed.
- Diagnostics: recovery 24 and journal 77; independent diagnostics 32/32 PASS. Required standalone flags passed individually.
- win-x64 build: exit 0, 0 warnings, 0 errors. This is a cross-build, not native WPF execution.
- `git diff --check` and safety-config strict validation PASS; existing physical-device unknowns remain.

Not executed: WPF runtime/menus/confirmation/upgrade notice/Settings JSON projection, native Windows CI, two physical Windows machines, classroom network, Chinese IME, physical DPI/multiple monitors, native clipboard, Windows junction/path-only checks. No push, PR, merge, release, installation or deployment.

A concurrently created `windows-lite/docs/specs/LINE-PARITY.md` is not this task's file; left untouched and untracked, excluded from these commits.

## Follow-up step 1 and Ben mandatory repairs

- Step 1: Tom 82482e4/f4227e9 cherry-picked as cd20c14/c5bfe5f, original author/coauthor preserved; RR-03 clarified in c4ad1a5. --contacts 143 assertions and --classroom-all passed.
- Step 2: ClassroomStoreChecks now runs all 122 checks on v14. Successful receive/conflict/address-change paths use returned/resolved principals; D-28 opt-out is explicit. Only CM-02 merge fixtures seed an unknown source through an injected, recoverable auto-contact failure (CT-05) on the actual v14 schema. No successful-ingress paths are pinned to v13.
- MessageRecoveryChecks root/open/restart moved to v14; receive assigns actual principal, secondary draft fixtures keep their own IP principal, and nickname selection uses its endpoint instead of Single() on the whole roster. All 32 assertions remain.
- ClassroomInboxChecks keeps its historical v13 contract. v14 corresponding coverage: `CT-13 v14 inbox projects active queued unknown and archived rows without writes`, plus `CT-13 pre-v8 orphan conversation remains a read-only archive`, CT-02 ownership/receipt/draft merge, CT-07 restore and TC-05 archive. New v14 scenario checks combined legacy/classroom unread, per-principal uniqueness, queued attempts, unknown drafts, read-only archive and unchanged WAL bytes.
- NormalizeChatUxHistory failure now rolls back its transaction, records fixed `contact-normalization-failed`, allows startup, retries next open. New scenario injects pre-COMMIT failure after a real merge and proves the data stays in its original scope, then next startup merges.
- Pre-v8 orphan `roster:` conversations without RosterRemoved are visible as read-only archives with fallback label 已移除的聯絡人; migration/real SQLite scenario preserves body.
- teacher.md documents explicit teacher selection changes csv to manual.
- Step 2 focused results: --contacts 156 assertions, --classroom-store 122, --message-recovery 32, all PASS. Full step2 classroom result recorded after completion. Logs: build/b1-followup/step2-*.log.
- Step 2 full `--classroom-all`: exit 0, all prelude and 15 focused suites passed; no older outcome assertions removed. v14 adaptation additionally updates automatic-source row counts, D-27 restore/duplicate-add expectations and the explicit v14 migration version. Historical Inbox v13 is paired with the new v14 projection/orphan scenarios above.

## Follow-up step 3 — D-29 blocking Core

Additive v15 preserves reviewed v14. BlockedSource stores only explicit local block actions. Receive/legacy/attachment/receipt admission checks share their writer transaction with mutation; source-not-allowed reuses existing errors. Blocking preserves history, cancels queued/active retry work, fences late ACK/attachment writes and demotes teacher atomically; unblock never automatically resumes cancelled work. Auto-download and classroom-file author/node access are filtered. Inbox exposes a Blocked flag without duplicating an empty IP subject for an existing contact.

BK-01..08 Core scenarios: BlockingChecks.cs via --blocking, 25 assertions PASS, including persistent read-only projection, no ingress records/notifications, author/node filtering, queued preservation/manual retry, removal distinction, teacher demotion, v14→v15 backup and two-connection serialized receive. --contacts (explicit v14 contract) 156, --outbox 63, --diagnostics recovery24/journal77 PASS. Logs build/b1-followup/step3-*.log.

S0 blocking clarification: no local backfill; after unblock a new explicit transmission can be accepted because rejected IDs/content are not retained. Network bytes already in flight cannot be recalled. Signed catalog operations received from an unblocked replica remain available for monotone withdrawal semantics, while blocked authors are hidden locally. WPF remains unexecuted. Historical AttachmentChecks v10→v14 fixture version setting has been reported to PM before alteration; approval pending.
