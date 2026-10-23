# 架構與資料流

對照版本：0.1.0-beta.2。

## 一句話版本

按快捷鍵開始錄音，錄音經 WASAPI 擷取並轉為 16 kHz 單聲道 WAV，送到 Groq 辨識；結果經文字規則處理後，寫入剪貼簿並送出一次 Ctrl + V 貼到目前的輸入框。

## 主要流程

### 1. 快捷鍵與錄音開始

- `HotKeyService` 用 `RegisterHotKey` 註冊主要與第二組快捷鍵。註冊失敗時會回復先前的設定。
- `NeighborApplicationContext` 收到熱鍵後，依狀態呼叫 `DictationCoordinator.ToggleAsync`。
- 沒有金鑰時直接開啟金鑰設定，不開始錄音。

### 2. 擷取與轉換

- `WasapiRecorder` 以麥克風的混音格式（通常是 48 kHz 立體聲浮點）開啟 `WasapiCapture`。
- `CaptureFormatConverter` 把每個封包轉為 16 kHz 單聲道 16 位元 PCM：先平均聲道，再用加窗 sinc 低通濾波與取樣率轉換。只輸出已有足夠輸入的樣本，不補零。
- 寫入 `WaveFileWriter` 的是 16 kHz 單聲道 16 位元格式，並同時計算音量供錄音浮窗顯示。
- 開始錄音後，會等到實際收到音訊封包才回報已就緒，最多等待三秒。

### 3. 雲端辨識

- `GroqTranscriber` 以 `multipart/form-data` 送出 WAV，模型固定為 `whisper-large-v3`，`temperature` 為 0。
- 金鑰只在請求時從 `WindowsCredentialStore` 讀取，以 Bearer 標頭送出。不跟隨重新導向，避免把金鑰送到其他網址。
- 請求逾時 90 秒。錯誤訊息依 HTTP 狀態轉為繁體中文，不回傳伺服器的回應內容。

### 4. 文字處理與輸出

- `TextRules`、`LanguagePolicy` 與 `VocabularyStore` 處理簡繁、標點、中英文空格、數字轉換與提示詞。
- `WindowsTextDelivery` 先把文字放入剪貼簿，再用 UI Automation 確認目前焦點是可編輯的欄位，且不是更高權限的程式或密碼欄位。條件不符時只保留剪貼簿。
- 通過檢查時送出 Ctrl + V。App 不會按 Enter，也不會切換到其他視窗。

### 5. 修字學習（選用）

- 貼入後，`WindowsTextDelivery` 觀察同一欄位的選取範圍。使用者修正一個詞並穩定約 1.1 秒後，`LearningEngine` 產生候選詞。
- 候選詞會以提示顯示兩秒，使用者可取消。未取消且範圍仍可驗證時，才寫入 `VocabularyStore`。
- 學習結果只用於下次辨識的提示詞，不訓練模型。

## 模組

| 位置 | 內容 |
|---|---|
| `Neighbor.Core/DictationCoordinator.cs` | 錄音狀態機：開始、完成、取消、重試、保留錄音與清理 |
| `Neighbor.Core/GroqTranscriber.cs` | Groq 請求、回應解析與錯誤對應 |
| `Neighbor.Core/TextRules.cs`、`LanguagePolicy.cs`、`UnicodeText.cs` | 文字規則、語言策略與 Unicode 處理 |
| `Neighbor.Core/LearningEngine.cs`、`VocabularyStore.cs` | 修字候選與詞典 |
| `Neighbor.Core/SettingsStore.cs`、`Models.cs` | 設定與資料模型，損壞的設定檔會保留而不覆寫 |
| `Neighbor.Windows/NeighborApplicationContext.cs` | 系統匣、選單、快捷鍵事件與各模組的協調 |
| `Neighbor.Windows/Platform/WasapiRecorder.cs`、`CaptureFormatConverter.cs` | 麥克風擷取與格式轉換 |
| `Neighbor.Windows/Platform/WindowsTextDelivery.cs` | 剪貼簿、UI Automation、貼上與修字觀察 |
| `Neighbor.Windows/Platform/HotKeyService.cs` | 全域快捷鍵 |
| `Neighbor.Windows/Platform/WindowsCredentialStore.cs` | Windows 認證管理員中的 API Key |
| `Neighbor.Windows/Platform/LoginStartup.cs` | 目前使用者的登入啟動項目（預設關閉） |
| `Neighbor.Windows/UI/` | 設定、金鑰、詞典、錄音浮窗與學習提示 |

## 修改時的界線

- 能放在 `Neighbor.Core` 的邏輯盡量放在那裡，不依賴 Windows 介面，測試不需要開視窗。
- 新增網路請求、檔案寫入或記錄時，同步更新 [PRIVACY.md](../PRIVACY.md)。
- 改動金鑰位置、設定檔或詞典格式時，要保留既有使用者的資料，並在升級測試中覆蓋。
- 不要讓 App 自動按 Enter，也不要切換焦點到其他視窗。

## 建置產物

```text
Neighbor.Windows/bin/Release/net48/   Neighbor.exe 與相依 DLL
dist/app/                              安裝程式的檔案清單
dist/Neighbor-Setup-<版本>-x64.exe     安裝檔
dist/build-manifest.json               來源 commit、大小與各檔雜湊
```
