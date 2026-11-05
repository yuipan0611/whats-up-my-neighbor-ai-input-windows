# 統一錯誤分類與本機診斷

使用者授權傳送、接收、儲存、畫面更新的診斷與故障測試。整合基準為 PR #26 `6cc7679`（含草稿保存／Send互斥、同步離開與訊息ID去重）；診斷保持 draft，未安裝或發布。

## 範圍與守門條件

風險中等：新增跨 Core／WPF 的觀察接線。沿用 StoreException、wire reason codes、持久 outbox 及 read-through，不改 schema、wire、身分或核准素材。診斷不能改變交易、ACK、草稿、取消／刪除、世代檢查或傳輸結果；不新增重試排程器。

事件只允許固定分類／階段／結果、合法 UUID、數字、版本與不含路徑的專案方法名稱。排除聊天、附件內容／名稱、IP、暱稱、token、金鑰、SQL、HTTP body／標頭及原始 Exception.Message。

## 接線與失敗隔離

- DiagnosticFault 統一網路、逾時、取消、遠端拒絕、未確認、儲存忙碌／滿載／唯讀、完整性與程式例外；UI 顯示固定 NCD 代碼。原始原因與固定程式位置保留在 journal，未知錯誤不改判成功。
- DiagnosticJournal 背景寫入：4×512 KiB、256 工作／近期事件、單 writer、2秒重複合併及計數。Record 不等待磁碟，記錄失敗降為記憶體。匯出5秒／停止2秒等待上限，最後事件可能尚未落盤。
- Sender 記準備、hello、傳輸、確認與原始例外；接收記提交與提交後通知例外，通知失敗不撤銷已儲存訊息。Storage／Worker 保持原回傳或拋出行為；附件校驗失敗可用 UUID 追查。
- Outbox 記入列、領取、傳送、收尾、取消及恢復。入列直接使用 atomic receipt.MessageId，不另做提交後查詢。Finish／Completed 只代表收尾操作返回，送達仍需真實確認。取消使用既有 CancelOutbox 實際布林結果，不新增 SQL；持久停用後才 CancelIo。
- Runtime 管理 journal。UI finally／傳送邊界恢復 busy；同流程成功清除暫時提示，保留 StartupWarning／OutboxFailure。設定可手動匯出診斷 ZIP，不自動上傳或打包 profile。
- 保留 no-unread guard、前景與世代／COMMIT 守門條件。未讀仍由實際可視最後一則推進，涵蓋先前未載入訊息，後方新訊息保持未讀。

## 故障驗證

本機 diagnostics59、Outbox63、附件70、classroom-all全部15組及完整Core6509通過；WPF win-x64交叉建置0警告0錯誤。檢查數不代表相同數量的獨立故障情境。

Windows head `125811d`：focused診斷28項／未讀24項通過；封裝與portable各自通過診斷28項、未讀24項、啟動、訊息恢復與取消。原生診斷包含真TCP斷線入列、下一草稿保留、同UUID續送、真SQLite寫鎖、worker滿載、錯誤恢復、匯出失敗後重試與隱私。

SQLite contention 保持聊天前景：可視讀、Send flush、autosave可能各耗既有5秒SQL timeout，整體最多35秒；每25ms監測Dispatcher，回應超2秒即失敗。沒有提高產品SQL timeout或隱藏畫面避開爭用。新no-unread guard消除純傳出畫面的無效寫入。

完整CI `42053805683` 在安裝後 archived reaction fixture失敗：SmokeOpenEntryAsync遇到coalesced refresh會先返回，隨即讀舊名單快照。新增有限期等待真實refresh完成再選對象，保留角色／收件人斷言，不跳gate。並同步最新版A失敗／B先送達／A重試排序fixture。最後整合CI仍需核對，先前通過的單項不代替完整綠燈。

兩台Windows、真實教室、IME與實體DPI仍需現場驗收。跨平台建置不作原生證據；WPF RenderTargetBitmap截圖不作實體DPI證據。操作、容量與隱私說明見 `windows-lite/DIAGNOSTICS.md`。

共用recovery等待再修正：eafb6b3 的package18全過，但portable在第二則Count==2過早斷言。現已同步peer先等待接收SQLite原文、再等待自己的正常history row；第一則與人工重試也使用正向正常row證據，保留原Count／ID／無殘留卡斷言。focused診斷workflow額外跑既有18項native recovery並獨立上傳JSON，較早發現這類錯誤；完整套件gate保持不變。

產品UI修正整合：peer full run42058610700發現history可新於inbox摘要，舊_selected.Attempt可能把已送達訊息的待送卡加回。正常classroom列現在保留內部MessageId，SyncAttemptRow只在目前History存在同ID、Outgoing且非Legacy的正常列時移除／拒絕舊卡。不同ID同文字不合併，incoming不壓掉outgoing待送卡；UUID不顯示在UI。保留即時無殘留卡斷言及diagnostic接線，重新驗證focused18與完整封裝gate。

入列前診斷補強：SendAdmission以固定UiNotReady／OperationBusy／DraftNotSaved／StaleView記錄disabled click、傳送中、editor未ready或保存後拒送；StorageWrite也記非exception的Saved=false，不保存原始reason字串。Core新增分類／匯出檢查後為63項。Native診斷新增空輸入disabled Send的真事件斷言（28→30）。

peer gate修正473116a：autosave、Send與exit保存共用async gate，涵蓋真正SQLite返回後的CompleteWrite，保持嚴格epoch／revision CAS。native recovery用isolated completion hook把第一筆真autosave停在SQLite已存、UI未CompleteWrite的位置；再按一次Send，要求兩者仍等待，放行後真peer／正常row／Count2。不是重複點擊或放寬deadline，恢復套件18→19項。

同步exit consumer已對齊：正常關閉await async gate；App.OnExit同步fallback改走worker-only FlushForDispatcherExitAsync，不等待UI CompleteWrite。交錯fixture在gate持有時同步呼叫該fallback，確認不阻塞且DB保存完整文字，恢復套件19→20。保持原嚴格CAS拒絕與例外契約。

負對照腳本只在build私有候選移除Inbox autosave／Send gate；tracked產品及full/package輸出不變。只有候選成功建置、前4項前置斷言通過，且JSON精確在autosave／Send交錯斷言失敗，才算有效紅燈；逾時、編譯錯誤或其他crash不算。之後必須未改原產品的20項通過。PS腳本與候選建置已本機檢視／交叉建置；真正Windows負對照與30+20項正向結果以focused CI為準。
