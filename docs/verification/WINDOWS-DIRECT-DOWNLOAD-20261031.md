# Windows consent-driven update download — 2026-10-31

The update confirmation previously ignored the release's installer URL and opened HtmlUrl in a browser. Both editions now interpret consent as a request to download the matching Setup directly. They show native progress/cancel controls and offer installer execution only after verification completes. Opening Setup and closing the running app requires a separate explicit choice; the standard app also protects active dictation.

## Implementation boundaries

- Both release parsers select the edition-specific Setup plus its matching checksum, independent of asset order. A portable EXE cannot be mistaken for Setup.
- A shared net48/net10 downloader accepts only this repository's HTTPS release assets and matching version/asset checksums. It manually bounds trusted GitHub/CDN redirects, transfer size and timeout, streams to one partial file, checks length/SHA-256/MZ, then atomically rotates one fixed installer cache per edition.
- Cancel/failure removes partial downloads, preserves an existing verified cache, and never starts a process. Reparse paths, concurrent cache writers, untrusted redirects, malformed checksums and incomplete/oversized bodies are rejected.
- Windows Internet-zone metadata is written in ASCII and system security prompts remain under OS control. SHA-256 verification does not claim Authenticode signing.
- Progress callbacks are throttled; a late cancel suppresses the install offer. Background version checks still only notify.

## Validation before PR CI

- Baseline Lite: 3,966 checks passed.
- Updated Lite: 4,017 checks passed on macOS, including 51 direct-download verification/progress/cancellation/boundary checks. Windows adds Internet-zone metadata coverage.
- Standard net48 test project and Lite WPF app cross-builds pass with zero warnings/errors.
- A real public beta.8 Setup (284,038,860 bytes) was downloaded through the production GitHub/CDN path and SHA-256 verified. It was never executed. Temporary probe artifacts are under build/direct-download.
- Shared checks run under both target frameworks. New native WinForms/WPF smoke cases exercise verified completion, the actual cancel button, checksum failure and partial-file cleanup with synthetic HTTP only. Native Windows results are tracked in the PR; cross-build success alone does not establish native behavior.

No public release or version bump is included. Existing beta.8 binaries require a manual upgrade once to receive this client-side fix. Existing standard Windows desktop-pet test failures are tracked separately from this change.
