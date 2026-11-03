# 配對視窗驗證單

目前 Windows 原生項目一律 **未執行，disposition: fix/unexecuted**。Mac cross-build、真 loopback TCP／SQLite 與 HTML 排版預覽都不能代替以下驗收。身分模式與 L 未決；不要用執行 fixture 代替產品決策。教材仍暫緩。

## 在 Mac 可重跑的檢查

使用工作區固定 .NET 10 SDK、既有 NuGet cache 與工作區內 TMPDIR：

```sh
dotnet run --project windows-lite/NekoChat.Tests -- --pairing-host
dotnet run --project windows-lite/NekoChat.Tests
dotnet build windows-lite/NekoChat.Windows
```

host checks 用合成金鑰、真 loopback TCP 和獨立 SQLite，不作人類比碼或 Windows 證據。

## Windows 合成原生入口（尚未執行）

在目前使用者的互動式 Windows desktop，使用這批來源建出的 App：

```powershell
.\NekoChat.Windows.exe --pairing-ui-smoke-test C:\NekoChat-QA\pairing-ui-run1
```

輸出目錄可自選；每次內部資料夾用隨機新名稱。fixture 不讀日常 Settings/profile，只在輸出目錄下建立兩份合成 DPAPI identity／SQLite；退出時嘗試清理，結果以 `cleaned` 報告。P1 明確啟用而 L 仍 null、CanSend false。兩個真 WPF 視窗經 loopback 換 SAS，由程式以 routed button／checkbox actions 依次確認，再查兩側原名單已綁定。這是合成自動操作，**不是人類完成面對面比較**，也不是 native input injection 證據。

成功才可把 `pairing-ui-result.json` 的 passed 看作該合成 suite 通過。它記錄檢查名稱、失敗 exception type、cleaned 與剩餘項目，沒有私鑰／訊息／SAS transcript 日誌；截圖只含 fixture 資料。會輸出 520 版核對圖和 440×360 本機完成圖；兩者仍須人工檢視。可用 portable EXE 跑同命令驗證打包後來源一致。禁止把未執行或失敗當 PASS。

## 必須另做的 Windows／教室驗收

- 實際兩人面對面比較相同／不同六位碼；對方已確認不代表本機已比較；reject 文案必須保留中止結果，既有 paired 不被取消降級。
- 未勾選不可確認；ticket／對象／視窗輪次更替清除勾選；離開焦點停用確認；Enter 不觸發預設確認；鍵盤 Tab／Space／Escape 與 Narrator 讀名單、狀態、核對碼。
- JhengHei／Consolas 及長姓名／完整 bound peer id：100／125／150／200% DPI、440 窄寬、360 短高、工作區更小與多螢幕，正文可捲動，固定 Close 永遠可操作。
- 確認前後關閉／隱藏 owner、真正睡眠／恢復、身分替換／遺失、列改名／IP／rebind、revoke／reset，舊 UI／callback 不得建立信任；等待中的 store 工作要排空。
- 不對稱完成、斷網／限速／對方 COMMIT 後失去回應，不能顯示雙方完成或自動恢復／重送。120 秒到期明確重試，不自動開新輪。
- Windows P256／DPAPI CurrentUser、跨帳號／機器還原與 ACL，缺／壞金鑰不自動重建。
- 真雙機同網段、防火牆與教室名單／提醒隱私。沒有授權時，不自行安裝或改防火牆。

普通 App 不會因這份檢查單而啟用 P1。完整 N002B 與 Windows N008 仍受未決政策與上述原生驗收限制。
