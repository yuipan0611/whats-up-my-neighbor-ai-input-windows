# Preserved signed-V2 / P1 regressions

The fixed classroom LAN/IP requirements supersede signed identity pairing (PMCM R3/R4/R5). Source, frozen designs/vectors and all old checks remain present. Default Core execution counts current/shared checks separately and does not run the historical group.

- `--archived-superseded` runs the preserved group and labels its result separately.
- `--all-with-archived` runs both groups with separate counts.
- Existing focused signed/P1 arguments still work and now print an archived label.
- `NekoChat.Tests/ArchivedSupersededChecks.cs` lists the exact preserved calls. No historical assertion or frozen vector was removed to obtain a current green result.

M1 keeps these shared regression guards in the default current/shared group: `SecurityRevisionChecks`, `V2StorageFenceChecks`, `V2BoundaryChecks`, `PresenceChecks` (including ABA), `PresenceWireChecks`, and the first seven D-10 privacy assertions of `ReceiptServiceChecks`. Some use signed fixtures to exercise shared implementations; their success does not establish classroom protocol/runtime acceptance.

The archived group retains signed vectors/TCP/crash flows, `SharedPortChecks` (mixed historical routing fixture), passive identity, identity vault/recovery and all P1 pairing tests. `ReceiptServiceChecks.RunArchivedProtocol` retains the remaining four protocol/trust/legacy-event assertions; it prepares the same fixture without rerunning/counting the seven privacy assertions. The existing focused `--receipt-worker` still runs all eleven once, and new `--receipt-privacy` runs the seven shared ones; `--receipt-archived` runs the remaining four. No assertion or frozen vector is removed.

The historical intermittent Release P1 repair failure remains **UNRESOLVED**. Earlier failing evidence is retained in `build/delivery-standalone-20261103/logs/core.log` and the Codex verification record. A later historical green run does not prove it fixed. New classroom receive/send/receipt/clear paths still require actual SQLite transaction-boundary process kills and independent final acceptance.

Explicit isolated native signed/P1 fixture entry points are retained for historical engineering use, not exposed by ordinary product startup/settings. Windows native execution remains unperformed on this Mac.
