# What's up my neighbor AI Input — Windows

官方網站：[nekonode.cloud](https://nekonode.cloud)。各平台進度、使用教學與開發者文件可在官網查看。

小黑貓語音輸入工具（Windows 版）：按快捷鍵說話，再按一次完成，錄音送到 Groq 辨識，然後把文字貼到目前的輸入框。**只貼上，不會自動按 Enter 送出訊息。**

目前版本：**0.1.0-beta.2（預發布測試版）**。實機驗收尚未完成，見 [VALIDATION.md](VALIDATION.md)。

本 repo 是 Windows 版的 fork，由 andrewchu0101 維護。原作者為 yuipan0611，授權為 MIT（見 [LICENSE](LICENSE)）。macOS 版在 [Mac repo](https://github.com/yuipan0611/whats-up-my-neighbor-ai-input-macos)。Windows 版與 macOS 版各自開發、各自發布；功能不完全對等，以本文件列出的內容為準。

## 桌面小黑貓（Windows 第一階段）

預設關閉，與 macOS 版第一次使用的狀態一致。開啟後，小黑貓會在螢幕工作區底部散步與休息。右下角系統匣選單的「桌面小黑貓散步」可開關，設定會保存。貓咪視窗不搶焦點；只有貓咪身體接收滑鼠，可按住拖曳，放開後在新高度水平散步，透明區域仍會穿透到後面的視窗；錄音與辨識期間、鎖定、休眠及全螢幕時隱藏，恢復後繼續。Windows 實際桌面行為仍待人工驗收。

素材及腳步參考原作者 macOS 版，保持 X 左眼與勾勾右眼。這是第一階段功能同步，已支援拖曳（放開後只水平散步），尚未包含舔掌、抓起動畫、資料夾停靠與其他 macOS 桌面互動。錄音浮窗的眼睛操作維持原樣。

## 版本與發布流程

- 版本號只在 [VERSION](VERSION) 檔案維護。
- 發布流程見 [docs/UPDATES.md](docs/UPDATES.md)。流程會建置、測試、打包、做安裝測試，全部通過後才建立預發布版本。
- 本版不做程式內自動更新。要升級時，下載新版安裝檔直接執行即可，設定、詞典與 API Key 會保留。
- 版本紀錄見 [CHANGELOG.md](CHANGELOG.md)，發布說明見 [RELEASE_NOTES.md](RELEASE_NOTES.md)。

## 下載與安裝

到 [Releases](https://github.com/yuipan0611/whats-up-my-neighbor-ai-input-windows/releases) 下載最新版本，只需要一個檔案：

```text
Neighbor-Setup-<版本>-x64.exe
```

同一個 Release 另附 `.sha256` 校驗碼與 `build-manifest.json`。不需要下載原始碼，也不需要安裝 Python、Node.js 或其他開發工具。

**系統需求**

- Windows 11 x64，或 Windows 10 22H2 x64。
- 系統內建 .NET Framework 4.8（Windows 10 22H2 與 Windows 11 預設已有；安裝程式會先檢查，不會下載額外的執行環境）。
- 麥克風與網路。
- 不需要系統管理員權限；安裝到目前使用者的個人資料夾。
- ARM64 未驗證。

**安裝步驟**

1. 執行安裝程式。
2. 從開始功能表開啟 **What's up my neighbor**，右下角系統匣會出現黑貓。
3. 第一次使用時，依提示儲存自己的 Groq API Key（見下一節）。

**關於未簽章的提示**

這是未購買程式碼簽章的測試版。Windows 第一次執行時可能顯示「Windows 已保護你的電腦」，請點選 **其他資訊**，再點選 **仍要執行**。請只從本專案的 Releases 下載，並核對 SHA-256。不要為了安裝而關閉系統防護。

PowerShell 核對雜湊：

```powershell
Get-FileHash .\Neighbor-Setup-<版本>-x64.exe -Algorithm SHA256
```

## 建立 Groq API Key

1. 到 [Groq 控制台](https://console.groq.com/keys) 建立自己的 API Key。
2. 在系統匣的黑貓上按滑鼠右鍵，選擇 **設定／更換 Groq 金鑰…**。
3. 把 Key 貼入安全輸入欄並儲存。

App 不會替你購買 API 額度；額度或費用依你的 Groq 帳號方案計算。金鑰只存在目前 Windows 帳號的認證管理員中，目標名稱為 `WhatsUpMyNeighbor/GroqApiKey`。

## 使用方式

1. 點一下想輸入文字的欄位。
2. 按 **Ctrl + Alt + Space** 開始錄音，再按一次完成。
3. 浮窗中的小黑貓左眼可取消，右眼可完成；辨識失敗時錄音會保留兩分鐘，右眼可重試，也可從系統匣選擇 **重新辨識保留錄音**。
4. 文字會貼到目前的輸入框。若無法確認輸入框，文字仍會留在剪貼簿，請按 **Ctrl + V** 手動貼上。

## 功能

- 快捷鍵：預設 Ctrl + Alt + Space，可在設定中改為主要與第二組快捷鍵。
- 麥克風：可在設定中選擇輸入裝置，錄音浮窗顯示真實音量。
- 輸出模式：純逐字（預設）、標準、簡潔。
- 語言：預設繁體中文加英文，可選九種主要語言，並可設定最多兩種混用語言。
- 數字：中文數字轉換預設關閉。
- 靜音停止：預設關閉，可選 3、5 或 10 秒。每段錄音最多五分鐘。
- 詞典：最多 200 個詞，每個詞最多 40 個 Unicode 字元。可搜尋、新增、修改、啟用／停用、釘選與刪除；超出提示字數上限的詞條會顯示為未納入。
- 修字學習：在支援的輸入框中，貼入後修正同一欄位裡的一個詞，修正穩定後會顯示提示，可選擇加入詞典或不加入。不支援的應用程式可手動新增詞條。學習是提示詞管理，**不是重新訓練語音模型**。
- 登入時啟動：預設關閉，需要在設定中自行開啟。

## 隱私

- 每次錄音都會上傳到 `api.groq.com`，使用模型 `whisper-large-v3`。沒有聊天改寫，也沒有其他供應商的備援。
- API Key 只存在 Windows 認證管理員中，不寫入設定檔或記錄檔。
- 設定與詞典儲存在 `%LOCALAPPDATA%\WhatsUpMyNeighbor`。
- App 不寫入日常完整逐字稿，也不記錄金鑰。辨識與修字觀察所需的文字只短暫保留在記憶體中。
- 成功複製、取消、捨棄或新錄音取代時，錄音檔會刪除。
- 剪貼簿由 Windows 管理，可能受你自己開啟的剪貼簿歷程或同步設定影響。
- 詳見 [PRIVACY.md](PRIVACY.md)。

## 故障排除

- **沒有錄音**：在系統匣選擇 **Windows 麥克風隱私設定**，確認允許桌面應用程式使用麥克風，並確認選到正確的裝置。
- **無法自動貼上**：對以系統管理員權限執行的程式、密碼欄位、無法識別的輸入框，或中文組字狀態不明時，App 只會複製文字。請按 Ctrl + V 手動貼上。
- **沒有修字提示**：需要輸入框提供可靠的 UI Automation 文字範圍。開啟 **詞典與學習狀態** 可以看到原因，也可以手動新增。
- **辨識結果不正確**：短句、靜音或語言判斷錯誤時，App 目前沒有完整防護。請核對辨識結果。
- **額度或網路錯誤**：App 會顯示錯誤，不會無限重試、不會更換模型，也不會自動付費。

## 解除安裝

到 Windows **設定 → 應用程式**，移除 What's up my neighbor。設定、詞典與 API Key 預設保留；要完整清除時，先在 App 中移除金鑰，再刪除 `%LOCALAPPDATA%\WhatsUpMyNeighbor`。

## 從原始碼建置

見 [docs/DEVELOPMENT.md](docs/DEVELOPMENT.md)。測試方式與人工驗收清單見 [docs/TESTING.md](docs/TESTING.md)。

## 文件

| 文件 | 內容 |
|---|---|
| [CHANGELOG.md](CHANGELOG.md) | 版本紀錄 |
| [RELEASE_NOTES.md](RELEASE_NOTES.md) | 發布說明範本 |
| [docs/DEVELOPMENT.md](docs/DEVELOPMENT.md) | 開發環境與本機建置 |
| [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) | 架構與資料流 |
| [docs/TESTING.md](docs/TESTING.md) | 測試方式、覆蓋範圍與人工驗收清單 |
| [docs/UPDATES.md](docs/UPDATES.md) | 發布、校驗與升級流程 |
| [docs/WEBSITE.md](docs/WEBSITE.md) | 官網交付資料 |
| [VALIDATION.md](VALIDATION.md) | 已驗證與待實機驗收的範圍 |
| [MIGRATION.md](MIGRATION.md) | 從原專案拆分為獨立 repo 的紀錄 |
| [PRIVACY.md](PRIVACY.md)、[SECURITY.md](SECURITY.md) | 隱私與安全性 |
| [CONTRIBUTING.md](CONTRIBUTING.md) | 回報測試結果與參與開發 |

## 不在本版範圍

iPhone 接力、跨裝置詞典同步、多人會議分人、本機 AI 模型，以及 macOS 版的其他功能。
