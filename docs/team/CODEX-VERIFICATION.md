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
| ClassroomStoreChecks.cs:560 — Fixture.Options | Former B1-a fixture was pinned to v13 | Follow-up now runs all 122 checks on v14; exact adaptations below | D-27/D-28 and Ben mandatory request | No; full v14 suite |
| ClassroomInboxChecks.cs:164 — Fixture.Options | CM-02/17 explicitly expects unknown ip conversations and old orphaned-legacy projection | Keep old assertions unchanged with Migrations.Take(13); v14 archive semantics tested in CT-07/13 | D-27 (old projection replaced with automatic contacts) | Yes, 161 checks |
| MessageRecoveryChecks.cs:18,106 — root Open and reopen | Former B1-a root fixture was pinned to v13 | Full v14 root/restart; use received principal and endpoint-specific nickname row; all 32 deletion/recovery assertions retained | D-27 and Ben mandatory request | No; v14 |
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

Historical D-29 implementation below; D-30 later replaces the outward refusal response while preserving the transactional no-persistence boundary. Additive v15 preserves reviewed v14. BlockedSource stores only explicit local block actions. Receive/legacy/attachment/receipt admission checks share their writer transaction with mutation; source-not-allowed reuses existing errors. Blocking preserves history, cancels queued/active retry work, fences late ACK/attachment writes and demotes teacher atomically; unblock never automatically resumes cancelled work. Auto-download and classroom-file author/node access are filtered. Inbox exposes a Blocked flag without duplicating an empty IP subject for an existing contact.

BK-01..08 Core scenarios: BlockingChecks.cs via --blocking, 25 assertions PASS, including persistent read-only projection, no ingress records/notifications, author/node filtering, queued preservation/manual retry, removal distinction, teacher demotion, v14→v15 backup and two-connection serialized receive. --contacts (explicit v14 contract) 156, --outbox 63, --diagnostics recovery24/journal77 PASS. Logs build/b1-followup/step3-*.log.

S0 blocking clarification: no local backfill; after unblock a new explicit transmission can be accepted because rejected IDs/content are not retained. Network bytes already in flight cannot be recalled. Signed catalog operations received from an unblocked replica remain available for monotone withdrawal semantics, while blocked authors are hidden locally. WPF remains unexecuted. Historical AttachmentChecks v10→v14 fixture setting was initially pending PM approval; Emily subsequently approved explicit Take(14), recorded in the D-30 follow-up below.


## Follow-up test adaptation details (current files)

All rows below retain the assertion's data-loss, scope, quota, privacy, rollback or cancellation check. Only changed contact ownership/default fixtures are adapted. The earlier table now describes the final v14 coverage rather than the superseded v13 choice.

| Test file:line | Old assertion / fixture | Current assertion / fixture | Basis | Old migration contract retained? |
|---|---|---|---|---|
| ClassroomStoreChecks.cs:43 | Stranger has ip principal; 2 roster rows | Stored principal is roster; 3 rows | D-27 | No; v14 |
| ClassroomStoreChecks.cs:50 | Claimed identity text still belongs to ip source | Compare actual stored source principal; target's 2 rows unchanged | D-27 | No; source boundary retained |
| ClassroomStoreChecks.cs:56,535 | Query old ip directly after endpoint move | Resolve actual source principal; old/new address histories stay distinct | D-27 | No; CM-10 boundary retained |
| ClassroomStoreChecks.cs:132,134 | Default off queues zero | Explicitly turn off then assert zero | D-28 | Old default retained in RR-02 migration fixture |
| ClassroomStoreChecks.cs:167,191,201,221,237,254,262,276,288,424,436,494 | v13 supplies unknown histories for merge/clear/guard/import fixtures | v14 plus CT-05 auto-contact local failure supplies identical unknown histories; assertions unchanged | D-27; Ben full-v14 request | No; conflict/rollback/consent checks remain |
| ClassroomStoreChecks.cs:315 | Count 3 incoming rows under ip:* | Count 3 incoming rows, now auto-owned | D-27 | No; source quota still enforced |
| ClassroomStoreChecks.cs:342 | 2 retained legacy rows have null owner | 2 retained legacy rows join source=auto; diagnostics still exclude addresses/content | D-27 | No; count/privacy retained |
| ClassroomStoreChecks.cs:367 | New message creates second unknown conversation | Restores same principal and one conversation | D-27 / CT-07 | No |
| ClassroomStoreChecks.cs:368 | Legacy owner cleared by removal | Legacy row reattaches to restored contact; count stays 1 | D-27 / CT-07 | No |
| ClassroomStoreChecks.cs:369,370 | Manual add creates new id and empty history | Duplicate refused, original id and 2 messages retained | D-27 | No |
| ClassroomStoreChecks.cs:371 | Clear deletes 1 row | Clear deletes both preserved rows | D-27 | No; clear scope retained |
| ClassroomStoreChecks.cs:503,560 | user_version 13 / fixture Take(13) | Explicit 14 / Take(14), old Message/Legacy/Peer contents unchanged | Migration version advance; Ben v14 request | v3 legacy data retained through v14 |
| MessageRecoveryChecks.cs:18,37,52,78,106,108,111 | v13 root; ip assumptions; Single() roster | v14; stored.Principal; reset isolated pagination principal; resolve after restart; select nickname by endpoint | D-27; Ben full-v14 request | No; all 32 original lifecycle outcomes retained |
| ContactExperienceChecks.cs:59 | Upgrade uses latest default | Explicit Take(14) | D-29 adds v15; retain already reviewed v14 contract | Yes; BK-08 separately covers v14→v15 |

## Follow-up step 4 — WPF implementation, native acceptance pending

| Specs | Implementation | Verification boundary |
|---|---|---|
| CT-10/11 | ClassRoster: IP + optional local note; duplicate opens captured existing IP | Cross-build only |
| CT-12/14, CT-08 confirmation | ContactManagement shared menu for list right-click and title ⋯; note dialog states local-only; explicit adoption confirmation | Cross-build only |
| CT-13, BK-01/06/07 | Inbox normal/archive/blocked sections, read-only composer; separate removal/block confirmations; teacher-demotion warning; unblock never auto-retries | Core scenarios PASS; WPF unexecuted |
| TC-01..07 projection | Runtime supplies LegacyTeacherIp before Open; authoritative teacher projected after startup/mutation; failed JSON save retried by reload/next open; teacher replacement confirmed | Core scenarios PASS; native JSON/UI flow unexecuted |
| RR-02 | Visible toggle, explanatory text, notice recorded only after shown | Core migration/notice tests PASS; native modal unexecuted |
| MD-01/03/05 | Hover/focus ⋯ and handled正文 right-click; disabled reason; preserve code newline-copy choices; draft removal / transfer cancel / local delete are separate | Cross-build only; native routing/clipboard/hover unexecuted |
| BK-02/04 | Deferred pet notification rechecks source block; pending status says stopped due to block; blocked/archived retry and attachment transfer disabled | Core admission/outbox PASS; native pet/UI unexecuted |

Cross-build logs: build/b1-followup/step4-{layout,contacts,actions,final,reviewed,readonly}-build.log; each edited WPF batch built for win-x64. Final WPF code commit: 8e54149. Final readonly cross-build exit 0, zero warnings/errors; preceding layout/contacts/actions/final/reviewed builds also exited 0. git diff --check and safety config strict validation passed (two existing physical/native unknowns remain).

