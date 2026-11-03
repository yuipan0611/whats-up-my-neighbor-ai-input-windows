# 本機整合候選交付 — 2026-11-03

本批是本機交叉產物，供後續 Windows 驗收；未安裝、簽章驗證、上傳或發布。以同批 `delivery-manifest.json` 的來源 SHA／實際檢查與每檔 SHA-256 為準。`build/final-delivery-20261103/` 中的53b983c是較早快照，缺當次取消修正，且 portable 尚是資料夾包，不代表目前來源。新產物由下方可重做流程產生至全新輸出目錄，不覆寫舊證據。

## 三種產物

| 檔名 | 使用與驗證界線 |
|---|---|
| `NekoChat-Windows-Lite-win-x64.zip` | 完整 self-contained 套件；整包解壓後使用 NekoChat.Windows.exe。 |
| `NekoChat-Lite.exe` | 真正單檔免安裝候選；內含原素材／花色／runtime，使用.NET all-content extraction 模式。靜態與 Mac content-load proof 不代替 Windows WPF／解壓權限驗收。 |
| `NekoChat-Setup-win-x64.exe` | 嵌入同一完整 ZIP 與 SHA-256；離線讀 metadata 核對，未執行安裝／更新／防火牆。 |

另附 `.sha256`、DELIVERY、`delivery-manifest.json`、`portable-bundle.json` 與 logs。真正需要跟 EXE 放一起的 runtime sidecars 不得靠資料夾包掩蓋；portable verifier 必須驗到 all-content flag 及各原始檔 bytes。保留的完整 publish 子目錄只是中間證據。

## 他人可重做

從乾淨、已 commit 的整合 checkout，使用 Python、Git 與 .NET10 SDK：

```sh
python3 windows-lite/tools/build_delivery.py --dotnet /完整路徑/dotnet --output build/delivery-new
```

環境／分項命令見 [工具說明](tools/README.md)。腳本執行實際 Core／動畫 tests，記錄精確來源與 SDK，再建三種包裝及核對。不預填成功數或拿較早 log 當本次執行；來源在過程中改變即失敗。Windows PE App／Setup 不在 Mac 執行。

## 來源範圍與待決

已實作持久對話／草稿／清除、簽章可靠收發與 presence、選擇性已讀與隱私預設、WPF 接線，以及條件式 P1 的選定列同交易綁定／SAS／host lifecycle。當次傳送取消修正見 [SEND-CANCELLATION.md](SEND-CANCELLATION.md)，配對設計與有限證據見 [PAIRING-IMPLEMENTATION.md](PAIRING-IMPLEMENTATION.md)。核准的 themeA／貓／反應保留；先前 SAS source/browser finding resolved，**native disposition仍fix/unexecuted**。

身分模式 P1／P2／P3 與 L 尚未選定，普通 App 不自行建立金鑰或啟用配對／V2 私訊。synthetic fixture 的明確設定不代表產品決策；原 N002P-v2 的退回與有限密碼學論證不改成通過。教材依使用者暫緩、另訂規則。

Windows WPF／字型／焦點／鍵盤／assistive／IME／DPI／多螢幕、DPAPI／CNG／ACL、OS生命週期、安裝／更新／防火牆、真雙機／教室仍未執行。操作單：[PAIRING-VERIFICATION.md](PAIRING-VERIFICATION.md)、[SEND-CANCELLATION.md](SEND-CANCELLATION.md)、[IDENTITY-VERIFICATION.md](IDENTITY-VERIFICATION.md)、[BACKUP-RESTORE.md](BACKUP-RESTORE.md)。完整N002B／N008與產品啟用不由交叉包裝或Mac tests自動通過。
