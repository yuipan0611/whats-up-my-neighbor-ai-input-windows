# NekoChat Lite beta.10 交付與驗證

本版由 GitHub Actions 建置 Windows x64 自含產物，不需另外安裝 Python 或 .NET。版本與來源以同版 `release-provenance.json`、SHA-256 及 Release 連結的 CI 為準；不混用先前本機候選包。

- `NekoChat-Setup-win-x64.exe`：建議下載。內嵌同批完整 App，驗證新安裝、升級、啟動與保留個人設定／自訂檔案；防火牆授權由有權限者決定。
- `NekoChat-Lite.exe`：真正單檔免安裝，以 all-content extraction 保留素材。需要可寫入的 Windows 暫存區，不自行配置防火牆。
- `.sha256`：同版檔案校驗；`release-provenance.json`：實際版本、FileVersion、來源 commit、CI run、大小及雜湊。
- `validation-evidence.zip`：隔離資料的原始 Windows smoke 結果與畫面。歷史合成 signed fixture 不代表產品重新啟用配對。
- `QUICKSTART.zh-TW.md`：同版功能、安裝與限制。課堂專用 IP sidecar 不隨公開包發布。

CI 必須先通過 Core、教室附件、三節點課堂分享、WPF、雙程序訊息、更新、安裝升級與防火牆回復；僅信任 main 的成功 run 在既有草稿附上相同來源及雜湊產物，最後發布是另一步。

本版新增啟動故障隔離：附件路徑衝突／未完成檔鎖定時，停用附件但保留文字收發與待傳草稿；修復後重啟恢復。資料庫損壞或無法升級仍停止收發，避免錯誤寫入。重要附件須另外另存，資料庫備份不含附件內容，舊版無法直接讀取新版資料庫。

原始本機驗證與失敗紀錄見專案 `docs/attachments/`、`docs/classroom-ux/`。未完成的現場項目：兩台實體 Windows、實際教室網路與多人負載、其他 DPI／多螢幕、實裝 IME。Windows CI 與 Parallels 同機證據不代替以上驗收；beta.10 維持未簽署的預發布測試版。

## 先前發布紀錄

上一個公開測試版為 [beta.9](https://github.com/yuipan0611/whats-up-my-neighbor-ai-input-windows/releases/tag/nekochat-lite-v0.1.0-beta.9)，來源 `ecbed3dd9b679b8569b44fda80243d8f352d8b1e`，對應 [CI 41834511796](https://github.com/yuipan0611/whats-up-my-neighbor-ai-input-windows/actions/runs/41834511796)。該版產物與紀錄保留，不冒充 beta.10；beta.10 的實際來源以同版 provenance 為準。
