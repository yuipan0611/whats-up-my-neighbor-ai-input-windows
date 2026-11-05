# 統一錯誤分類與本機診斷

基準：`6ec7241`（PR #26 未讀修正候選）。獨立分支，不合併其他工作分支。使用者已授權傳送／接收／儲存／畫面更新的診斷與故障測試；不發布或安裝。

## 影響與守門條件

風險中等：新增跨 Core／WPF 的觀察接線，但診斷不能影響交易、ACK、草稿、世代檢查或傳輸結果。沿用 StoreException、既有 wire reason codes 與人工原 ID 重試；不新增自動重送，不改 schema／wire／身分／素材。

事件只允許固定分類／階段／結果、已驗證 UUID、次數／耗時與不含路徑／訊息的程式呼叫位置。不存聊天內容、附件內容／名稱、IP、暱稱、token、金鑰、SQL 或原始 Exception.Message。記錄具容量上限、背景寫入、佇列上限與遺失計數；記錄失敗降為記憶體，不能改變產品結果。匯出只含診斷檔與固定摘要，不含資料庫或設定。

## 實作順序

1. Core 固定錯誤分類、非阻塞診斷 journal、受限輪替與匯出；測隱私、容量、佇列／磁碟失敗。
2. 接入 Sender、StoreSink／Endpoint、ChatStore 失敗與 Worker、附件失敗；保持既有協定與 durable outcomes。
3. Runtime 管理 journal 生命週期；WPF 接入 refresh／visible-read／操作例外，提供匯出診斷及明確錯誤代碼。確保 finally 恢復 busy／傳送狀態。
4. 真 TCP／SQLite 故障測試，加 Windows 原生故障操作及後續成功操作；執行既有 classroom／inbox／未讀回歸與封裝 CI。

基準：inbox workspace 25 項、classroom UI policy 133 項通過；safety config strict validation 通過。雙實機／教室／實體 DPI 的既有限制維持。

## 已完成的接線與本機結果

- Core `DiagnosticFault` 固定分類、原始取消／逾時區分、遠端代碼白名單；`DiagnosticJournal` 有界背景佇列、輪替、記憶體降級、同一 profile 單一 writer、重複事件合併及 ZIP 匯出。
- Sender 記準備／hello／傳輸／確認及原始例外；接收記交易結果與提交後通知例外；Storage／Worker 記失敗並照原行為回傳或拋出；附件校驗失敗有訊息 ID 關聯。
- Runtime 管理生命週期。UI refresh／visible-read／操作失敗有診斷，傳送外層隔離與 finally 恢復 busy；設定可手動匯出。移除舊失敗訊息阻擋下一則新訊息的確認視窗，對應本次失敗後可續用的驗收；沒有自動重送。
- 新的 Core 診斷故障套件 142 項檢查通過（包含逐行 JSON 格式檢查，不代表 142 個獨立故障情境）；附件 70 項通過。WPF 交叉建置通過，原生結果待 CI。
- 第一輪 logger ownership 測試抓到同一 process 在 Unix 的檔案分享鎖不足；加入有限 process registry 並保留 OS writer lock 後通過，未刪除斷言。另有一次本機 MSBuild worker 提前退出；以工作區 TMPDIR 與單節點重建成功，非產品測試失敗。

使用方式、容量上限及最後事件可能未落盤的限制見 `windows-lite/DIAGNOSTICS.md`。

Windows 首輪原生 run `42036124701`：測試使用空白草稿，依既有 inbox 規則不會建立可選對話，因此在操作前失敗。已改為有效合成草稿再測；沒有修改產品的空白草稿規則或刪除驗收斷言。Core 的逐行 JSON 驗證改為統一一項 assertion，仍逐行解析，避免容量差異影響檢查總數。另補 I/O 所屬階段分類及匯出失敗事件，診斷版本保留 build commit。

同 head `c2cdf1f` Windows 原生 22 項全部通過（run 42036698113）；截圖另發現恢復後舊 StorageBusy 提示仍留在畫面。新增同流程恢復狀態：成功 refresh／可視已讀或確認送達後清除對應暫時錯誤，保留 runtime startup warning，記 Recovered 事件；原生回歸補兩個清除提示斷言。啟動資料失敗亦使用統一分類提示。

## 背景待送整合（2026-11-05）

依使用者授權與另一個 Codex 協作，整合 PR #26 `30c75bc`（含 #24 `1049168`）。前述人工重試基準結果保留作歷史證據；目前產品使用持久 outbox 自動續送，診斷不新增重試器。atomic QueuedMessageReceipt 直接提供訊息 ID 與新草稿，避免提交後讀失敗造成重複入列。閱讀位置採 read-through API，保留前景、世代及 busy requeue 守門條件。

新增 OutboxEnqueue／Claim／Attempt／Finish／Recovery／Cancel 階段；收尾 Completed 不等於傳送成功。工作 fault 被既有 outbox 觀察後記 root cause，修復與取消沿用產品狀態。暫時 UI 錯誤恢復時保留 OutboxFailure 與 StartupWarning。原生診斷 fixture 改驗斷線入列、下一則草稿保留、同 ID 續送及接收端 SQLite 真實資料，不再把 composer 清空當作已送達。

PR #26 啟動檢查曾在新 enqueue 語意下過早對接收資料做 Single，原生未讀當時通過；由流程負責 Codex 修正為有限等待實際接收 SQLite，保留內容斷言，未跳過 gate。

整合後本機：Core diagnostics 59、Outbox 63 項通過；WPF win-x64 交叉建置 0 警告／0 錯誤。原生診斷與封裝回歸由更新後 #27 CI 執行。

取消關閉交錯整合：跟進 #26 `09fb95a`（含 #24 `5bd81a7`），CancelQueuedAsync 以不隨視窗關閉取消的 worker 工作先持久停用，再呼叫 CancelIo。診斷在同一 worker callback 使用既有 CancelOutbox 布林結果，準確區分 Cancelled／Stale；不新增 SQL 或改回同步 UI 寫入。

整合原生首輪 42052519728 在真 SQLite busy 案例逾時：前 18 個斷線／草稿／續送與 refresh 斷言已過，StorageBusy 已顯示但 Send 尚未 idle。新閱讀位置會與 Send flush／autosave 依序各等待原 5 秒 SQL deadline；fixture 保持前景，將整個排隊流程設 35 秒上限，另每 25ms 驗 Dispatcher 可恢復、單次回應不超過 2 秒，故障時附 Send task／busy 狀態及 metadata recent 事件。未改產品 SQL timeout、未隱藏畫面避開爭用。
