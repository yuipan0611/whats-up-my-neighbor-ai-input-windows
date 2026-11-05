# 聊天訊息生命週期（2026-11-05 使用者確認）

狀態：實作與驗證中，更新 PR #24/#26；未合併、安裝或發布。此決定取代先前「一律手動重送」與「只標記畫面內訊息」的規則。

## 使用者行為與驗收

- 傳送先持久入列，成功交接才釋放輸入框；背景工作不讀寫下一份草稿。文字、程式碼、圖片及檔案皆適用。
- 暫時斷線自動以 1/2/4/8/16/30 秒退避，網路／App 恢復立即檢查；每對話一個進行中工作、全域最多四個，不阻塞其他對話。
- 同一則重試沿用原訊息 ID。不同的「傳送」即使文字相同也有新 ID；成功只留一則訊息，按原始入列時間排序，不殘留失敗卡。
- 附件使用受管理的本機副本；通知送達與檔案校驗完成分開。下載中斷從零重新校驗，不宣稱 byte-range resume。
- 不可恢復錯誤停止自動續送；取消也停止。舊版傳送結果不明時保留人工重試，不冒充可去重。
- 本機暱稱只影響自己的顯示；刪除只影響自己。刪除待送項目同時移除續送擁有權，晚 ACK／重傳不可復活；身分、IP 與其他草稿保留。
- #26 推進到最後實際呈現訊息的閱讀位置，包含之前尚未載入的訊息；背景／隱藏／最小化／另一對話不推進。回條關閉仍清自己的未讀。

## 資料流與風險

本次屬資料庫遷移與背景傳送的高風險修改。Migration V13 新增 ClassroomOutbox，由 EnqueueClassroomMessage / EnqueueAttachment 交易移交草稿及附件參照；ClassroomOutbox 排程器使用既有 ClassroomSender / AttachmentService。Claim / finish / retry / cancel 與本機刪除均經 SQLite 交易及既有墓碑保護。舊的失敗／取消記錄沒有 outbox 資格，升級不會自行發送；明確人工重試才加入。

保留 frozen SchemaV1、classroom wire、附件配額、hash 校驗及核准素材。SchemaGuard 必須新增完整 V13 預期 DDL，而非允許未知表。跨平台 Core/SQLite/TCP 測試不取代 Windows WPF、套件及單一 EXE 測試。

## 驗證命令

使用工作區 dependencies/dotnet-10.0.403/dotnet，TMPDIR 指向 build/tmp：

```
dotnet run --project windows-lite/NekoChat.Tests -c Release -- --outbox
dotnet run --project windows-lite/NekoChat.Tests -c Release -- --classroom-all
dotnet run --project windows-lite/NekoChat.Tests -c Release
dotnet build windows-lite/NekoChat.Windows -c Release -r win-x64 -p:EnableWindowsTargeting=true
```

原生 `--message-recovery-smoke-test` 需操作真正 Send、斷線入列、恢復後自動 TCP 傳送、新草稿保留、無重複卡、時間順序、取消／刪除、原右鍵暱稱及重啟。#26 的 `--unread-smoke-test` 改以閱讀位置驗收。兩支最終整合 head 通過 CI 才能合併，安裝及發布另行授權。

## 與診斷 PR #27 的分工

本分支維護生命週期及資料庫；診斷聊天維護既有 DiagnosticJournal / DiagnosticFault，不建立第二套記錄器。新的背景排程器提供 Changed / Failed 事件，待 #24/#26 API 穩定後，由 #27 接上分類、去識別化記錄與恢復提示。

## 閱讀位置實作

`MarkClassroomReadThrough` 驗證可視 anchor 確實屬於所選對話，使用相同時間／classroom-before-legacy／序號順序，對此前接收訊息做集合更新。即使舊頁未載入仍能更新；對外回條只排入本次新讀的 classroom 訊息。前景／開啟／分頁／世代在交易開始與提交前檢查，離開畫面則本機已讀及回條一起回滾；不新增 schema 或 wire。
