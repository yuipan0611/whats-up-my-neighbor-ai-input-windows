# NekoChat Windows Lite 本機交叉建置候選交付

本批是本機交叉建置產物，供後續 Windows 驗收使用；未經 Authenticode 簽章、未執行安裝、未上傳且未公開發布。

## 1. 交付產物

交付包含三種執行產物，各自的 SHA-256 雜湊值與完整檔案清單見同目錄的 `.sha256` 與 `delivery-manifest.json`：

| 檔名 | 類型與驗證界線 |
|---|---|
| `NekoChat-Windows-Lite-win-x64.zip` | 完整 self-contained 套件；解壓後直接執行 `NekoChat.Windows.exe`。包含完整 runtime 與素材。 |
| `NekoChat-Lite.exe` | 真正單檔免安裝候選檔；採用 .NET all-content extraction 模式打包，內建完整素材與花色。Mac 上的靜態分析與 probe 不代替 Windows 實機的 WPF 與解壓權限驗收。 |
| `NekoChat-Setup-win-x64.exe` | 簡易安裝候選檔；內嵌同一完整 ZIP 套件並附校驗碼。僅進行離線 PE metadata 與 checksum 核對，未在 Windows 實機執行安裝或系統註冊。 |

隨附文件包含本說明、[README.md](README.md)、[BACKUP-RESTORE.md](BACKUP-RESTORE.md)、[SEND-CANCELLATION.md](SEND-CANCELLATION.md)、[THIRD-PARTY.md](THIRD-PARTY.md)、`tools/README.md`、驗證報告及完整建置日誌。

## 2. 交付範圍與教室模式

### 2.1 已整合功能
- **聊天視窗與資料庫**：持久對話歷史、草稿保存、訊息清除、SQLite attempt 與 ACK 紀錄。
- **UI 與外觀**：核准的 themeA、黑貓圖示素材、貓咪花色切換與 reaction 圖片顯示。
- **手動傳送取消**：TC-05 / SDS API-02 當次傳送取消接線，支援取消進行中的網路 I/O，保護草稿與狀態，詳見 [SEND-CANCELLATION.md](SEND-CANCELLATION.md)。
- **教室模式傳輸**：已整合 CM-01 至 CM-18c 工程預設（ClassroomTransport），支援局域網廣播探索、固定通訊埠傳輸、TCP 訊息收發與送達確認。

### 2.2 裁定 D-04 與 D-15 執行說明
- **D-04（身分與舊版交換撤除）**：依專案決策，Windows Lite 移除非對稱金鑰身分系統、金鑰交換與相關流程，回歸教室模式輕量通訊。相關核心封存程式碼不啟用，一般啟動不會要求或建立金鑰。
- **D-15（移除交付包中的廢棄文件與測試入口）**：
  1. 交付包已移除過去的身分與交換驗證文件，僅保留教室模式、備份還原與取消傳送說明。
  2. 應用程式已移除過時的 UI 與身分保管測試命令列入口，僅保留 `--manual-send-cancel-smoke-test` 作為傳送取消的合成隔離驗證。

## 3. 已知限制與環境說明

1. **安裝器限制（N008）**：
   `NekoChat-Setup-win-x64.exe` 為單純解壓縮與捷徑建立的簡易安裝工具，依規格定義目前不提供解除安裝（Uninstall）功能。此為已知規格範圍，不列為待修缺陷。
2. **VM 測試環境說明**：
   根據 Microsoft 官方生命週期清單，Windows 10 21H2 家用版與專業版已於 2023 年終止支援；測試環境若使用 21H2 VM，僅作為最低相容性 smoke 參考，不代表官方主流支援基準。

## 4. 未在 Windows 實機執行項目

本批產物由 macOS 交叉編譯產生，以下項目尚未在 Windows 實體機或完整環境進行端到端驗收，維持未執行狀態：
- **UI 與輸入**：WPF 原生渲染、高 DPI 多螢幕縮放、中文 IME 輸入法組字、Tab / 焦點導覽與輔助技術（Accessibility）。
- **系統整合**：實際 Windows 檔案系統 ACL、Windows 電源管理（睡眠、喚醒、休眠）對 socket 的影響、實際網路中斷與重連。
- **雙機通訊**：真實兩台 Windows 實機在實體區域網路的教室模式雙向通訊驗收。

## 5. 重做建置步驟

任何人員於具備 Python 3.11+、Git 與 .NET 10 SDK 的乾淨工作樹上均可重現建置產物：

```sh
python3 windows-lite/tools/build_delivery.py --dotnet /完整路徑/dotnet --output build/delivery-new
```

詳細參數與驗證工具請參閱 [tools/README.md](tools/README.md)。
