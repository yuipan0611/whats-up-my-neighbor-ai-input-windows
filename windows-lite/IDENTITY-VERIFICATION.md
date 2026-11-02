# 本機身分金鑰驗證

`IdentityFileVault` 只由 Windows factory 搭配 `ProtectedData`／`CurrentUser` 建立。私鑰 PKCS8 僅在 RAM 暫存，經 DPAPI 加密後以 create-new、flush、rename 發布在獨立 `identity` 目錄。公鑰與不透明參照進 SQLite，私鑰不進資料庫、備份或診斷。沒有提供使用者匯出、刪除、搜尋其他金鑰或自動修復的操作。明確建立／替換身分以 captured local revision 與生命週期 guard 在交易內核對；失敗可留下加密孤兒檔，保留舊金鑰。

目前這批尚未接到正常 App 啟動流程，也不替使用者決定 P1/P2 或文字上限。身分遺失／不符的 WPF 明確確認流程仍待接線。

Mac 可重跑：

```sh
dotnet run --project windows-lite/NekoChat.Tests -c Release -- --identity-vault
```

此測試把同一份 Windows 檔案實作連結進 net10 測試專案，使用 **AES-GCM fixture protector**，涵蓋實際檔案、P256、SQLite 與生命週期；不驗證 DPAPI、CNG、Windows ACL 或其他帳號／電腦。

Windows 原生操作（本次未執行）：在隔離的測試套件執行，輸出目錄請另建，不使用真實使用者資料目錄。

```powershell
& .\NekoChat.Windows.exe --identity-vault-smoke-test .\identity-smoke-output
Get-Content .\identity-smoke-output\identity-vault-result.json
```

免安裝單檔的名稱可能是 `NekoChat-Lite.exe`，使用相同參數。此指令不載入 `settings.json`、不讀既有身分、不啟動 listener／WPF 主畫面、不改防火牆。只在輸出目錄的隨機 fixture 子目錄建立合成身分；結束後清除 fixture，報告列出 `cleaned`。必須確認 exit 0、`passed: true`、`cleaned: true`；失敗時只記錄例外型別，不輸出私鑰或例外正文。

原生 smoke 驗證同一使用者 DPAPI 建立／再開、實際 Windows P256 簽驗、加密檔竄改／遺失、明確替換及 DB 重開。不同使用者／設定檔／電腦、真正登出重啟、DB 還原造成身分不符，以及 WPF 確認流程仍須另測，不能由同使用者 smoke 推定通過。DPAPI 不是整份使用者設定檔／映像複製的偵測器，不使用 MachineGuid 作為防複製證明。