Final Core rerun: contacts 156, blocking 25, outbox 63, diagnostics recovery 24/journal 77, diagnostic-journal 77, diagnostic-recovery 24, diag-independent 32: all exit 0. Logs build/b1-followup/final-*.log and final-results.json. Historical pre-D-30 rerun: full classroom-all exited 1 at AttachmentChecks:112 only: the default upgrade now reaches v15 but the historical assertion requires 14. Prelude plus first 14 suites passed. At that checkpoint no full-green claim was made; PM was asked before changing this old test (fdd40628, repeated ed623eae). Emily later approved the explicit v14 fixture; final rerun is recorded below. WPF runtime, physical Windows, two-machine/classroom, IME/DPI remain unexecuted. No push/PR/merge/release/install/deploy.


## D-30 follow-up — approved fixture and Tom integration

| Test file:line | Old assertion / fixture | New assertion / fixture | Basis | Old migration contract retained? |
|---|---|---|---|---|
| AttachmentChecks.cs:111–112 | Default Open now reaches v15 while asserting LatestVersion == 14 | Explicit Migration.Default.Take(14); constant 14 and ListBackups().Count != 0 unchanged | Emily's latest ruling, reply to fdd40628; migration version advance | Yes: historical v10→v14. BK-08 separately covers v14→v15 backup |
| ContactExperienceChecks.cs:244 | Restore/archive overlap not independently guarded | Tom's unchanged automatic-restore / one-inbox-row negative scenario | 3f35778 cherry-picked as f968b52, author/coauthor preserved | v14 |
| ContactExperienceChecks.cs:254 | Stale archive/live roster overlap not independently guarded | Tom's unchanged live-row-never-removed scenario | Same Tom commit | v14 |
| ContactExperienceChecks.cs:261 | Row-scoped legacy leak not independently guarded | Tom's unchanged removed-contact legacy isolation scenario | Same Tom commit | v14 |
| BlockingChecks.cs (BK-02 response assertion) | Blocked transport returns 403 | HTTP 200 normal stored ACK; local Core still rejects persistence in the same transaction | D-30 | New v15 behavior, not a legacy migration waiver |

M2 (`unknownLegacyFilter`) is an equivalent mutant by PM ruling: no product change was made for it. Tom's three new scenarios are retained verbatim; no assertions removed.

D-30 behavior map:

- ClassroomStoreSink.OnMessageAsync: transaction's source-not-allowed result becomes normal message.ack stored with the frozen writer's real content hash; no MessageStored / success-rearm callback. Legacy uses the existing v1 delivered response, not a new v1 wire shape. OnReceiptAsync returns discarded without event or identifying diagnostics.
- Receive diagnostics: start/rejection and adapter exception paths omit request IDs, including stopped-request rejection before the store can identify a block. Normal stored-message success diagnostics remain unchanged. Normal unblocked storage failures also retain their original UUID/root-cause correlation at the sink; blocked or unreadable block-state failures omit identifiers. Existing DiagnosticChecks:235 remains unchanged and passes; new D-30 privacy tests cover both failure branches. Actual negative test initially caught an ID retained on the stopped-request path; the regression now checks all receiver journal events and a real ZIP export.
- AttachmentService.HandleAsync: validate offer normally, then catch the transactional block refusal and return existing offered ACK. No attachment metadata/download/complete. A's durable attachment queue completes with zero failures; row remains offered with no CompletedUtc, label 「通知已送出 · 等待收件確認」. Chunk/complete blocking stays enforced.
- ClassroomSender.SendReceiptAsync: recheck local source-block state before hello, after hello and on retry. Controlled hello peer blocks during the probe and proves zero receipt POSTs; an unblocked positive control sends/completes normally. Already-written network bytes remain outside cancellation guarantees.
- ChatWindow.Inbox.cs: classroom stored-ACK history label is 「已送出」 (D-30), since that ACK no longer proves persistence. Frozen signed/v2 archive history stays unchanged. Native WPF display is not executed; cross-build only.
- BlockingChecks covers public private-IP APIs plus isolated real TCP/SQLite sender/outbox and attachment flow. Test-only loopback DB mapping is explicitly documented; a negative assertion proves the product still rejects loopback contacts. No test-only product guard or assembly exception was added. All B table counts remain unchanged during blocked traffic; no notification, auto contact, unread, receipt or attachment records; exported diagnostics exclude source IP, UUIDs, body and filename. Private-IP same-id resend after unblock uses normal CT ownership; no backfill or ID blacklist.

Scope: no frozen Wire/SchemaV1/artwork changes, no new migration beyond additive v15. No push/PR/merge/release/install/deploy. WPF runtime, physical Windows, two-machine/classroom, IME/DPI and native clipboard remain **未執行**; cross-build/Core results do not count as those acceptances.


## 第 1 批完成 — 最終驗證（D-30）

Local commits for this follow-up: f968b52 (Tom 3f35778 preserved verbatim), fc7f2a1 (approved historical attachment fixture), acecc54 (D-30 product/scenarios/spec/UI label). These build results cover the final product/test content; the final evidence commit changes documentation/config only.

| Check | Result | Exit | Seconds |
|---|---|---|---|
| tests-build | Release console test build | 0 | 4.88 |
| blocking | 54 assertions, BK-01..08 + D-30 | 0 | 1.75 |
| contacts | 169 assertions, including Tom negative scenarios | 0 | 1.27 |
| teacher | 31 assertions | 0 | 0.34 |
| receipts | 22 assertions | 0 | 0.42 |
| classroom-all | Prelude + all 15 suites, attachments 70 | 0 | 45.81 |
| outbox | 63 assertions | 0 | 5.74 |
| diagnostics | recovery 24 + journal 77 | 0 | 0.58 |
| diagnostic-journal | 77 assertions | 0 | 0.55 |
| diagnostic-recovery | 24 assertions | 0 | 0.06 |
| diag-independent | 32 assertions | 0 | 2.99 |
| win-x64-build | 0 warnings, 0 errors; cross-build only | 0 | 3.87 |

Evidence: build/b1-followup/d30-final-results.json and d30-final-*.log. TMPDIR was this worktree's real build/tmp. Earlier failing diagnostic attempt is retained as a resolved regression in d30-diagnostics-repair.log; **no DiagnosticChecks assertions were changed**. Earlier version-14 fixture failure is resolved by the explicitly approved Take(14), not by weakening the version/backup check.

Additional changed behavior / review anchors (acecc54 snapshot; newer Ben-fix anchors below supersede current-file line numbers):

| Area | File:line |
|---|---|
| Stored ACK without persistence; blocked receipt/legacy handling | NekoChat.Core/Classroom/Transport/ClassroomStoreSink.cs:36,52,109,132 |
| Normal error UUID retained only when source is safely unblocked | ClassroomStoreSink.cs:38; ClassroomEndpointAdapter.cs:216,258 |
| Offered-only attachment ACK after same-transaction block refusal | NekoChat.Core/Attachments/AttachmentService.cs:253 |
| Outgoing receipt block fences | NekoChat.Core/Classroom/Transport/ClassroomSender.cs:505,594,602 |
| User-visible classroom sent label | NekoChat.Windows/ChatWindow.Inbox.cs:380 |
| Failure privacy negative cases | NekoChat.Tests/BlockingChecks.cs:67 |
| Block-during-hello + positive control | NekoChat.Tests/BlockingChecks.cs:105 |
| Isolated real TCP/SQLite two-sided send and attachment queue | NekoChat.Tests/BlockingChecks.cs:155 |
| Private-IP same acknowledged ID after unblock / CT ownership | NekoChat.Tests/BlockingChecks.cs:270 |
| Approved constant-14 upgrade fixture | NekoChat.Tests/AttachmentChecks.cs:111–112 |

