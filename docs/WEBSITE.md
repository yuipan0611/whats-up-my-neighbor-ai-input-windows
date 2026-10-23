# 官網交付資料

這份文件提供給原作者或官網維護者，用來上架 Windows 版下載資訊。

**狀態：尚未發布。** 下列下載網址是預計網址，目前不可下載。正式發布後，雜湊與檔案大小必須以 GitHub Actions 產生的附件為準，不可沿用本文件的數值。

## 版本

| 項目 | 內容 |
|---|---|
| 產品名稱 | What's up my neighbor（Windows 版） |
| 版本 | 0.1.0-beta.2 |
| 發布類型 | 預發布測試版（beta） |
| 授權 | MIT License，版權聲明 Copyright (c) 2026 yuipan0611 |
| 原作者 | yuipan0611 |
| Windows 版維護者 | andrewchu0101（本 fork） |

## 安裝檔

| 項目 | 內容 |
|---|---|
| 檔案名稱 | `Neighbor-Setup-0.1.0-beta.2-x64.exe` |
| 大小（本機打包） | 2,817,927 bytes（約 2.7 MB；上限 20,000,000 bytes） |
| SHA-256（本機打包，僅供參考） | `959f01fc2e036b354ec60a01d34dec38ed55802a58b584073b59da59facd5548` |
| 程式碼簽章 | 未簽章 |
| 校驗檔 | 同一 Release 附 `Neighbor-Setup-0.1.0-beta.2-x64.exe.sha256` |

**正式發布時請替換**：上表的大小與 SHA-256 來自本機建置，對應的 commit 為 `9e767ea`，而且目前的改動尚未提交。正式版請以同一次 GitHub Actions 產生的 `Neighbor-Setup-<版本>-x64.exe.sha256` 與 `build-manifest.json` 為準。

## 下載網址

| 項目 | 網址 | 狀態 |
|---|---|---|
| Release 頁面 | `https://github.com/yuipan0611/whats-up-my-neighbor-ai-input-windows/releases` | 預計網址（上游 repo，尚未建立 Release） |
| 直接下載（預計） | `https://github.com/yuipan0611/whats-up-my-neighbor-ai-input-windows/releases/download/v0.1.0-beta.2/Neighbor-Setup-0.1.0-beta.2-x64.exe` | 預計網址，**尚不可下載** |
| 校驗檔（預計） | `.../releases/download/v0.1.0-beta.2/Neighbor-Setup-0.1.0-beta.2-x64.exe.sha256` | 預計網址，**尚不可下載** |

發布後請用瀏覽器實際確認直接下載網址可以下載，再更新官網。

## 系統需求

- Windows 11 x64，或 Windows 10 22H2 x64。
- 系統內建 .NET Framework 4.8（Windows 10 22H2 與 Windows 11 預設已有；安裝程式會先檢查）。
- 麥克風與網路。
- 使用者需要自己的 Groq API Key。App 不會購買額度，費用依 Groq 帳號方案計算。
- 不需要系統管理員權限。
- ARM64 未驗證。

## 安裝與注意事項（官網可直接使用的文字）

1. 下載 `Neighbor-Setup-<版本>-x64.exe`，並核對 SHA-256。
2. 雙擊執行。Windows 可能顯示「Windows 已保護你的電腦」，請點選「其他資訊」→「仍要執行」。這是因為安裝檔沒有程式碼簽章。
3. 安裝完成後，從開始功能表開啟 What's up my neighbor，並儲存自己的 Groq API Key。
4. 按 Ctrl + Alt + Space 開始錄音，再按一次完成。文字會貼到目前的輸入框，不會自動送出。

這是預發布測試版。真實麥克風、各應用程式與中文輸入法等項目仍待實機驗收，詳見 VALIDATION.md。

## 更新摘要（0.1.0-beta.2）

- 錄音改為使用麥克風的混音格式擷取，再轉為 16 kHz 單聲道，解決部分麥克風不接受 16 kHz 串流的問題。
- 版本號統一由 `VERSION` 決定，安裝檔、應用程式版本與發布標籤一致。
- 發布流程改為全部測試通過後才發布，並附上校驗檔與測試紀錄。
- 安裝程式的網址指向原作者的 Windows repo，原作者署名與 MIT 授權保留。

完整內容見 [CHANGELOG.md](../CHANGELOG.md) 與 [RELEASE_NOTES.md](../RELEASE_NOTES.md)。

## 與 macOS 版的差異

Windows 版與 macOS 版各自開發，功能不完全對等。Windows 版目前沒有 macOS 版的剪貼簿歷史、iPhone 接力與多人會議功能；桌面小黑貓僅有第一階段的散步與休息（預設關閉），沒有拖曳、舔掌與資料夾停靠。官網不應宣稱功能完全相同。
