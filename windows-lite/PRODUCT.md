# Neko Chat Lite

<!-- impeccable:product-schema 1 -->

## Platform

Windows desktop, WPF/.NET 10. Mac hosts Core tests and cross-builds; WPF/IME/DPI/DPAPI native execution requires Windows.

## Users

Teachers and students exchanging classroom LAN messages, code and five existing classroom reactions, with the existing desktop cat.

## Product Purpose

Keep separate local conversations, drafts and unread state across ordinary restarts. Attribute classroom conversations using accepted socket source IPv4 and roster/IP principals; confirm stored messages only after guarded SQLite commits. Explicit Send persists an independent outbox item and releases the composer. Temporary disconnection retries automatically; permanent errors require user action. Pre-upgrade failures remain manual until explicitly retried. See docs/bugfixes/20261105-chat-lifecycle.md.

## Capabilities and Constraints

Seats, people and IPs are fixed within the classroom LAN. Manual roster editing and CSV preview/import assign addresses without keys or number matching. Outbound read receipts default off; local visible-read is independent. Desktop message notifications show name and “新訊息”, without body. Materials are deferred by the user. Probe hello before any user text; only the exact baseline legacy error response enables a v1 candidate. Legacy sends retain the 2,000-scalar limit and label, never imply stored/read confirmation, and cannot be used as fallback after classroom message bytes may have been written.

D-04 uses classroom LAN/IP attribution, without product key/pairing/recovery flows. D-06 is confirmed: 65,536 UTF-8 bytes per message. Draft persistence has its own 1,048,576-byte bound (CM-12), preserving over-message-limit text without truncation. Signed/P1 sources and tests are archived-superseded. The classroom transport/store/UI integration is still in progress; these requirements are not a completed runtime claim. No deployment, release, install, firewall changes or main merge authorization.

## Brand Commitments

Preserve approved cat/reaction assets and animation. The user approved only the displayed normal1440 and narrow440 Inbox/conversation direction, linked below; no new visual identity is requested. Traditional Chinese product copy.

## Evidence on Hand

- Visual authority: `/Users/panh.c/Desktop/NekoChat Lite/docs/team/ui/preview/evidence/01-default-1440.jpg` and `05-narrow440-conversation-optA.jpg`, backed by adjacent HTML/CSS.
- Requirements/decisions: `/Users/panh.c/Desktop/NekoChat Lite/docs/03-srs.md`, `DECISIONS.md`, `team/QUESTION-QUEUE.md`.
- Detailed current implementation evidence: `build/wire-evidence/` in the worktree and `/Users/panh.c/Desktop/NekoChat Lite/docs/team/CODEX-VERIFICATION.md`.