All paths in this anchor table are under windows-lite/. git diff --check and safety config strict validation passed. No Wire codec, SchemaV1 or artwork changed. WPF runtime, native Windows CI, physical Windows, two-machine LAN/classroom, IME/DPI, native clipboard remain **未執行**. No push, PR, merge, release, install or deployment. Next gate belongs to PM: Ben reviews v15/D-30 and Tom runs blocking mutants; this report does not claim their review is complete.


## Ben 最終修正 F1–F5（b42a2a4 後）

- F1: Actions:84 / ClassroomSendUiPolicy:74 success says 「已送出。」; cancel outcome :210 says 「已送出（無法再取消）。」. Classroom queued/manual waiting banners use 「接收回應」; storage-unavailable failure text no longer calls it a remote storage confirmation. Attempt/outbox states, evidence fields and ACK validators are unchanged.
- F2: App.xaml.cs:448/459 now calls NotifyAfterBlockCheckAsync (:478). The block query runs through the runtime Worker, then Dispatcher handles the notification with closing/runtime/listener reference guards. Query failure suppresses notification and records identifier-free fixed diagnostics. The synchronous ChatRuntime helper was removed. Signed/v2 notification callback is unchanged. Native dispatcher behavior remains unexecuted on macOS; source audit and cross-build are the available evidence.
- F3: Inspection confirmed the prior filtered lookup prevented relaying cached blocked-author bytes. Storage's internal GetClassroomFileForServing (:48) ignores only local author hiding. ClassroomFileService:228/246/249 uses it only for outward status/chunk; local list/Get/Download continue to hide the author. Existing caller-source, grant/IP binding, size/range, ready/cache and withdrawal checks remain. New BK-03 F3 scenario serves exact staged bytes to another permitted node, verifies local hiding, wrong-token rejection, blocked-source rejection and signed withdrawal invalidation of an old grant.
- F4: blocking.md explicitly accepts diagnostic fail-closed with no UUID/IP when block state cannot be read. Existing D-30 failure privacy scenarios remain unchanged and continue to prove both blocked and unknown-state failures.
- F5: BK-08 F5 wraps the actual Migration.V15.Apply inside its existing transaction, executes real table creation + partial row/data changes, then injects failure. A canonical read-only snapshot compares user_version, schema and every table value (including sqlite_sequence) before/after. Valid v14 re-open, backup retention and successful next normal v15 upgrade are checked. No new product fault hook or migration format was added.

### Old test adjustments — D-30 文案變更

| File:line | Old assertion | New assertion | Basis | Migration contract? |
|---|---|---|---|---|
| ClassroomSendUiPolicyChecks.cs:68 | Confirmed cancel text contains 已送達 | Exact 已送出（無法再取消）。 | D-30 文案變更; Ben F1 | No: UI wording only; stronger exact check |
| ClassroomSendUiPolicyChecks.cs:74 | AlreadyConfirmed cancel text contains 已送達 | Exact 已送出（無法再取消）。 | D-30 文案變更; Ben F1 | No: UI wording only; stronger exact check |
| ClassroomSendUiPolicyChecks.cs:300 | Exact 已驗證對方的儲存確認。 | Exact 已送出。 | D-30 文案變更; Ben F1 | No: all DraftCleared/attempt/state assertions retained |

No old assertions were removed; the policy suite still has 133 checks. No signed/v2 wire, schema or vectors changed. Actions.cs:84 is actually in the archived signed/v2 branch, but this one status line was explicitly listed by PM F1 and therefore follows the requested new wording; the frozen protocol/persisted ACK semantics and v2 history/smoke expectations are retained.

### Whole-project wording search and retained matches

Command: rg -n --hidden (excluding .git, build, bin, obj) '已儲存|儲存確認|已確認儲存' . . Full output: build/b1-followup/ben-final-storage-wording-rg.txt. This scans the worktree, not only the three identified lines.

| Retained source match | Reason |
|---|---|
| ChatWindow.Inbox.cs:383; App.MessagingSmokeTest.cs:121 | Signed/v2 persisted-message history and its exact native smoke expectation; D-30 classroom shadow ACK does not alter this frozen protocol path |
| ChatWindow.Actions.cs:79,88 | Signed/v2-only waiting text and local failure to save a received confirmation; neither asserts classroom remote persistence. Its success line :84 follows explicit F1 |
| ChatWindow.Inbox.cs:337–338; ChatWindow.xaml:220; ChatWindow.ProtocolHints.cs:32 | Explicit negative explanation that legacy confirmation does NOT mean stored/read |
| ChatWindow.Inbox.cs:552,555; ChatWindow.ClassRoster.cs:105; ChatWindow.Pairing.cs:22; ChatWindow.xaml:274 | Local roster/nickname persistence, not a remote-delivery claim |
| PairingDialogPresentation.cs:15; preview/pairing-dialog.html | Pairing confirmation persistence is separate from classroom message delivery and its warning remains valid |
| Historical/spec/verification Markdown quoting old strings | Audit trail and the requested old/new assertion table; not active product text |

Active classroom success/cancel/waiting/error paths no longer use the three searched storage-confirmation phrases. No blanket replacement of signed/v2, local-data or negative safety text was performed.


### 第 1 批最終修正完成 — 驗證結果

Product/test commits: 7a3041b (F1/F2), edd53ae (F3/F5 plus F4 spec). The final evidence commit changes docs/config only. WPF was cross-built immediately after F1/F2 (ben-final-ui-build.log), then again after all Core changes (ben-final-win-x64-build.log); both exit 0, zero warnings/errors.

| Check | Result | Exit | Seconds |
|---|---|---|---|
| tests-build | Release test build | 0 | 10.5 |
| blocking | 65 assertions (54 retained + F5 5 + F3 6) | 0 | 1.47 |
| contacts | 169 assertions | 0 | 1.13 |
| classroom-ui-policy | 133 original checks retained | 0 | 0.07 |
| classroom-files | 53 real TCP/SQLite/share regression checks | 0 | 12.03 |
| classroom-all | Prelude + all 15 suites including attachments 70 | 0 | 44.52 |
| outbox | 63 checks | 0 | 5.7 |
| diagnostics | recovery 24 + journal 77 | 0 | 0.58 |
| diagnostic-journal | 77 checks | 0 | 0.54 |
| diagnostic-recovery | 24 checks | 0 | 0.06 |
| diag-independent | 32 checks | 0 | 3.04 |
| win-x64-build | cross-build; 0 warnings/errors | 0 | 27.36 |

Evidence: build/b1-followup/ben-final-results.json, ben-final-*.log and ben-final-storage-wording-rg.txt. TMPDIR = this worktree's real build/tmp. git diff --check and safety config strict validation pass; the two existing physical/native unknowns remain. V15 migration body, schema version and frozen wire/schema/artwork are unchanged; only the test wraps v15 for failure injection.

Current regression anchors: BlockingChecks.cs:67 snapshot, :86 V15FailureRollback, :112 BlockedAuthorRelay; ClassroomFileService.cs:228/246/249 outward lookup, ChatStore.ClassroomFiles.cs:48 internal relay lookup; App.xaml.cs:478 Worker/Dispatcher helper. Old assertion table above records all three F1 string changes.

