# Windows Lite upgrade cleanup — 2026-10-31

The installer previously kept a complete `.nekochat-previous-*` folder after every upgrade because custom artwork could live inside it. The replacement transaction now keeps rollback until launch acceptance, verifies the installed payload, preserves modified/unrecognized files under the per-user `UpgradePreservedFiles` data directory, then removes owned old program folders. Subsequent installs carry a hash manifest, so obsolete managed files can be removed without treating user changes as disposable.

The existing checksum-verified embedded ZIP, stable install path, settings and firewall/UAC flow remain. Closing before launch or rejecting the launch restores the prior App. Cleanup failures after a successful launch retain the new App and report the leftover path. Reparse points and unknown backup names are protected. A directory-specific exclusive file handle serializes replacements; an empty lock file is retained for safe reuse.

Legacy backups have no ownership manifest. Only folders with the exact installer-generated name and full required-runtime layout are migrated. Current-payload program filenames are treated as program files; changed resources and unknown files get verified content-addressed recovery copies before anything is deleted. Identical relative paths and content are not recopied. Unrecognized or malformed ownership data is retained and reported.

## Validation

- Baseline Lite core: 3,920 checks passed before changes.
- Updated Lite core: 3,966 checks passed, including 46 new fresh/repeated upgrade, cancellation, launch failure, post-copy tamper, legacy migration, custom/modified resource preservation, deduplication, locked-file retry, recovery-copy failure, concurrency and reparse-point checks.
- WPF installer cross-build: zero warnings/errors with the workspace .NET 10.0.403 SDK.
- Native Windows Setup smoke now launches the real installed App before committing a fresh install and upgrade, then checks that no prior-program backup remains and synthetic custom artwork/settings survive.
- Native Windows CI results are tracked in the PR. macOS-hosted core checks and cross-build do not establish actual Windows packaging/startup success.

The standard Inno installer already overwrites its fixed program location without creating Lite backup folders; it is unchanged. Its existing main-branch Windows workflow at `ff376c0` has 584 passes/3 failures (run 40663261004), separate from this Lite change.

Windows currently checks/notifies releases and opens the download page; users still execute Setup. This patch implements automatic cleanup after successful installation, not unattended downloading/installation. No version bump, public release, real user installation or firewall change is part of this patch.
