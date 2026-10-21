# What's up my neighbor AI Input — Windows

官方網站：[nekonode.cloud](https://nekonode.cloud)。各平台進度、使用教學與開發者文件可在官網查看。

Windows 獨立 repo；macOS 版請見 [Mac repo](https://github.com/yuipan0611/whats-up-my-neighbor-ai-input-macos)。目前仍為預發布測試版，實機待驗收項目見 [VALIDATION.md](VALIDATION.md)，拆分紀錄見 [MIGRATION.md](MIGRATION.md)。

小黑貓語音輸入工具：按快捷鍵說話，再按一次完成，透過 Groq 辨識並把文字貼入目前的輸入框。只貼上，不會自動傳送訊息。

## 下載與開始使用

到本專案 [Releases](https://github.com/yuipan0611/whats-up-my-neighbor-ai-input-windows/releases) 找名稱含 **Windows** 的預發布版本，下載 `Neighbor-Setup-…-x64.exe`。不需要下載原始碼或安裝 Python、Node.js、開發工具。

1. 執行安裝程式，安裝到目前 Windows 使用者的個人資料夾。
2. 從開始功能表開啟 **What's up my neighbor**，右下角系統匣會出現黑貓。
3. 第一次使用時，在 App 的安全輸入欄儲存**自己的** Groq API Key。[建立金鑰](https://console.groq.com/keys)
4. 在想輸入的位置點一下，按 **Ctrl + Alt + Space** 開始錄音；再按一次完成。
5. 黑貓左眼可取消、右眼完成。遇到無法驗證的輸入框，文字仍會留在剪貼簿，請按 **Ctrl + V** 手動貼上。

需要 Windows 11 x64 或 Windows 10 22H2 x64、系統內建 .NET Framework 4.8 或較新 4.x、麥克風與網路。ARM64 未驗證。若學校電腦限制安裝／執行應用程式，需由電腦管理者處理，請勿關閉防護。

這是未購買 Windows 程式碼簽章的測試版，可能遇到 Windows 發行者／信譽提示；發布頁會提供 SHA-256，請確認檔案來源。App 不要求管理員權限，亦不會自行購買 API 額度。

## 桌面功能

- 自訂主／第二快捷鍵、麥克風選擇、非搶焦點浮窗與真實音量顯示。
- 純逐字（預設）、標準、簡潔；繁中＋英文（預設），可選九種主語言及最多兩種混用語言。
- 中文數字轉換預設關閉；靜音停止預設關閉，可選 3／5／10 秒；每段最多五分鐘。
- 詞典搜尋、新增、修改、啟停、釘選、刪除，以及提示詞納入狀態。最多 200 詞，每詞 40 個 Unicode 碼位；完整辨識提示最多 200 UTF-8 bytes，顯示因額度未納入的詞條。
- 支援的輸入框可觀察本次貼入範圍的改字：修正穩定約 1.1 秒後顯示兩秒「不加入」，未取消且仍可驗證時加入詞典。可在詞典管理確認／拒絕／刪除候選。學習是提示詞管理，不是訓練 Whisper。
- 登入啟動由使用者自行在設定中開啟；安裝時不預設常駐登入。

## 隱私與故障處理

API Key 只存目前帳號的 Windows 認證管理員，目標名稱 `WhatsUpMyNeighbor/GroqApiKey`。每次錄音送至 `api.groq.com` 的 transcription endpoint，模型固定為 `whisper-large-v3`；沒有聊天改寫或其他供應商備援。

設定與短詞紀錄位於 `%LOCALAPPDATA%\WhatsUpMyNeighbor`。App 不寫入日常完整逐字稿或金鑰日誌。為辨識與觀察修字所需的文字只短暫留在記憶體；剪貼簿由 Windows 管理，可能受使用者自行開啟的剪貼簿歷程／同步設定影響。

成功複製、取消、捨棄或新錄音取代時刪除錄音。辨識／網路／剪貼簿失敗的錄音暫留兩分鐘，從系統匣選「重新辨識保留錄音」可重試；正常退出與下次啟動也會清理。App 不保證雲端供應商的資料保存或帳務政策。

- 沒有錄音：系統匣開啟「Windows 麥克風隱私設定」，確認允許桌面應用程式使用麥克風，並確認選到正確裝置。
- 無法自動貼上：維持原程式與欄位焦點；對以管理員權限執行的程式、安全欄位、無法識別的輸入框或無法確認的中文組字狀態，只複製文字。
- 沒有修字提示：需要輸入框提供可靠的 UI Automation 文字範圍；開啟「詞典與學習狀態」查看原因，也可手動新增。
- 若把本次貼入的整段文字全部選取後替換，部分輸入框會失去原範圍邊界，此次自動學習會停止，請在詞典中手動加入。
- 語言異常：語言檢查只分辨文字系統，拉丁字母語言彼此無法區分；短句、靜音或同語言幻覺尚無完整防護，請檢查辨識內容。
- 額度或網路問題：App 顯示錯誤，不無限重試、不更換模型、不自動付費。

升級保留設定、詞典與金鑰；解除安裝移除程式、捷徑、登入項目與暫存錄音，預設保留詞典與金鑰。若要完全清除，先在 App 移除金鑰，再自行刪除上述個人資料夾。

## 建置與驗證

Windows 開發環境需 .NET SDK、.NET Framework 4.8 targeting pack（亦可由鎖定的 reference-assemblies 套件提供）及 Inno Setup 6。

執行依賴固定為 NAudio.Wasapi／NAudio.Core 2.2.1 與 Interop.UIAutomationClient 10.19041.0。後者只提供 Windows 原生文字介面的呼叫宣告；不附加另一套桌面執行環境。安裝包使用明確檔案清單，排除測試、除錯符號、SDK、模型與測試音訊。

```powershell
./scripts/build.ps1
./Neighbor.Tests/bin/Release/net48/Neighbor.Tests.exe --interactive
./scripts/package.ps1
./scripts/test-install.ps1
```

整合測試只應在測試用桌面執行：會開啟隔離文字視窗並使用剪貼簿。測試不讀正式金鑰、不錄真實麥克風、不呼叫 Groq。安裝檔必須小於 20,000,000 bytes。

驗證結果與未測範圍見 [VALIDATION.md](VALIDATION.md)。iPhone 接力、跨裝置詞典同步、會議分人與本機 AI 模型不包含在此版。
