# 附件儲存啟動失敗隔離

發布 beta.10 前依使用者的整體停用風險問題，重新檢查 `979dbc8`。已確認 source 路徑：AttachmentVault 建立目錄、檢查路徑或刪除 `.part` 遇到檔案鎖／權限／路徑衝突時，原本例外會穿過 AttachmentService 與 ChatRuntime constructor，StartDataAsync 因而停用所有收發。這是 App 聊天可用性風險，不是作業系統損壞。

修正限制在附件檔案系統錯誤（IOException、UnauthorizedAccessException、unsafe_path）：資料庫正常才建立純文字／回條端點，不宣告附件或課堂檔案能力，顯示修復與重啟指引，停用選檔／課堂上傳／附件清理。既有未完成傳輸標為中斷；附件草稿從仍正常的資料庫讀取，保留名稱與文字，明確移除才可傳文字。草稿尚未讀取完成時禁止提早送出，異步結果須屬於目前 runtime／對話。

不捕捉並忽略 StoreException、資料庫損壞／schema／備份失敗，不改寫凍結文字協定，不放寬來源 IP、儲存 ACK、路徑或分享權限檢查。沒有加入記憶體假聊天或全域略過安全驗證。

Windows `--startup-smoke-test` 新增隔離的真實 Runtime／TCP 檢查：附件路徑被檔案占用、以 FileShare.None 鎖住未完成檔、純文字雙向送達、附件通知不假成功／對方草稿保留、已存附件草稿不誤轉文字、使用者明確移除、修復後重啟服務與文字歷史恢復、損壞資料庫拒絕且原檔不變。只用測試資料夾與 Loopback，不改日常 profile、防火牆或向同學傳送。

本機 WPF cross-build 與 UI source checks 可驗證編譯，Windows 結果以 beta.10 對應 CI 及 `attachment-startup-result.json` 為準。原本主線來源 979dbc8 的全部成功檢查不冒充本次新增程式已驗證。