No push, PR, merge, release, install or deployment. WPF runtime/Dispatcher visual behavior, native Windows CI, two physical Windows devices/LAN/classroom, IME/DPI and native clipboard remain **未執行**. Product changes are ready for PM/Ben follow-up review; this is not a claim that the new commits have already passed that independent review.


## D-31 B1 CI 準備 — 原生 WPF smoke 與預設 Console 整備

Tom bbef682 was cherry-picked without conflict as 5e27cbc, retaining original author/coauthor and all assertions. Blocking combines Tom's 54→79 with our F3/F5 11 assertions: --blocking = 90. The native smoke implementation and matrix are documented in windows-lite/docs/specs/contacts-ui-smoke.md; Windows runtime results remain pending PM push/Draft PR.

- App.ContactsUiSmokeTest.cs drives the real ChatWindow, native list/body right-click, title/message ⋯ menus, actual IP/note form, teacher replacement/Settings projection, archives/blocking, disabled-delete reason, attachment action labels and persisted one-time receipt notice. Each stage captures only synthetic UI. The duplicate-IP case first selects another conversation, preventing a false pass from an already-selected existing row.
- Per-window ContactPrompt/ContactDialog hooks require attached IsolatedData runtime plus the exact same Settings/DB root. Before any Save/Open the smoke asserts paths; it never loads or writes daily settings. MessageBox confirmations are auto-answered and recorded, while native IP/note forms are filled/clicked through Loaded hooks. Ordinary UI uses the original MessageBox/ShowDialog behavior.
- The shared seed creates actual v13 via original migrations without changing Core friend access or frozen SQL. The cross-platform --contacts-ui-seed-check executes 7 assertions against the same source: v13 seed, overwrite refusal, real upgrade, received auto contact/old history/draft, one-time notice and backup. Input/profile guards and original migration semantics are preserved.
- Incoming HTTP stays loopback; the fixture sink supplies a declared synthetic private source to actual storage via Worker (normal contacts reject loopback). Actual sessions/endpoint validation and receive/dedup transactions still run. JSON declares this mapping; it is not LAN identity proof. Only the loopback receiver starts; no outbox/receipt/classroom background send loops. Sending/receiving states are explicit synthetic Core fixtures for UI binding, not transfer acceptance.
- windows-2025 runs both package/portable with --contacts-ui-smoke-test; exit/passed/isolation flags and capture existence are required. JSON/PNG upload via always() artifact nekochat-lite-contacts-ui-smoke. Local YAML parse validates syntax/runner; actual Actions/pwsh/WPF are not claimed run on macOS.
- Existing App.MessageRecoverySmoke now locates current message ⋯ and local delete menu, and uses 編輯本機備註 names. It retains all data/retry/deletion/nickname assertions and adds fail-fast for disabled delete. Popup lookup waits for actual native popup/layout rather than assuming synchronous creation.

### Consolidated old Console test adjustments (PM's latest ruling)

All current-version checks use explicit constants 15 / 35, never Migration.Default.Count; the three extra STRICT tables are v14 ClassroomArchivedContact/ClassroomAutoSource plus v15 BlockedSource. Historical source/preimage versions (v1..13/v5/v13 backups, explicit v10→v14 AttachmentChecks) remain unchanged. No default-suite fixture was newly pinned or moved backward. Previously approved explicit historical Inbox/v13 and contact/v14 fixtures remain separate from the latest-schema checks.

| Test file:line | Old fixture / assertion | New fixture / assertion | Basis; old contract retained? |
|---|---|---|---|
| StorageFoundationChecks.cs:39 | current user_version 13 | explicit 15 | migration 版本前進; no |
| StorageFoundationChecks.cs:40 | SchemaVersion count 13 | explicit 15 | migration 版本前進; record completeness still checked |
| StorageFoundationChecks.cs:42–43 | 32 total / STRICT application tables | 35 total / STRICT tables, includes all 3 new tables | migration 版本前進; STRICT invariant unchanged |
| StorageFoundationChecks.cs:302 | CRLF frozen-v1 upgrades to 13 | upgrades to 15 | migration 版本前進; CRLF validation unchanged |
| StorageTombstoneChecks.cs:26 | v1→current version 13 | 15; same body/draft/clear guards | migration 版本前進 |
| StorageRosterChecks.cs:68 | current pre-import backup version 13 | 15; same original roster/chat checks | migration 版本前進; historical backups elsewhere unchanged |
| StorageLegacyRosterChecks.cs:62 | v3→current version 13 | 15; bootstrap still pending | migration 版本前進 |
| SecurityRevisionChecks.cs:114 | v4→current version 13 | 15; key/revision/pending-receipt/data checks unchanged | migration 版本前進; no signed wire change |
| LegacyMessageChecks.cs:62 | v5→current 13 / 32 tables | 15 / 35; same original roster id and v5 backup | migration 版本前進 |
| BackupRestoreChecks.cs:31,49 | restore target 13 | 15; source versions, hash, suspended trust and privacy reset unchanged | migration 版本前進; v1..13 source contracts retained |
| UiEnhancementChecks.cs:73 | fresh policy disabled/gen0 | D-28 enabled/gen1 | D-28; old default retired |
| UiEnhancementChecks.cs:77 | first UI startup enables gen1 | same true/gen1 value, now explicit idempotent call | D-28; disable gen2 and user-choice assertions unchanged |
| StorageReadChecks.cs:42–44 | v2 upgrade disabled/gen0 | enabled/gen1; unsent old queue dropped, reads/history unchanged | D-28 |
| StorageReadChecks.cs:49–50 | default off phase implicit | first assert new on/gen1, explicitly opt out before testing off/read/no-backfill | D-28; off guards retained as explicit choice |
| StorageReadChecks.cs:57,61,65 | re-enable/token gen1, disable gen2 | explicit opt-out means re-enable/token gen3, disable gen4 | D-28; monotonic token/stale-ACK guards unchanged |
| V2StorageFenceChecks.cs:91–95 | wire integration expected old default OFF | set explicit OFF before wire traffic; same off-state and signed/nonce/storage fences | D-28; no signed/v2 wire change |
| ReceiptServiceChecks.cs:25–28,46 | implicit defaultOFF live-service phase | explicit SetReadReceiptPolicy(false), same no-send/no-backfill assertions | D-28; privacy-off contract retained, not old default |
| ClassroomRosterChecks.cs:26,63,216 | unknown source supplied by old no-auto default | two merge-only fixtures inject CT-05 recoverable admission fault on latest schema | D-27; original stale-consent/conflict assertions preserved |
| ClassroomRosterChecks.cs:68 | manual add merges0, 2 unknown legacy rows | merges2, 0 unknown, both rows preserved | D-27 / CT-02 |
| ClassroomRosterChecks.cs:82,200 | old address queried as unknown scope | resolve source's current auto roster scope; original message/source separation assertion retained | D-27 |
| LegacyMessageChecks.cs:42 | reuse unknown receive context after first message | recapture admitted scope as real listener does; same 2 unidentifiable arrivals | D-27; stale context rejection elsewhere retained |
| LegacyMessageChecks.cs:82,85 | removed row read/cleared as unknown IP | original Bob row archive read/cleared; Carol still gets zero old messages | D-27; no cross-person transfer |
| LegacyMessageChecks.cs:102 | clear former IP scope after auto admission | recapture current row scope before clear; same monotone sequence/count guards | D-27 |
| LegacyMessageChecks.cs:121 | visibility fixture unknown source | known manual fixture, derives actual scope; same local-read-only/no-receipt guards | D-27 |
| LegacyMessageChecks.cs:142,153 | other/overflow scope captured before auto admission | recapture after receive for subsequent operations; same isolation/overflow checks | D-27 |
| LegacyMessageChecks.cs:160 | two concurrent arrivals share obsolete unknown context | known manual contact stabilizes both contexts; same 2 arrivals + 119 paging rows | D-27; concurrency/no-v1-dedup checks unchanged |
| InboxWorkspaceChecks.cs:24 | unknown legacy id / fixed anonymous label | auto roster at source endpoint / its explicit IP display label | D-27; no identity/trust promotion |
| InboxWorkspaceChecks.cs:38 | deleted student's unread read under unknown IP | read original student's archived row scope; same unread/no-read-mutation checks | D-27 |

