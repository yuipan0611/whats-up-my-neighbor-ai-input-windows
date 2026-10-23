What's up my neighbor Windows {{VERSION}}（預發布測試版）

這是 Windows 版語音輸入工具的**預發布測試版**。下方的測試範圍分開列出；請特別注意「尚未完成的實機驗收」。

Windows 版程式碼的原始專案與授權聲明來自 yuipan0611（見 LICENSE 與 MIGRATION.md）。本 Release 由 andrewchu0101 維護的 Windows fork 發布。

## 一般使用者：下載一個檔案

下載 **`Neighbor-Setup-{{VERSION}}-x64.exe`**，雙擊執行即可安裝。

- 不需要另外安裝 Python、Node.js 或其他開發工具。
- 支援 Windows 11 x64 與 Windows 10 22H2 x64。系統需內建 .NET Framework 4.8；Windows 10 22H2 與 Windows 11 預設已有，安裝程式會先檢查。
- 安裝到目前使用者的個人資料夾，**不需要系統管理員權限**。
- 安裝後從開始功能表開啟 **What's up my neighbor**，右下角系統匣會出現小黑貓。

## 第一次使用

新增第一階段桌面散步小黑貓，預設關閉，可從系統匣「桌面小黑貓散步」開啟或關閉。錄音與辨識期間隱藏。桌面互動仍待人工驗收；本版可拖曳並水平散步，尚無舔掌、抓起動畫或資料夾停靠。

1. 到 [Groq 控制台](https://console.groq.com/keys) 建立自己的 API Key。
2. 在系統匣的黑貓上按滑鼠右鍵，選擇 **設定／更換 Groq 金鑰…**，把 Key 貼入安全輸入欄並儲存。Key 只存在 Windows 認證管理員中。
3. 點一下想輸入文字的欄位，按 **Ctrl + Alt + Space** 開始錄音，再按一次完成。文字會貼到目前的輸入框，**不會自動按 Enter**。

## Windows 的安全提示

這個版本**沒有數位簽章**，Windows 第一次執行時可能出現「Windows 已保護你的電腦」。這代表安裝檔沒有程式碼簽章，不代表檔案有問題。

- 點選 **其他資訊**，再點選 **仍要執行**。
- 只從本專案的 Release 頁面下載，並核對 SHA-256（見下方）。
- 請**不要**為了安裝而關閉 Windows 安全性或防毒軟體。學校或公司電腦若限制安裝，請聯絡電腦管理者。

核對 SHA-256（PowerShell）：

```powershell
Get-FileHash .\Neighbor-Setup-{{VERSION}}-x64.exe -Algorithm SHA256
```

輸出的雜湊值應與 `Neighbor-Setup-{{VERSION}}-x64.exe.sha256` 的內容相同。

## 本版變更

- 麥克風改用裝置自己的混音格式錄音，錄完再轉為 16 kHz 單聲道。先前直接要求 16 kHz 串流，部分麥克風會拒絕，導致錄音無法開始。
- 版本號統一由 `VERSION` 檔案決定，安裝檔名稱、應用程式版本與發布標籤一致。
- 發布流程改為：全部測試與安裝測試通過後，才建立預發布版本並附上校驗檔與測試紀錄。
- 安裝程式的發行者、支援與更新網址指向原作者的 Windows repo；原作者署名與 MIT 授權保留。

## 升級

直接執行新版安裝檔即可。安裝程式會保留設定、詞典與 API Key。

升級的自動測試是用合成資料完成的，尚未以真實使用者的舊版資料完整驗證。升級前如果想保險，可先複製 `%LOCALAPPDATA%\WhatsUpMyNeighbor` 資料夾。

## 解除安裝

到 Windows **設定 → 應用程式** 移除 What's up my neighbor。設定、詞典與金鑰預設保留；完整清除步驟見 [使用說明.txt](https://github.com/yuipan0611/whats-up-my-neighbor-ai-input-windows/blob/main/%E4%BD%BF%E7%94%A8%E8%AA%AA%E6%98%8E.txt)。

## 本版附件

| 檔案 | 用途 |
|---|---|
| `Neighbor-Setup-{{VERSION}}-x64.exe` | 安裝程式，一般使用者下載這個 |
| `Neighbor-Setup-{{VERSION}}-x64.exe.sha256` | 安裝檔的 SHA-256 校驗碼 |
| `build-manifest.json` | 本次建置的來源 commit、大小、雜湊與檔案清單 |
| `validation-evidence.zip` | 本次建置的自動化測試與安裝測試紀錄 |

所有附件都來自同一次 GitHub Actions 建置，可用 `build-manifest.json` 的 `workflowRun` 與 `sourceCommit` 對照。

## 測試範圍

**本次發布流程（GitHub Actions，Windows Server 2022）**。任一步失敗都不會建立本 Release：

- 建置與離線核心／平台測試，包括錄音格式轉換的合成資料測試。
- 受控桌面 UI 整合測試（隔離的測試視窗、文字與憑證資料）。
- 安裝程式打包、安裝、升級、啟動冒煙測試與解除安裝（合成資料；不讀取正式金鑰、不錄真實麥克風、不呼叫 Groq）。

測試紀錄見 `validation-evidence.zip`。

**維護者在開發機上的手動驗證**：建置、離線測試、打包與安裝測試。UI 整合測試未在開發機執行，以 CI 結果為準。

## 尚未完成的實機驗收與限制

- 真實 Windows 10／11 電腦上的麥克風錄音、藍牙耳機與麥克風隱私權設定。
- Notepad、Chrome／Edge、Word、LINE、ChatGPT 等實際軟體的貼上與修字學習。
- 中文輸入法組字與選字、多螢幕與高 DPI、螢幕閱讀器。
- 使用真實 Groq 金鑰的辨識品質與延遲。
- ARM64 未驗證；iPhone 接力、跨裝置詞典同步、多人會議與本機 AI 模型不在本版範圍。

詳見 [VALIDATION.md](https://github.com/yuipan0611/whats-up-my-neighbor-ai-input-windows/blob/main/VALIDATION.md)。遇到問題請到 [Issues](https://github.com/yuipan0611/whats-up-my-neighbor-ai-input-windows/issues) 回報，不要附上 API Key 或私人錄音。
