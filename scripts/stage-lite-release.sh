#!/usr/bin/env bash
set -euo pipefail

# Only a trusted main push stages generated binaries into an existing draft.
test "$GITHUB_EVENT_NAME" = push
test "$GITHUB_REF" = refs/heads/main
version="$(python -c 'import xml.etree.ElementTree as E; print(E.parse("windows-lite/NekoChat.Windows/NekoChat.Windows.csproj").findtext("./PropertyGroup/Version"))')"
[[ "$version" =~ ^0\.1\.0-beta\.[0-9]+$ ]]
tag="nekochat-lite-v$version"
draft_id="$(gh api "repos/$GITHUB_REPOSITORY/releases" --jq ".[] | select(.tag_name == \"$tag\" and .draft == true) | .id")"
if [[ -z "$draft_id" ]]; then
  echo "No existing draft for $tag; no release changes."
  exit 0
fi
[[ "$draft_id" =~ ^[0-9]+$ ]]
mkdir -p release-artifacts
gh run download "$GITHUB_RUN_ID" --repo "$GITHUB_REPOSITORY" --name nekochat-windows-lite-setup --dir release-artifacts/setup
gh run download "$GITHUB_RUN_ID" --repo "$GITHUB_REPOSITORY" --name nekochat-windows-lite-portable --dir release-artifacts/portable
gh run download "$GITHUB_RUN_ID" --repo "$GITHUB_REPOSITORY" --name nekochat-windows-smoke-captures --dir release-artifacts/evidence

# Verify exact source and hashes before attaching anything.
python - <<'PY'
import hashlib, json, os, pathlib
root = pathlib.Path("release-artifacts")
provenance = json.loads((root / "setup/release-provenance.json").read_text(encoding="utf-8-sig"))
assert provenance["sourceCommit"] == os.environ["GITHUB_SHA"], "Artifact source does not match main"
for asset in provenance["assets"]:
    path = next(root.glob("*/" + asset["name"]))
    assert hashlib.sha256(path.read_bytes()).hexdigest() == asset["sha256"], path
PY

notes="nekochat/docs/releases/$tag.md"
test -f "$notes"
cp "$notes" release-artifacts/QUICKSTART.zh-TW.md
{
  printf '# NekoChat Lite %s：整合修正版\n\n' "$version"
  printf '**[⬇ 下載 Windows 安裝檔（建議）](https://github.com/%s/releases/download/%s/NekoChat-Setup-win-x64.exe)**\n\n' "$GITHUB_REPOSITORY" "$tag"
  cat "$notes"
  printf '\n## 本版本驗證來源\n\n來源 commit：%s。\n\n[通過的 Windows Lite 建置與驗證](https://github.com/%s/actions/runs/%s)。\n\n附件含兩種執行檔、SHA-256、來源紀錄與 validation-evidence.zip；完整包、portable、Setup 安裝後 App 的鍵盤、收發及散步測試，和真實防火牆規則驗證均須先通過。\n\n標準版 Neighbor 的三項既有測試失敗與 Lite 分開記錄；本次未修改標準版語音功能。\n' "$GITHUB_SHA" "$GITHUB_REPOSITORY" "$GITHUB_RUN_ID"
} > release-artifacts/RELEASE_NOTES.zh-TW.md
(cd release-artifacts/evidence && zip -qr ../validation-evidence.zip .)

test "$(gh api "repos/$GITHUB_REPOSITORY/releases/$draft_id" --jq .draft)" = true
gh release upload "$tag" --repo "$GITHUB_REPOSITORY" --clobber \
  release-artifacts/setup/NekoChat-Setup-win-x64.exe \
  release-artifacts/setup/NekoChat-Setup-win-x64.exe.sha256 \
  release-artifacts/setup/release-provenance.json \
  release-artifacts/portable/NekoChat-Lite.exe \
  release-artifacts/portable/NekoChat-Lite.exe.sha256 \
  release-artifacts/QUICKSTART.zh-TW.md \
  release-artifacts/validation-evidence.zip
test "$(gh api "repos/$GITHUB_REPOSITORY/releases/$draft_id" --jq .draft)" = true
gh release edit "$tag" --repo "$GITHUB_REPOSITORY" --draft=true --prerelease \
  --target "$GITHUB_SHA" --notes-file release-artifacts/RELEASE_NOTES.zh-TW.md
echo "Verified $tag artifacts are attached to the draft; publishing remains a separate action."