### Product source-guard repair (separately approved by PM)

StorageFix_SourceRules was not changed. Contacts' communication observer and diagnostic sink catches now journal fixed NotificationFailed metadata without IP/content/message UUID. The sink's catch never invokes the sink again; journal errors remain nonblocking by the existing journal contract. A new CT-05 scenario proves throwing sink receives once, body persists without a partial contact, observer throw is isolated, same-session receive recovers and metadata has no identifying content (6 assertions, contacts total 175).

The existing source scanner treats the first property-pattern brace in a catch filter as a catch body. The SQLite constraint filter was rewritten from `e is SqliteException { SqliteErrorCode: 19 }` to equivalent `e is SqliteException sqlite && sqlite.SqliteErrorCode == 19`; no guard relaxation or exception-classification change. Per PM, new product guard violations are fixed in product, not waived in tests.


### B1 CI 準備完成 — 最終本機結果

Commits: 5e27cbc (Tom bbef682), 8974fd3 (typed diagnostic isolation + scenario), 185fffc (consolidated version/D-27/D-28 Console adjustments), c4af924 (native smoke/shared seed/CI). The final evidence commit modifies docs/config only.

| Check | Result | Exit | Seconds |
|---|---|---|---|
| tests-build | Release test build | 0 | 2.03 |
| default-ci | full default Console: 6,570 checks, including unchanged SourceRules 321 | 0 | 70.59 |
| contacts-ui-seed-check | 7 actual SQLite historical seed/upgrade/isolation checks | 0 | 0.22 |
| blocking | 90 assertions, Tom + retained F3/F5 | 0 | 1.73 |
| contacts | 175 assertions (169 retained + 6 diagnostic isolation) | 0 | 1.15 |
| classroom-ui-policy | 133 checks | 0 | 0.07 |
| classroom-files | 53 TCP/SQLite checks | 0 | 12.05 |
| classroom-all | prelude + all 15 suites; attachments 70 | 0 | 44.86 |
| outbox | 63 checks | 0 | 5.74 |
| diagnostics | recovery24 + journal77 | 0 | 0.54 |
| diagnostic-journal | 77 checks | 0 | 0.53 |
| diagnostic-recovery | 24 checks | 0 | 0.05 |
| diag-independent | 32 checks | 0 | 3.0 |
| win-x64-build | cross-build, 0 warnings / 0 errors | 0 | 5.57 |

Evidence: build/b1-followup/d31-final-results.json and d31-final-*.log. All tests used this worktree's real build/tmp as TMPDIR. YAML parsed with Ruby/Psych; windows job resolves to windows-2025 and the contacts artifact uses always(). git diff --check / safety-config strict validation pass. Product source guard, frozen wire/vector files, SchemaV1 and artwork were not weakened or rewritten.

Native WPF smoke, pwsh Actions execution and screenshot artifacts are **未執行／待 Emily push＋Draft PR 的 Windows CI**. Source code and a cross-build are not those results. IME, physical DPI, real two-machine/LAN/classroom acceptance and native clipboard also remain unexecuted. No push/PR/merge/release/install/deploy was performed locally; PM owns push/Draft PR.


### PR #28 Windows file-sharing correction (2026-11-08)

PM requested a test-only repair for run 42396110498. The retrieved Windows log confirms `ContactExperienceChecks.cs:82` called `File.ReadAllBytes` on a live SQLite WAL; the Mac baseline passed 175 assertions, so that baseline does not reproduce or clear the Windows failure. No Core/product behavior, protocol, schema, existing guard or workflow is changed.

Impact/plan: retain the open-store before/after SHA-256 assertion; use a narrowly scoped shared test/smoke reader, audit all 87 Console test source files plus linked Shared fixtures and native smoke entrypoints, add positive/negative controls, then run contacts, blocking, default Console and win-x64. Risk is limited to test inspection and Windows handle lifetimes; native results still require PM's CI rerun.

| Inspected file / location | Result / action |
|---|---|
| `Shared/TestFixtureFiles.cs:12` (new; linked by both test and WPF project manifests) | `FileStream(Open, Read, ReadWrite \| Delete)` for streamed SHA-256 and exact-byte inspection. No catch, missing-file fallback, newline normalization, DB checkpoint or owner disposal. Sharing does not claim an atomic snapshot of concurrent writes. |
| `ContactExperienceChecks.cs:75` / `:102` | Keep store open across original WAL before/after read-only-inbox check; switch hash to shared stream. Add 5 exact-byte/open-owner/rename/delete/missing-file checks and 1 real SQLite-write negative control. All original 175 assertions retained: total 181. |
| `ClassroomInboxChecks.cs:157` | Existing live DB streamed hash already shared ReadWrite; add Delete. Existing no-write and projection assertions unchanged. |
| `PairingCrashChecks.cs:255`, `V2CrashChecks.cs:172` | Full-directory private-key leak scans can encounter live `.db`, `-wal`, `-shm` or journal files. Use exact shared bytes; preserve size bounds, raw/base64 search and zeroing. |
| `StorageTombstoneChecks.cs:57,59`, `ContactsUiSeedChecks.cs:19,21` | Shared DB-byte inspection for schema-rejection/seed-overwrite invariants (normally after prior connection disposal). All byte/backup/schema assertions retained. Seed path construction now `Path.Combine(root,"data")`. |
| `DiagnosticChecks.cs:146,161` | Both retained-generation/lock-file dictionaries use shared exact-byte reads. Owners are disposed before enumeration, so an exclusive live writer-lock is never bypassed. Same file-set and per-generation byte comparisons. |
| `App.DiagnosticSmokeTest.cs:145,150` | Native retained-history before/after export dictionaries use the same reader; owner lifetime and all-generation preservation assertion unchanged. |
| `App.AttachmentStartupSmoke.cs:112` | Corrupt DB preservation check uses shared exact bytes after rejected open; same fail-closed assertion. |
| `BlockingChecks.cs` (Tom tests, F5 `DatabaseSnapshot` at `:67`, `RowCounts` at `:226`) | No unsafe raw DB/WAL read exists. F5 retains `Mode=ReadOnly`, `Pooling=false`, complete canonical schema/version/all-table values, after original store is disposed. Tom row-count checks use scoped SQLite connections. `ZipFile.OpenRead` at `:289` occurs only after awaited export completes and disposes its output. No assertion or product change. |
| Existing storage/backup/history/security/legacy hash helpers | Already stream with `FileShare.ReadWrite \| FileShare.Delete`; left intact. Backup/report sentinel strings, static vectors/assets/source files, completed diagnostic ZIPs and committed attachment blobs are read after writers close; no sharing correction required. `StreamReader` calls over socket/ZIP-entry streams are not direct file opens. |
| Native contacts smoke / `ChatWindow.ContactsSmokeFixture.cs` / shared v13 seed | No direct WAL/DB file hash or byte read exists in these files. Native contacts reads state through the Worker/SQLite API, and its captures are real WPF rendering. The existing native file-unchanged check is the diagnostic retained-history check above; no imaginary contacts WAL check is claimed. |

