# 官網交付資料

這份文件提供給官網維護者，用來上架 Windows 版下載資訊。

**狀態：尚未發布。** 下列下載網址是預計網址，執行 Release 流程後才可下載。檔案大小與雜湊一律以 GitHub Actions 產生的附件為準。

## 版本

| 項目 | 內容 |
|---|---|
| 產品名稱 | What's up my neighbor（Windows 版） |
| 版本 | 0.1.0-beta.3 |
| 發布類型 | 預發布測試版（beta） |
| 授權 | MIT License，版權聲明 Copyright (c) 2026 yuipan0611 |
| 維護與發布 | yuipan0611（本 repo） |
| 主要貢獻者 | andrewchu0101 |

## 安裝檔

| 項目 | 內容 |
|---|---|
| 檔案名稱 | `Neighbor-Setup-0.1.0-beta.3-x64.exe` |
| 大小與 SHA-256 | 發布後以同一 Release 的 `.sha256` 與 `build-manifest.json` 為準 |
| 程式碼簽章 | 未簽章 |
| 校驗檔 | 同一 Release 附 `Neighbor-Setup-0.1.0-beta.3-x64.exe.sha256` |

0.1.0-beta.2 只在貢獻者的 fork 發布過，雜湊與本 repo 的安裝檔不同。官網不要使用 beta.2 的檔案或雜湊。

## 下載網址

| 項目 | 網址 | 狀態 |
|---|---|---|
| 版本頁面 | `https://github.com/yuipan0611/whats-up-my-neighbor-ai-input-windows/releases/tag/v0.1.0-beta.3` | 預計網址，**發布後才可開啟** |
| 直接下載 | `https://github.com/yuipan0611/whats-up-my-neighbor-ai-input-windows/releases/download/v0.1.0-beta.3/Neighbor-Setup-0.1.0-beta.3-x64.exe` | 預計網址，**發布後才可下載** |
| 校驗檔 | `.../releases/download/v0.1.0-beta.3/Neighbor-Setup-0.1.0-beta.3-x64.exe.sha256` | 預計網址，**發布後才可下載** |

- 官網請連到指定版本的頁面或檔案，不要用 `/releases/latest`：GitHub 的 latest 會略過預發布版本，連過去會找不到 beta。
- 發布後請用瀏覽器實際確認直接下載網址可以下載，再更新官網。

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

## 更新摘要（0.1.0-beta.3，相對於 0.1.0-beta.1）

- 新增第一階段桌面小黑貓（預設關閉）：散步、休息，可拖曳。
- 錄音浮窗與桌面小黑貓改用逐像素透明，移除邊緣的紫色外框。
- 錄音改為使用麥克風的混音格式擷取，再轉為 16 kHz 單聲道，解決部分麥克風不接受 16 kHz 串流的問題。
- 版本號統一由 `VERSION` 決定，安裝檔、應用程式版本與發布標籤一致。
- 發布流程改為全部測試通過後才發布，並附上校驗檔與測試紀錄。
- 安裝程式的支援與更新網址指向本 repo，原作者署名與 MIT 授權保留。

完整內容見 [CHANGELOG.md](../CHANGELOG.md) 與 [RELEASE_NOTES.md](../RELEASE_NOTES.md)。

## 與 macOS 版的差異

Windows 版與 macOS 版各自開發，功能不完全對等。Windows 版目前沒有 macOS 版的剪貼簿歷史、iPhone 接力與多人會議功能；桌面小黑貓僅有第一階段的散步、休息與拖曳（預設關閉），沒有舔掌、抓起動畫與資料夾停靠。官網不應宣稱功能完全相同。
