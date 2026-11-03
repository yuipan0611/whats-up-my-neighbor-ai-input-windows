# 當次手動傳送取消

TC-05／SDS API-02 要求取消目前的網路操作；它不能撤回對方已存的訊息。完成稽核發現原本 Cancel 在 `_sending` 時反而停用，僅能改已結束嘗試的資料狀態。本次已補實際 runtime 接線，未更改核准版面或通訊格式。

## 來源契約

`ManualSendOperation` 只擁有一個已由人觸發的 `messageId + TryCount` 的取消 token。錯誤 id／try、過期／已結束操作不會取消下一次重送。取消先使該次 I/O token 失效，callback 在背景執行，擁有者等待 callback 及網路工作後才釋放。沒有新增私訊佇列或背景重送。

`ChatRuntime` 註冊真正正在傳送的 operation；`CancelAttemptAsync` 先取消該工作，再於 worker 以同一 try 寫入取消狀態。SQLite 拒絕寫入也不能恢復已取消的 I/O。WPF 既有「取消當次」在目前工作進行中可用；已取消、確認或被取代的嘗試不反覆取消。

**使用者取消和身分／信任失效不同。** Cancel token 只控制 I/O，不能加進保存已驗證 ACK 的 security/lifetime predicate。若完整且驗證正確的 ACK 已先抵達，仍保存送達事實；只有與該次快照相符的草稿才清除，新稿不受影響。介面不保證撤回，也不把已驗證的送達改成失敗。

## Mac 已執行

```sh
dotnet run --project windows-lite/NekoChat.Tests -- --manual-send-cancel
```

34 項：真正 TCP／SQLite 取消、callback 阻塞與 drain、等候 hello、對方已 COMMIT 而 ACK 尚被擋住、取消資料寫入失敗、direct SQL state／歷史／草稿核對、零自動再發、同 id 下一 try 手動重送、拒絕舊取消、已驗證 ACK 最後交易被阻塞時取消，以及較新草稿保留。兩個 negative controls 分別拿掉 try 守衛、把人工取消錯加進 ACK 最後 guard，均在精確行為 assertion 失敗；還原後完整 suite 通過。這不證明原生按鈕、鍵盤或兩實機已驗收。

## Windows 合成入口（未執行）

```powershell
.\NekoChat.Windows.exe --manual-send-cancel-smoke-test C:\NekoChat-QA\manual-cancel-run1
```

這個入口建立全新的隔離 DPAPI／SQLite profile，以測試用配對公鑰和 `L=16384` 啟用 sender；不讀／更改日常設定，不代表使用者已選身分方案或 L，也不作人類配對同意證據。實際 ChatWindow 的 Send／Cancel routed controls 透過 ChatRuntime 發送到一個刻意不回 hello 的 loopback socket；成功才會檢查取消狀態、0筆歷史與原稿保留。輸出 `manual-send-cancel-result.json`、96-DPI WPF RenderTargetBitmap 截圖，並報告 fixture 清理是否成功。它不是 native input injection／物理 DPI／真鍵盤驗收。

另需在 Windows 驗收：按鈕／Tab／Enter／IME、對話切換中的舊 callback、真斷網與 receiver COMMIT 前後、睡眠／身分替換／關閉、存檔失敗、DPI／輔助工具。未執行維持未執行，不填 PASS。