Windows differences audited: filesystem paths use `Path.Combine`/platform-aware helpers; HTTP URL `/` separators and explicitly unsafe path vectors are intentional and unchanged. No additional filename-case bug identified in the audited tests/fixtures; existing intentional case-insensitive installer/Windows path checks remain. Existing DB/stream scopes dispose before recursive fixture deletion, with SQLite helpers using `Pooling=false`; no retry/forced unlock/suppressed cleanup failure introduced. New handle regression renames/deletes while inspection and owner handles are open with Delete sharing, reads the retained bytes, then closes **all** handles before asserting final removal (Windows may defer deletion). CRLF/non-ASCII/NUL bytes are compared without normalization. HTTP CRLF, chat raw-text CRLF and migration-CRLF fixtures test explicit formats, not host-generated newlines, and are unchanged.

Raw audit evidence (local build artifacts): `build/b1-followup/windows-file-read-audit.txt`, `windows-file-read-audit-after.txt`, `windows-platform-audit.txt`; original CI log `windows-ci-42396110498-failed.log`. The latter is read-only GitHub evidence, not a new CI result.

Final local verification (real worktree `build/tmp` used as TMPDIR):

| Check | Result | Exit | Seconds |
|---|---|---|---|
| tests-build-final | Release test build (after final Windows deferred-delete fixture adjustment), 0 warnings / 0 errors | 0 | 6.55 |
| contacts-final | 181 assertions: 175 retained + 6 new controls | 0 | 2.05 |
| blocking | 90 assertions, including Tom, F3 and F5 | 0 | 2.36 |
| contacts-ui-seed-check | 7 real SQLite seed/isolation checks | 0 | 1.03 |
| default-ci | 6,570 behavior checks; SourceRules 321 unchanged | 0 | 69.96 |
| win-x64-build | 0 warnings / 0 errors | 0 | 9.63 |

Evidence: `build/b1-followup/windows-wal-results.json`, `windows-wal-*.log`. The final source adjustment only closes the synthetic sharing-regression owner before asserting deletion completion; Release tests were rebuilt and contacts 181 rerun afterward. Default Console includes diagnostic, inbox, tombstone and signed crash/leak-scan regressions. Cross-build includes both modified native smoke fixtures and the linked test reader. `git diff --check` and existing strict safety-config validation pass (the two existing physical/native unknowns remain).

**CI 修正完成（本機）**. No product semantics or existing assertions were weakened. Native Windows file-sharing behavior/WPF/PNG capture results are **未執行／待 Emily push 同一分支重跑 PR #28 CI**; Mac success and cross-compilation do not replace that evidence. IME/physical DPI/two-machine/classroom acceptance remain unexecuted. No push/new PR/merge/release/install/deploy performed.


### PR #28 CI round 2: archived selection and composer projection (2026-11-08)

Read-only evidence: GitHub run 42427643109 failed in the package native contacts smoke, after the Windows Console/contacts/blocking steps passed. Retrieved log: `build/b1-followup/windows-ci-42427643109-failed.log`. PM's artifact is `reports/lite-b1-ci-screenshots-20261108/contacts-ui-result.json` and `18-failure.png` under the desktop workspace. The real screenshot shows the removed contact selected in the archive filter, an inactive send button, and the old draft text still visible.

**Confirmed cause, not an inferred slow-runner fix:** `ChatStore.ClassroomInbox.cs`'s removed-subject SELECT intentionally projects `endpoint=NULL`; `ClassroomInboxSnapshot.ToInboxSnapshot` retains that value and the stable roster principal. The old native Wait at `App.ContactsUiSmokeTest.cs:224` required both archive filter 2 and `row.Entry.Endpoint == ContactsManualIp && Removed`. That conjunction cannot succeed for a correctly archived row. The old product also deliberately set filter 2 in both `ManageContactAsync` and settings `DeleteRosterClick`; PM's new CT-13 ruling changes this behavior. No timeout is increased.

| Change | Behavior / protection |
|---|---|
| `ChatWindow.ContactManagement.cs` `CompleteContactRemovalAsync` | Both removal entrypoints keep the current filter/search; after actual refreshed data arrives, select the first visible active row, otherwise explicitly clear the conversation to an empty state. Show exactly “已移到封存區，可在篩選切換查看”. Do not select a hidden archived row. |
| `ChatWindow.Inbox.cs` refresh completion / selection | Only the removal flow (and its smoke observer) requests awaited coalesced refresh completion; ordinary refresh coalescing remains nonblocking. A TaskCompletionSource completes in finally, with asynchronous continuations. Existing view generation / history selection / visible-read guards remain. An explicitly empty selection after removal survives refresh until the user selects a contact. |
| `ChatWindow.Inbox.cs` `SetDraftText`, `DraftEdited`, `UpdateComposer` | Removed/blocked composer text is an empty UI projection with an explicit read-only placeholder and disabled send/input. `_settingDraft` and the read-only edit guard prevent projection changes from editing the retained buffer. No draft DELETE/UPDATE or epoch/revision change is added. Unblocking/restoring an editable contact shows the retained buffer again. |
| Status handling | A separate removal-notice flag keeps the useful archive notice visible even when the newly selected row has an attempt. Any later SetStatus or explicit selection clears the exception; ordinary failed attempts still use their message cards, not a second global failure banner. |
| Settings removal | Uses the same completion helper and flushes drafts before removal. Its confirmation routes through the existing isolated per-window hook; ordinary operation still uses the same MessageBox confirmation. |
| Native smoke / isolated hooks | Compare the removed row by stable id/principal, explicitly assert its endpoint is null, use the actual list selection after the user changes filter. Observe completion of the actual menu Task and view/history/attachment idle within the existing 12-second Wait; never force the condition or manufacture product state. |
| Stronger native CT-13 checks/captures | General-filter first-row selection and archive status; manual archive selection with readable body, empty read-only composer and explicit prompt; exact SQLite draft text/epoch/revision plus in-memory buffer after rendering/flush; blocked draft hidden and restored after unblocking; removal of the last visible contact, refresh staying empty, explicit archive access; original draft still exact after dispose/reopen. Screenshots are configured for each new stage, not claimed captured on macOS. |
| Core regression / specs | New v15 CT-13 scenario asserts exact retained draft and stable archived identity with null endpoint through reopen (5 assertions, contacts 181→186; all existing assertions retained). `contacts.md` records the PM ruling; `contacts-ui-smoke.md` records the stronger native matrix. |

Scope: WPF selection/composer behavior, smoke/observation hooks, additive Core regression, specifications and this verification record. No Core product, migration/schema, wire, journal, CI time limit or daily profile change. Test/build outputs stay under this worktree with real `build/tmp` as TMPDIR. Native package/portable UI and new PNG results require Emily's push and PR #28 CI rerun; IME, physical DPI, two-machine/LAN/classroom acceptance remain unexecuted.

Final local results:

