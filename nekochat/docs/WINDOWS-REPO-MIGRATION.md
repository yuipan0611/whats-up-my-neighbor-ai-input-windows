# NekoChat Lite → Windows 專用 repo（2026-10-27）

使用者指定 Windows 實作應提交至 `yuipan0611/whats-up-my-neighbor-ai-input-windows`。本次從該 repo 最新 main `c150f28f2c286335697fe3a6cd760c2e150b9f07` 開新分支 `codex/nekochat-windows-lite`，不直接 push／合併 main。

## 來源與範圍

原始實作：`yuipan0611/whats-up-my-neighbor-ai-input-macos` commit `ed340c4556ae9794bb2bf9b81319d62ae1a857d9`（Mac PR #35 草稿）。只搬移已提交的 `windows-lite/`、`nekochat/`、九個 `Examples/CatAppearance/*.json` 及專用 CI；不搬 `.git`、建置輸出、環境、金鑰、個人設定、Swift App 或 Office。Windows 不需要的 `windows-lite/tools/` Swift 素材匯出工具保留原來源，Windows App 使用相同已匯出的 PNG／解剖遮罩。專用 CI 的顯示名稱加上 NekoChat Lite，避免與既有 `windows` 檢查混淆。

`Neighbor.Core/`、`Neighbor.Windows/`、`Neighbor.Tests/`、既有 `assets/`、`packaging/`、`scripts/`、版本與發布 workflow 不修改。Lite 使用不同的安裝目錄與 OS 憑證名稱；原版安裝大小上限與發布規則保留，兩套測試在獨立 CI jobs 執行。

## 保護與驗證

搬移前 Windows main 的 [Windows CI](https://github.com/yuipan0611/whats-up-my-neighbor-ai-input-windows/actions/runs/39901163401) 已通過。Lite 的原來源 [Windows/Linux CI](https://github.com/yuipan0611/whats-up-my-neighbor-ai-input-macos/actions/runs/40390552214) 已通過，含 40 Python、92 C# 檢查（實際 WebSocket 整合為 94），獨立房間服務、Setup.exe 新裝／更新／保留資料、捷徑與實際啟動；這是來源證據，不冒充本 Windows repo 的驗證。

此 Windows repo 會重新執行原有 Windows CI 與新的 NekoChat CI，並在 Windows PR 記錄結果與專屬安裝包下載來源。實體教室雙機網路、防火牆／AP 隔離、真實麥克風與第三方 App 文字欄位仍待人工驗收。沒有 VPS 部署、正式發布、安裝既有 App 或合併 main。


搬移後本機驗證：92 項 C# 行為檢查、40 項 Python Relay／真實 C# WebSocket 整合通過；WPF App 與 Setup 專案編譯皆為零警告／零錯誤。所有 C#、XAML、Python、JSON、PNG 與二進位遮罩以原始已提交內容搬移；只調整說明文件、Windows repo 的 CI 顯示名稱與 build 忽略規則，沒有改寫執行邏輯。新增 SDK／虛擬環境位於桌面工作區的 dependencies／此 worktree 的 build，未提交至 Git。
