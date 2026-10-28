# Native cat animation integration

Owner-approved nine-coat animations and tuxedo muzzle correction, shared with the macOS renderer. `PetWindow` in the normal Windows Lite app consumes this module. The existing chat, voice, settings and credentials remain in their original owners.

Source: macOS commit `ef9fe89f1f78698453ed13d3cfba4181fc326537`, renderer SHA-256 `ce8746f213326c1ad2aa9c829b30cb5f1c00ccf5c3eb100d4a18c46901b09eeb`. `Assets/manifest.json` records the source, native lift timestamps and original frame indices. Nine preset IDs and `Examples/CatAppearance` are unchanged.

- 850ms pickup, 400ms landing, ending at native frame 35 without rebound. Early release and re-grab retain native forward/reverse travel.
- Walking in both directions includes native first-step easing; other frames are sampled directly from `DesktopPetView` for grooming, yawning, loafing, sleeping, curious and petted poses. Only the original silhouette turns; X/check glyph handedness is retained.
- Selecting an internal coat keeps pose/position and persists the same `Settings.Coat` field. Imported artwork remains static and temporary; choosing a built-in coat resumes animation.
- The Lite desktop cat uses the native sitting/standing/journey/grooming/yawning/loafing/sleeping activity order, randomized destinations and smoothstep movement. Foot phases follow actual travel distance rather than elapsed time. Visible message bubbles, dragging and an open context menu pause autonomous motion. The persistent app-specific automatic-wandering toggle defaults on independently of Windows client-area animations; no OS setting is changed. Intentional drag retains the approved lift sequence.
- Artwork parity does not imply OS integration parity: macOS folder perches/desktop-object jumps are not implemented by the Windows Lite host. See `docs/LITE-ANIMATION-PARITY.md`.
- WPF caches at most 64 decoded frames. One ZIP is embedded in `NekoChat.Animation.dll`, preserving the installer's 4,096-file and 512 MiB limits. The installer requires this DLL and still stages transactional upgrades.
- All assets are local. No download, telemetry, external service, user image recoloring or new configuration fields.

## Reproduce

From the Windows repository root, with .NET 10:

```sh
dotnet run --project windows-lite/NekoChat.Tests -c Release
dotnet run --project windows-lite/NekoChat.Animation.Tests -c Release -- windows-lite/NekoChat.Animation/Assets/manifest.json
dotnet build windows-lite/NekoChat.Windows -c Release -p:RestoreLockedMode=true
```

Regeneration requires macOS, Swift and a macOS repository containing the pinned commit:

```sh
zsh windows-lite/NekoChat.Animation/tools/export.sh /path/to/macos-checkout
```

This regenerates native animation frames, the nine static preset PNGs and native coverage maps for the Windows custom renderer. The original tuxedo mouth problem was a narrow anatomical triangle crossing the omega stroke. The approved renderer surrounds the whole muzzle with a curved white blaze and joins it to the bib. Windows samples the exported coverage instead of duplicating those curves. Custom per-part colors remain supported; patterned overrides retain their original rendering path.

The existing YUI artwork provenance and MIT notice are in `Assets/SOURCE-ARTWORK.md` and `Assets/LICENSE`. No reference photograph is distributed. Public release/install state is separate from source integration; physical DPI, mouse gestures and classroom hardware still need interactive acceptance beyond CI.
