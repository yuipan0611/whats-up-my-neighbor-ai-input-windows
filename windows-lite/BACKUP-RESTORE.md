# 離線備份還原

適用於本分支的 NekoChat Windows Lite。還原工具只在**全新目錄**準備副本，不替換現有資料，不啟動接收服務，不匯入或建立金鑰。這份操作說明尚未在 Windows 真機執行。

## 準備還原副本

1. 從貓咪右鍵選「離開」，確認 NekoChat 已關閉。工具會取得同一個 Windows 單程序 mutex；App 執行中會回傳失敗，不開啟設定或資料庫。
2. 將要還原的**單一獨立備份檔**放在本機一般目錄。來源應為 `%LOCALAPPDATA%\NekoChat.WindowsLite\data\backup` 內已完成的 `.db`。不要把目前使用中的 `nekochat.db` 或其 WAL 當作備份。保留原件。
3. 在 PowerShell 指定現有父目錄，以及尚不存在的輸出目錄。例如：

```powershell
$app = 'C:\NekoChat\NekoChat.Windows.exe'
$backup = 'C:\NekoRestore\nekochat-roster-20261103T100000Z.db'
$restored = 'C:\NekoRestore\restored-20261103'
$process = Start-Process -FilePath $app -ArgumentList @('--restore-backup', ('"{0}"' -f $backup), ('"{0}"' -f $restored)) -Wait -PassThru
if ($process.ExitCode -ne 0) { throw '還原未完成。保留原備份與既有資料，勿切換目錄。' }
Get-Content (Join-Path $restored 'restore-report.json')
```

成功必須同時有 exit code 0 及目標內的 `restore-report.json`。報告包含來源 SHA-256、原始與還原後 schema 版本、暫停的配對數、是否有本機身分記錄；不含訊息或金鑰。錯誤僅以 stderr 輸出錯誤類別及 exit code 1，不彈出互動確認。輸出目錄若已存在，須另選新名稱。

工具拒絕來源與輸出路徑中的符號連結／junction、UNC 路徑、來源旁任何 `-wal`、`-shm`、`-journal`。它在複製前後核對來源狀態與 SHA-256，驗證完整 schema、`integrity_check` 及 `foreign_key_check`，再將副本轉為 WAL、執行既有 migration 與 interrupted-attempt recovery。最後以同父目錄 rename 發布完整目錄。這是程序終止層級的原子可見性，沒有宣稱斷電或儲存裝置故障下的持久性保證。

## 人工切換與復原

先檢查報告；保持 App 關閉。保留 `%LOCALAPPDATA%\NekoChat.WindowsLite` 的完整離線副本，包含 `data`、`identity` 與 `settings.json`。只移動工具準備完成的整個資料目錄：將現有 `data` 改名保留，再把還原目錄搬到同一位置並命名 `data`。不要把兩個資料區的 `.db`、WAL 或 SHM 混用。切換中途如果沒有完整 `data`，不要啟動 App。

`identity` 與 `settings.json` 保持原位置。資料庫備份不包含 DPAPI 私鑰；只有資料庫不能恢復遺失的金鑰，複製到其他帳號／電腦也不表示可解密。原金鑰若與還原的身分不符，App 應顯示遺失或不符，不能自動重建相同身分。這個分支可從設定的「檢查本機身分…」明確建立新身分，預設取消；詳見 [IDENTITY-VERIFICATION.md](IDENTITY-VERIFICATION.md)。它不恢復原身分，Windows 人工流程尚未執行。

還原可能退回較早的配對／回條狀態，所以副本一律關閉對外已讀回條、丟棄待送已讀回條，並暫停原本已配對的所有 Peer；保留原有已撤銷／未配對狀態、訊息與草稿。尚未完成的手動傳送會成為「未確認」，不會自動重送。必須重新明確核對身分後才能恢復通訊；目前身分方案與產品訊息上限仍待使用者決定，正常啟動的簽章私訊仍受此限制。

重新開啟後先檢查歷史、草稿與身分提示。若不符，關閉 App，將這次還原的 `data` 改名保留，再把先前完整保留的 `data` 放回；不要在 App 開啟時改動檔案。這次工作沒有對日常資料執行任何切換。

## 未完成與殘留目錄

正常失敗或取消會刪除工具自己建立的 staging；不刪來源或任何既有／競爭建立的目錄。程序被強制終止時可能留下 `.nekochat-restore-<隨機值>`，內含部分或已準備的私人資料。這不是成功輸出，不可直接拿來使用。確認沒有工具仍在執行後，由操作者自行檢查並處理；工具不會自動清除其他次執行的目錄。