| Check | Result | Exit | Seconds |
|---|---|---|---|
| tests-build | Release Console build, 0 warnings / 0 errors | 0 | 9.05 |
| contacts | 186 assertions (181 retained + 5 new v15 archive/draft checks) | 0 | 2.58 |
| contacts-ui-seed-check | 7 SQLite seed/migration/isolation checks | 0 | 0.83 |
| default-ci | 6,570 behavior checks, unchanged source guards | 0 | 70.19 |
| win-x64-build-final | WPF + native smoke cross-build after final action-observation changes, 0 warnings / 0 errors | 0 | 7.33 |

Evidence: `build/b1-followup/ci-r2-results.json`, `ci-r2-*.log`; final cross-build is `ci-r2-win-x64-build-final.log`. `git diff --check` and strict safety-config validation pass (two previously recorded native/physical unknowns remain). The additive contacts scenario runs on v15; no previous tests or assertions were relaxed. Native smoke's 12/100/115-second and CI's 150-second bounds remain unchanged.

**CI 第 2 輪修正完成（本機）**. New screenshots are implemented as real native capture steps; they have **not** been produced/accepted on this Mac. Emily must push this local commit to the same PR #28 branch and rerun package/portable native smoke to obtain them. No push, new PR, merge, release, install or deployment performed.


### PR #28 CI round 3: portable menu timing, read-only tools and stable avatars (2026-11-08)

Inspected run 42434671689 log (`build/b1-followup/windows-ci-42434671689-failed.log`) and both downloaded JSON/PNG sets under desktop `reports/lite-b1-ci-screenshots-20261108/`. The package JSON reports passed=true / 51 checks / 27 captures. Portable reports 27 checks then `Enumerable.Single` in `ContactsOpenMessageMoreAsync`, with no source line in the single-file stack; `15-failure.png` shows the pending message card and no open popup. Its MD-01 more-menu and body-menu assertions had passed; MD-03 had not yet passed. This is **not** evidence that portable intentionally has different menus.

Confirmed source vulnerabilities: the previous helper performed one Single on the card's visual Button descendants **before any wait**, then after its wait queried the global open-menu enumeration again with another Single. `MessageCard.MessageUpdated` calls Build for action/delivery updates, replacing Content; a realized card alone does not prove its current template/content button subtree is ready. The single-file stack cannot distinguish which Single threw. The fix covers both timing windows, without pretending the old artifact captured a particular intermediate layout frame. Product menu construction has no package/portable branch and always adds copy plus deletion items.

| Scope | Change / evidence |
|---|---|
| `App.ContactsUiSmokeTest.cs` message more helper | One existing 12-second Wait now covers reacquiring the expected card by message reference, its current Content's loaded/visible/enabled ⋯, and the popup whose PlacementTarget is **that button**. Require nonzero menu size and exactly one expected delete item plus one “複製整則原文”. Select/capture the matched menu inside the predicate instead of querying again afterward. Ambiguity remains an immediate failure; missing items fail with last/current actual button labels, automation names, enabled states and open-menu item text. No FirstOrDefault fallback, fake pass, timeout increase or menu injection. MD-03 deliberately emits real RefreshActions→Build before opening the pending menu. |
| Other helper consumer | Initial cross-build exposed the existing `App.MessageRecoverySmoke.cs` consumer; it now uses the same guarded helper and propagates descriptive failures, retaining all deletion/ACK/paging/nickname checks and its original 50-second total bound. The initial CS7036 is recorded in the first build log and is resolved, not waived. |
| `ChatWindow.xaml` settings | ReadReceiptToggle and its help are adjacent in one named StackPanel; only afterward come cache-clean button and separate cache help. Native smoke asserts real visual coordinates/group order before screenshot 01. Cache help matches ClearCacheAsync's draft-reference retention. |
| Read-only composer | InsertCodeButton/CodeLanguage now follow editable composer state; attachment and sticker remain disabled for removed/blocked contacts. CircleIconButton previously had no disabled visual even when IsEnabled was false: it now dims the border and uses an arrow cursor. Disabled tooltips explain read-only state. Native smoke checks all tool entrypoints, the actual paperclip template opacity and cursor in archived, blocked, blocked-teacher and last-archive views before their existing captures. |
| “停止後續重試” | Retained with the **existing** scoped cancel guards: stopping an old failed/unconfirmed attempt is a local operation, not a new send or remote recall. Terminal/cancelled attempts do not gain a new stop action. Message-card tooltip explains this. Native archived fixture requires its pre-existing failed attempt's stop action still available. No queue/attempt/wire logic changed. |
| Avatar cause / PM ruling | Gray was one existing coat preset, not a presence signal. `seed.GetHashCode(Ordinal)` was process-salted, so the same input could choose different coats after restart. Do **not** collapse all contacts to black. Existing nine-preset order/assets and custom-file priority are retained. |
| `PeerAvatarPolicy` (Core pure function) / `CatArtwork` | Prefer stable peer/roster identity over mutable note/number seed; no key uses fallback seed, both blank keep index0. UTF-8 SHA-256, first uint32 in explicit big-endian order, modulo palette count. Custom config remains the first branch. Equal key + palette yields equal mapping regardless of OS/process; this is artwork selection, not presence. Different PCs with different unsynchronized local RosterIds are not magically one identity; cross-peer identity/avatar sync remains batch2. Fresh isolated test profiles create new IDs, so colors need not be identical across different CI fixtures. |
| Additive contacts scenario | CT-14 locks independent SHA vectors (`contact-fixed`→6, `聯絡人甲`→3 for nine presets), blank original fallback, ID over local note, two distinct real child processes, equal mapping in both and parent for 129 synthetic keys, multiple colors and index bounds. 8 new assertions, contacts186→194. Children have no DB/UI/file side effects. Existing 186 assertions retained. Native smoke additionally compares real list avatar pixels against the selected existing coat asset. |

The next runner captures reuse screenshot stages 01 (settings), 03 (avatars), 19/20/22/26 (read-only tools); indices may shift only if future stages are added. They are **configured**, not generated on this Mac. Existing single-step12/global100/watchdog115/CI150-second bounds are unchanged. Source menu diagnostics and explicit rebuild improve failure evidence without weakening the expected UI behavior.

Final local verification (TMPDIR = this worktree’s real `build/tmp`):

| Check | Result | Exit | Seconds |
|---|---|---|---|
| tests-build | Release Console build: 0 warnings / 0 errors | 0 | 9.11 |
| contacts | 194 assertions (186 retained + 8 stable-avatar assertions; two real child processes) | 0 | 2.79 |
| contacts-ui-seed-check | 7 real SQLite seed/isolation checks | 0 | 0.84 |
| default-ci | 6,570 behavior checks, existing source guards unchanged | 0 | 70.29 |
| win-x64-verified | final current WPF source + menu-rebuild fixture: 0 warnings / 0 errors | 0 | 7.27 |

Evidence: `build/b1-followup/ci-r3-results.json`, `ci-r3-*.log`; final current-source cross-build is `ci-r3-win-x64-verified.log`. The initial signature-consumer compile failure remains in the log and was corrected. Source timestamps were checked against the final WPF DLL to ensure the deliberate pending-card rebuild is included. git diff --check and safety-config strict validation pass. No schema, migration, wire, coat asset, existing guard or CI time-limit changes.

**CI 第 3 輪修正完成（本機）**. Native package/portable execution and the modified settings/tool/avatar screenshots still require Emily to push this local commit and rerun PR #28. The previous package 51-check success is retained as historical evidence, not substituted for acceptance of these new changes. IME, physical DPI, two-device/LAN/classroom acceptance remain unexecuted. No push/new PR/merge/release/install/deploy performed.


