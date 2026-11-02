# Neko Chat Lite

<!-- impeccable:product-schema 1 -->

## Platform

Windows desktop, WPF/.NET 10. Mac hosts Core tests and cross-builds; WPF/IME/DPI/DPAPI native execution requires Windows.

## Users

Teachers and students exchanging classroom LAN messages, code and five existing classroom reactions, with the existing desktop cat.

## Product Purpose

Keep separate local conversations, drafts and unread state across ordinary restarts. Confirm stored messages using signed identities and guarded SQLite commits. Offline messages remain drafts and require manual send/retry.

## Capabilities and Constraints

Manual roster labels and CSV preview/import never grant trust or pair identities. Outbound read receipts default off; local visible-read is independent. Desktop message notifications show name and “新訊息”, without body. Materials are deferred by the user. Normal private messages have no legacy v1-send fallback; incoming legacy history remains explicitly unverified.

Identity policy Q-D04-P and message-size Q-D06 remain open. Do not silently choose P1/P2 or an L default. Existing keys may be inspected; loss or mismatch never causes automatic replacement. No deployment, release, install, firewall changes or main merge authorization.

## Brand Commitments

Preserve approved cat/reaction assets and animation. The user approved only the displayed normal1440 and narrow440 Inbox/conversation direction, linked below; no new visual identity is requested. Traditional Chinese product copy.

## Evidence on Hand

- Visual authority: `/Users/panh.c/Desktop/NekoChat Lite/docs/team/ui/preview/evidence/01-default-1440.jpg` and `05-narrow440-conversation-optA.jpg`, backed by adjacent HTML/CSS.
- Requirements/decisions: `/Users/panh.c/Desktop/NekoChat Lite/docs/03-srs.md`, `DECISIONS.md`, `team/QUESTION-QUEUE.md`.
- Detailed current implementation evidence: `build/wire-evidence/` in the worktree and `/Users/panh.c/Desktop/NekoChat Lite/docs/team/CODEX-VERIFICATION.md`.
