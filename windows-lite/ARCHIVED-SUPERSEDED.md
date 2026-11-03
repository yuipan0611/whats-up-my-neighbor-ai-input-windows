# Preserved signed-V2 / P1 regressions

The fixed classroom LAN/IP requirements supersede signed identity pairing (PMCM R3/R4/R5). Source, frozen designs/vectors and all old checks remain present. Default Core execution counts current/shared checks separately and does not run the historical group.

- `--archived-superseded` runs the preserved group and labels its result separately.
- `--all-with-archived` runs both groups with separate counts.
- Existing focused signed/P1 arguments still work and now print an archived label.
- `NekoChat.Tests/ArchivedSupersededChecks.cs` lists the exact preserved calls. No historical assertion or frozen vector was removed to obtain a current green result.

This group includes signed-wire/parser/nonce/security-revision checks, signed TCP/receipt/presence and related crash tests, identity vault/recovery and all P1 pairing tests. Shared storage, legacy compatibility, presentation, roster and text/draft boundary regressions still run by default; those shared regressions alone do not establish the new classroom runtime works.

The historical intermittent Release P1 repair failure remains **UNRESOLVED**. Earlier failing evidence is retained in `build/delivery-standalone-20261103/logs/core.log` and the Codex verification record. A later historical green run does not prove it fixed. New classroom receive/send/receipt/clear paths still require actual SQLite transaction-boundary process kills and independent final acceptance.

Explicit isolated native signed/P1 fixture entry points are retained for historical engineering use, not exposed by ordinary product startup/settings. Windows native execution remains unperformed on this Mac.