### Classroom manual EXE first, then PR #28 CI round 4 (2026-11-08)

Step 1 finished **before any round4 source edit**. Source was clean `07ab266b4421e5936bb65a868b128b8747f18fd3` before and after publish. Delivered exactly three files under desktop workspace `reports/lite-b1-test-build/`: `NekoChat-Lite.exe`, `NekoChat-Lite.exe.sha256`, `BUILD-INFO.txt`. The EXE is **225613962 bytes**, SHA-256 **11D52DAB20CFF26BA715758F65DB8B1C1DF253E6BB3F04228012076478DCF3B3**. BUILD-INFO records source, actual command, start/end time and “本機 Mac 交叉發布、非 CI 產物、僅供教室手動試用”. No release, installation or deployment was performed. The later round4 commit does not replace/relabel this immutable 07ab266 test EXE.

Publish uses the CI's Release / win-x64 / self-contained / PublishSingleFile / IncludeNativeLibrariesForSelfExtract settings, including the existing project IncludeAllContentForSelfExtract setting. Initial absolute PublishDir failed SDK Copy with MSB3094 (three sources / one transformed destination) in this apostrophe-containing workspace. Without changing source or bundle flags, a project-relative PublishDir succeeded; the exact output-path-only workaround is disclosed in BUILD-INFO. Logs: `build/b1-followup/classroom-test-publish-07ab266*.log`. `verify_bundle.py` subsequently passed: PE/bundle/assets proof, 415 files, same SHA; `classroom-test-bundle-07ab266.json` explicitly records windowsExecuted=false. This is not a CI-produced binary or proof of physical Windows acceptance.

Immediately reported “測試版 exe 已就緒” + path/size/SHA to Emily's workspace through cmux (message fcc8f1ea-90c2-4ea4-9e92-3cc7f234cb71), then started source edits. Workspace UUID was used after the title alias failed to resolve. Static bundle proof was reported afterward; no silent file replacement.

Round4 baseline: retrieved run42436270908 log confirms `Privacy defaults changed` from alice; `scripts/test-lite-messaging.ps1:64` is the wrapper's FAIL-report reader, not the policy owner. PM reports package and portable contacts smoke each passed62/27PNG; this does not turn the whole failed job green. The stale assumption is in `App.MessagingSmokeTest.cs`; it incorrectly conflated the unchanged preview-OFF default with read-receipt-OFF, which D-28 changed to ON.

#### D-28 old-test comparison table

| Current file:line | Old assertion / report | New assertion / setup | Decision | Historical contract retained? |
|---|---|---|---|---|
| App.MessagingSmokeTest.cs:36–46 | `!ShowReactionPreviews && ReadReceiptToggle==false` as privacy defaults | Preview remains OFF; fresh UI and DB must both be ON and ExplicitChoice=false. Then exercise the actual checkbox Click to explicitly opt out, wait for durable OFF + ExplicitChoice=true **before** either peer's .ready rendezvous. No traffic can precede both opt-outs. | D-28 | Old universal OFF default retired; explicit opt-out privacy retained. Historical v13 default remains covered by seed/migration tests. |
| App.MessagingSmokeTest.cs:64–69 | Outgoing RemoteStoredAt!=null / RemoteReadAt==null described as “default-off” | Same exact persisted-message/ACK/no-remote-read assertion retained, now after explicit UI opt-out. Add OFF/ExplicitChoice and both receipt queues empty. No permissive “null or non-null” replacement. | D-28 | Non-disclosure while user opts out remains fully enforced. |
| App.MessagingSmokeTest.cs:108–109 | Reopened Enabled=false called a default check | Reopened Enabled=false **and** ExplicitChoice=true after the preceding real user choice; reopening must preserve that choice. | D-28 | Explicit disabled preference persists; not a fresh-install default assertion. |
| App.MessagingSmokeTest.cs:113 | PASS text advertised “remote-read default off” | PASS text says default ON checked first, explicit UI OFF before traffic, no remote read/queued receipts while opted out, persisted explicit choice. Wrapper still requires PASS and exit0. | D-28 | Report corrected; no success/exit guard weakened. |
| App.MessagingFixture.cs:27,47 | No dedicated fresh/reopen default assertion | Add ON + ExplicitChoice=false at fresh provisioning and the next unmodified reopen, before UI opt-out. | D-28 | Strengthens default persistence coverage. |

Unchanged guards include preview privacy, no body/IP in notifications, opaque peer reply target, exact two stored messages/raw Unicode/code, signed durable ACK/draft clear, separate unverified legacy scope, timeout retaining one attempt/draft, no invented outgoing history, retry visibility, clipping and orderly restart. No product default, Core, schema, migration, wire or script time limit changed.

Full-repository code/script grep: `build/b1-followup/ci-r4-full-repo-receipt-audit.txt`; initial targeted inventory `ci-r4-receipt-default-audit.txt`. No additional stale default-OFF assertion was found in executable smoke/scripts. Remaining OFF cases are intentional and unchanged:

- `App.DragDropSmokeTest.cs:64` and `ChatWindow.Ui4Preview.cs:25`: explicit fixture policy choice, not a claimed default.
- `App.UnreadSmokeTest.cs:98–108`: actual UI opt-out, then local-unread-clearing/no-queued-receipt contract.
- `Shared/ContactsUiSmokeSeed.cs:29`: **historical v13** enabled=false / generation0 seed, required for real v13→current D-28 upgrade. Do not rewrite history into ON.
- Identity replacement/recovery and offline restore deliberately disable outbound receipts; they are privacy reset flows, not fresh-install default claims.
- Runtime-unavailable UI fallback can show unchecked/disabled, without asserting a default. Console explicit-OFF and archived migration fixtures remain covered by the previously reviewed D-28 changes.

Raw failing CI log: `build/b1-followup/windows-ci-42436270908-failed.log`. Native two-process WPF messaging and PowerShell execution cannot run on this Mac (pwsh unavailable; Windows WPF unavailable); they require Emily's next same-branch CI push. Physical IME/DPI/two-machine/classroom acceptance remains separate and unverified. The authorized classroom EXE remains the original clean-source 07ab266 file above.

Final local checks (real worktree `build/tmp` as TMPDIR):

| Check | Result | Exit | Seconds |
|---|---|---|---|
| tests-build | Release test build 0 warnings / 0 errors | 0 | 9.58 |
| contacts | 194 assertions (D-28 default/choice/receipt regressions included) | 0 | 3.08 |
| receipt-privacy | 7 actual signed service/TCP/SQLite opt-out, disable, no-backfill checks | 0 | 1.1 |
| default-ci | 6,570 behavior checks; source guards unchanged | 0 | 70.48 |
| win-x64-build | WPF / both edited fixture files compile, 0 warnings / 0 errors | 0 | 7.23 |

Logs: `build/b1-followup/ci-r4-results.json`, `ci-r4-*.log`. Final `git diff --check` / existing strict safety-config validation pass. Rehashed the delivered 07ab266 EXE after all round4 work: size and SHA unchanged. No blanket OFF→ON replacement, guard deletion, product change or time-limit extension.

**CI 第 4 輪修正完成（本機）**. Await Emily's push to rerun the native packaged/portable signed messaging smoke and PowerShell wrapper. Native success of this changed fixture is not claimed from Mac Core checks or a cross-build. No push/new PR/merge/release/install/deploy performed.
