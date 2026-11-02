# 本機身分金鑰驗證

`IdentityFileVault` 只由 Windows factory 搭配 `ProtectedData`／`CurrentUser` 建立。私鑰 PKCS8 僅在 RAM 暫存，經 DPAPI 加密後以 create-new、flush、rename 發布在獨立 `identity` 目錄。公鑰與不透明參照進 SQLite，私鑰不進資料庫、備份或診斷。沒有提供使用者匯出、刪除、搜尋其他金鑰或自動修復的操作。設定中的「檢查本機身分…」提供遺失／不符時的明確新身分復原，不會回復舊身分。明確建立／替換身分以 captured local revision 與生命週期 guard 在交易內核對；失敗可留下加密孤兒檔，保留舊金鑰。

正常 App 啟動已連接檢查。設定中的「檢查本機身分…」會在背景檢查；只有既有身分的金鑰遺失或不符，才顯示原身分前綴與原生確認視窗（預設「否」）。首次未設定身分與健康身分不會產生復原確認，不替使用者決定 P1/P2 或文字上限。

明確按「是」後，App 先停止舊接收／收發服務、等待既有傳送並存妥所有草稿，再以一次性確認與 captured local revision 在同一 SQLite 交易替換身分、暫停原 paired peers、關閉對外已讀回條、丟棄待送回條及 supersede 舊嘗試。原訊息／草稿、已撤銷／未配對狀態、舊加密金鑰檔與設定保留。存稿失敗不進入替換；取消、舊確認、身分 ABA 或原金鑰已放回，都拒絕沿用舊確認。舊 runtime 關閉後重開新狀態；普通私訊仍須等產品身分政策與訊息上限定案。

Core 共 33 項實際 encrypted-file／signature／SQLite 檢查，包含二 handle 同時建立候選金鑰只提交一個，以及兩個在 storage transaction COMMIT 前後被強制終止的子程序。後者只驗證資料交易原子性，沒有聲稱驗過跨程序 DPAPI 或人類確認。Windows WPF 對話框、存稿失敗／退出交錯、原生 DPAPI 與 OS lifecycle 全部未執行。

Mac 可重跑：

```sh
dotnet run --project windows-lite/NekoChat.Tests -c Release -- --identity-vault
```

另外可用 `--identity-recovery` 跑復原檢查。這兩組測試把同一份 Windows 檔案實作連結進 net10 測試專案，使用 **AES-GCM fixture protector**，涵蓋實際檔案、P256、SQLite 與生命週期；不驗證 DPAPI、CNG、Windows ACL 或其他帳號／電腦。

Windows 原生操作（本次未執行）：在隔離的測試套件執行，輸出目錄請另建，不使用真實使用者資料目錄。

```powershell
& .\NekoChat.Windows.exe --identity-vault-smoke-test .\identity-smoke-output
Get-Content .\identity-smoke-output\identity-vault-result.json
```

免安裝單檔的名稱可能是 `NekoChat-Lite.exe`，使用相同參數。此指令不載入 `settings.json`、不讀既有身分、不啟動 listener／WPF 主畫面、不改防火牆。只在輸出目錄的隨機 fixture 子目錄建立合成身分；結束後清除 fixture，報告列出 `cleaned`。必須確認 exit 0、`passed: true`、`cleaned: true`；失敗時只記錄例外型別，不輸出私鑰或例外正文。

原生 smoke 驗證同一使用者 DPAPI 建立／再開、實際 Windows P256 簽驗、加密檔竄改／遺失、明確替換及 DB 重開。不同使用者／設定檔／電腦、真正登出重啟、DB 還原造成身分不符，以及 WPF 確認流程仍須另測，不能由同使用者 smoke 推定通過。DPAPI 不是整份使用者設定檔／映像複製的偵測器，不使用 MachineGuid 作為防複製證明。


Windows 人工流程（未執行）：使用拋棄式帳號及隔離測試資料，在 App 關閉時保留／移走對應測試加密檔，開啟後從設定按「檢查本機身分…」。先按「否」確認沒有變更；再檢查、確認後核對新身分與暫停配對、歷史與草稿保留、回條關閉及原金鑰檔保留。另測確認前放回原檔、確認中結束／休眠、存稿寫入被拒與復原後重啟。勿移動或竄改日常金鑰檔。自動 `--identity-vault-smoke-test` 已加入 DPAPI recovery flow 的取消／明確確認與 security reset，仍不代表人工 WPF 確認已驗收。
