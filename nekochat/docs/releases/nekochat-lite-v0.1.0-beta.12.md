# NekoChat Lite v0.1.0-beta.12 — 本機診斷與手動匯出

**[下載 Windows 安裝檔（建議）](https://github.com/yuipan0611/whats-up-my-neighbor-ai-input-windows/releases/download/nekochat-lite-v0.1.0-beta.12/NekoChat-Setup-win-x64.exe)** · [免安裝版](https://github.com/yuipan0611/whats-up-my-neighbor-ai-input-windows/releases/download/nekochat-lite-v0.1.0-beta.12/NekoChat-Lite.exe)

## 本版更新

- 新增本機診斷：傳送、接收、儲存與畫面更新遇到問題時，以固定的 NCD 錯誤代碼分類，協助追查；記錄失敗不會把訊息誤報為送達，也不阻塞正常送收。
- 設定中的「匯出診斷記錄」可手動產生 ZIP，由使用者自行選擇是否分享；App 不會自動上傳。
- 診斷不記錄聊天或附件內容、姓名、IP、檔案名稱、本機或來源路徑、token、金鑰及例外訊息正文。只保留固定分類、計數、版本、時間、合法訊息 UUID、已知例外類型與專案方法名稱；匯出不包含聊天資料庫、設定或附件。
- 同時發生的錯誤分開保留，持續中的儲存問題不會因另一個暫時錯誤恢復就消失；沒有 runtime 時手動匯出也不會輪替或刪除原有診斷。
- 記錄有容量上限。磁碟無法寫入時，本次 session 只保留最近 256 筆記憶體事件；修復空間或權限後，重新啟動 App 才恢復持久記錄。

## 使用與升級

從舊版貓咪右鍵選「離開」，再執行本版 Setup；既有更新流程也會在同意下載後核對 SHA-256，再由使用者決定是否執行安裝。本版保留 beta.11 的待送續傳、原 ID 重試、後續草稿與已讀定位規則。收到或匯出的檔案不會因診斷功能而自動上傳。

## 審查與自動化驗證

PR #27 經 **Ben 和 Tom 複審通過**，合併至 main `4159e70`。本次發布來源相對該 commit 只調整 App／Setup 版號及這份說明，沒有新增產品邏輯修改。發布必須使用同一來源通過 CI 的產物，並核對版本、來源與 SHA-256。

自動化涵蓋 Core／SQLite／TCP、診斷隱私與故障隔離、原生 WPF 診斷與匯出、未讀、待送／草稿恢復、封裝版與免安裝版互傳、Setup 安裝／升級及安裝後互傳；CI 使用隔離 profile 與合成資料，不使用日常聊天。

## 尚未完成的現場驗收

**WPF 診斷畫面的實機人工測試、兩台實體 Windows、教室 AP／VLAN 與多人網路、實裝中文輸入法、實體 DPI／多螢幕**仍未完成。原生 CI、loopback 和合成資料通過，不代表這些現場項目已通過。

這是未簽署的 Windows x64 預發布測試版。請從本 Release 下載並核對同版 SHA-256；本次發布不包含替使用者或老師安裝，也不部署任何服務。
