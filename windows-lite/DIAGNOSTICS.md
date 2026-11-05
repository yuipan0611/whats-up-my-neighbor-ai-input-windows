# 本機診斷與故障處理

設定 →「匯出診斷記錄」可產生 ZIP，由使用者自行決定是否分享。App 不自動上傳診斷。

診斷目錄位於目前 profile 的 `diagnostics/`，與 `data/` 分開。一般執行使用既有 NekoChat profile；隔離 UI 測試使用自己的 profile。匯出只包含固定名稱的診斷 JSONL、最近事件及摘要，不打包資料庫、設定、身分憑證、聊天或附件檔。

## 事件格式與隱私

- 固定的階段、結果、分類、錯誤代碼與嚴重程度。
- Session UUID、事件序號、UTC、App 版本、合法的訊息 UUID、傳送次數、計數與已量測的耗時（未量測為 null）。
- 已知例外類型、專案程式的方法名稱；不含 Exception.Message、InnerException.Message、來源檔路徑、SQL、HTTP body／標頭、IP、姓名、暱稱、附件名稱、聊天、token 或金鑰。
- 遠端回覆的錯誤只映射至固定代碼；不保存任意遠端字串。`RemoteResponse` 表示固定代碼來自對方的回覆，避免誤認為本機儲存錯誤。

錯誤分類使用 `NCD-<Code>`。例如 StorageBusy／QueueFull 可稍後重試；DiskFull／ReadOnly 需先處理空間／權限；Unconfirmed 代表尚未確認送達；IntegrityMismatch 表示校驗或內容衝突；Unexpected 保留程式位置供查 bug。CorruptData／UnsupportedData 表示資料完整性或版本需要檢查，保持既有 fail-closed 規則，不刪除或重建原資料庫。

## 上限與失敗隔離

- 預設 4 份 JSONL，每份不超過 512 KiB；輪替時移除最舊的診斷。另有一個零內容 writer lock。
- 最多 256 筆背景寫入工作、256 筆最近記憶體事件；佇列滿時丟棄診斷並計數，不等待 I/O。
- 相同階段／結果／代碼／訊息／次數／計數／程式位置在 2 秒內重複時合併計數，使用單調時鐘，不受系統時間調整影響。
- 檔案無法建立或寫入時，本次 journal 降為記憶體；`writeFailures`／`droppedEvents`／`suppressedEvents` 在摘要中可見。修復位置後重啟 App 才重新嘗試持久記錄。
- 匯出有 5 秒等待上限，停止時等待 journal 最多 2 秒。失敗、突然斷電或程序被強制終止時，最後的背景事件可能尚未落盤，不能把沒有事件當成沒有錯誤。
- 同一個目錄同時只允許一個 writer；另一個 journal 保留記憶體事件。診斷路徑／固定檔名拒絕符號連結或 reparse point。

## 傳送與接收語意

草稿與傳送嘗試仍由原 SQLite 交易管理。傳輸失敗、取消或確認遺失不會因記錄成功就變成傳送成功。人工重試保留原訊息 ID；不新增自動重送。

接收端交易提交後才回 ACK；畫面通知失敗不撤銷已提交的訊息。這類通知例外另記 ReceiveNotify／NotificationFailed。未讀仍以目前前景可視範圍及世代判斷，記錄不能繞過舊畫面檢查。

UI 操作的 finally／外層傳送邊界恢復 busy 狀態；分類提示與匯出按鈕可繼續操作。一般網路、儲存與介面操作失敗可以隔離；不對作業系統終止、原生程序損壞或記憶體耗盡宣稱能繼續運作。

## 故障回歸

```sh
dotnet run --project windows-lite/NekoChat.Tests -c Release -- --diagnostics
dotnet run --project windows-lite/NekoChat.Tests -c Release -- --attachments
```

Windows 原生：

```powershell
dotnet run --project windows-lite/NekoChat.Windows -c Release -- --diagnostics-smoke-test <隔離輸出目錄>
```

Core 檢查分類、隱私、上限、輪替、writer 競爭、記憶體降級、真 SQLite 回滾與飽和恢復、真 TCP 接收寫入失敗、通知例外，以及 ACK 遺失／損壞、逾時與取消。附件回歸實際破壞 TCP 傳送的 bytes，確認不產生完成事實且留有 UUID 關聯的完整性診斷。

原生 WPF 檢查真 Send 按鈕、故障後下一則、真 SQLite 寫入鎖逾時、滿載 refresh 及恢復、匯出失敗後重試、日誌不可寫仍能傳訊與匯出記憶體事件。只用合成資料與 loopback，不存取日常 profile。教室／雙實機／實體 DPI 另行驗收。
