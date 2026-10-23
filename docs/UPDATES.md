# 發布、校驗與升級

對照版本：0.1.0-beta.2。

## 使用者看到的行為

- App 不會自動檢查更新，也不會背景下載或安裝。
- 要升級時，到 [Releases](https://github.com/yuipan0611/whats-up-my-neighbor-ai-input-windows/releases) 下載新版的 `Neighbor-Setup-<版本>-x64.exe` 並執行。
- 安裝程式會保留設定（`%LOCALAPPDATA%\WhatsUpMyNeighbor`）、詞典與 Windows 認證管理員中的 API Key。
- 安裝程式會關閉正在執行的 App（`CloseApplications=yes`），升級後不會自動重新啟動。

## 簽章與 SmartScreen

- 安裝檔**沒有**程式碼簽章（Authenticode）。Windows 第一次執行時可能顯示「Windows 已保護你的電腦」，使用者可點選「其他資訊」→「仍要執行」。
- 目前沒有購買程式碼簽章憑證。就算之後購買，也不保證消除所有 SmartScreen 提示，提示會隨下載量與信譽累積而變化。
- 使用者應從本 repo 的 Releases 下載，並核對 `.sha256`。不要引導使用者關閉系統防護。

## 保存資料

| 項目 | 位置 | 升級時 | 解除安裝時 |
|---|---|---|---|
| Groq API Key | Windows 認證管理員，目標 `WhatsUpMyNeighbor/GroqApiKey` | 保留 | 保留（使用者可先在 App 中移除） |
| 設定 | `%LOCALAPPDATA%\WhatsUpMyNeighbor\settings.json` | 保留 | 保留 |
| 詞典 | `%LOCALAPPDATA%\WhatsUpMyNeighbor\vocabulary.json` | 保留 | 保留 |
| 暫存錄音 | `%LOCALAPPDATA%\WhatsUpMyNeighbor\Recordings` | 啟動時清理 | 刪除 |
| 程式檔案 | `%LOCALAPPDATA%\Programs\WhatsUpMyNeighbor` | 覆蓋為新版 | 刪除 |
| 登入啟動項目 | `HKCU\...\Run\WhatsUpMyNeighbor` | 不變 | 移除 |

升級保留的驗證是用合成資料完成的（見 [TESTING.md](TESTING.md)）。尚未以真實使用者的舊版資料完整驗證。

## 發布流程

發布由 `.github/workflows/release.yml` 執行。

1. 更新 [VERSION](../VERSION)，例如 `0.1.0-beta.3`，並更新 [CHANGELOG.md](../CHANGELOG.md) 與 [RELEASE_NOTES.md](../RELEASE_NOTES.md)。
2. 提交到 `main`，確認 `windows.yml` 通過。
3. 建立並推送標籤 `v<VERSION>`，例如 `git tag v0.1.0-beta.3`、`git push origin v0.1.0-beta.3`。也可以在 Actions 頁面手動執行 **Release**，它會以 `v<VERSION>` 建立標籤（只允許從 `main` 執行）。
4. 流程依序執行：安裝 Inno Setup 6、檢查標籤與 `VERSION` 一致、建置、離線測試、UI 整合測試、打包、安裝測試、收集證據。
5. 只有上述全部成功，`publish` 任務才會繼續：驗證安裝檔雜湊、清單的版本與 `sourceCommit`、安裝檔名稱，然後建立預發布版本。
6. 若同一版本的 Release 已存在，流程會停止，不會覆蓋。要重新發布請提高 `VERSION`。

Release 附件：

| 檔案 | 內容 |
|---|---|
| `Neighbor-Setup-<版本>-x64.exe` | 安裝程式 |
| `Neighbor-Setup-<版本>-x64.exe.sha256` | SHA-256 校驗碼 |
| `build-manifest.json` | 來源 commit、工作流程編號、大小、雜湊與檔案清單 |
| `validation-evidence.zip` | 離線與 UI 測試報告、安裝測試紀錄與安裝日誌 |

## 驗證範圍

- 自動化：建置、離線測試、UI 整合測試、打包、安裝測試（見 [TESTING.md](TESTING.md)）。
- 人工：安裝、首次設定、真實錄音、各應用程式貼上、中文輸入法等，依 [TESTING.md](TESTING.md) 的清單執行。
- 發布說明必須區分上述兩者，不能把尚未人工驗收的項目寫成已完成。

## 官網交付

官網的版本、下載網址、雜湊與系統需求資料整理在 [WEBSITE.md](WEBSITE.md)。發布後，雜湊與下載網址必須以本次發布的產物為準。
