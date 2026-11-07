# Codex verification

## 2026-11-07 B1-a — S0 and implementation plan

Scope: local branch codex/lite-chat-ux-b1-20261107, starting 0651128; no WPF, push, PR, release or installation.

Impact (high risk): SQLite migration/restore attribution, classroom receive and attachment/legacy ingress, roster merge/delete/teacher, receipt scheduling. Frozen wire and SchemaV1 remain unchanged. Existing epoch/revision, socket attribution, dedup/clear tombstones, immutable send snapshots and policy generation must survive.

Plan: (1) clarify four BDD specs, (2) additive v14 with pre-upgrade backup and Core contact/teacher/read preference APIs, (3) receive/merge/receipt integration plus ID-prefixed scenarios, (4) focused and required regressions, win-x64 cross-build, reviewable local commits. RR-02 migration follows Emily's 2026-11-07 ruling: disabled generation=0 becomes enabled, disabled generation>0 stays off; record explicit choices going forward. SQLite teacher state is authoritative; JSON projection is B1-b.

Baseline outbox running in isolated build/tmp; initial sandboxed SDK launch was terminated before output and restarted with loopback/build access. Evidence logs: build/b1-a-evidence/ (untracked build output).
