# 本機整合交付 — 2026-11-03

本批為 Mac 產生的 Windows x64 交叉產物，供 Windows 後續驗收。來源 SHA、每個檔案的大小與 SHA-256 以同批 `delivery-manifest.json` 為準；包裝輸出位於整合 worktree 的 `build/final-delivery-20261103/`。這不是安裝、簽章驗證、發布或 Windows 驗收通過。

## 三種包裝

| 檔名 | 用途與使用方式 |
|---|---|
| `NekoChat-Windows-Lite-win-x64.zip` | 完整 self-contained 資料夾，整包解壓後使用 `NekoChat.Windows.exe`。 |
| `NekoChat-Lite-portable-win-x64.zip` | 免安裝資料夾包，使用 `NekoChat-Lite.exe`；保留所有 Assets、Coats 與附檔，不只複製 EXE。 |
| `NekoChat-Setup-win-x64.exe` | 含同一完整 ZIP 及內嵌 SHA-256 的 Setup；未在 Windows 執行，不應把離線完整性核對視為安裝／防火牆驗收。 |

同批外附 `.sha256`、`delivery-manifest.json`、`offline-verification.json`。Mac 只核對 ZIP CRC／完整檔案 hash、原素材／花色、PE x64 apphost、Setup 內嵌 payload/checksum 與 bundle 中的實際 managed assembly。離線工具只讀 PE metadata，不載入／執行 Windows assembly。

## 已完成的來源與本機證據

- 持久化身分／名單／分對話歷史／草稿／清除、簽章與可靠收發、presence、選擇性已讀回條與隱私預設，及其 WPF 接線。
- 條件式 P1 的候選／commit-reveal／雙方 one-use SAS consent／同埠 TCP／交叉發起／真 kill-restart／MITM 反例、選定列同交易綁定、host／設定／視窗生命週期守衛。詳 [PAIRING-IMPLEMENTATION.md](PAIRING-IMPLEMENTATION.md)。
- Core 6,600 項、動畫 4,361 項及 WPF cross-build 0 warning／0 error。原始 log、反例控制與 hash 留於 worktree 的 `build/wire-evidence/`，不把它們稱作 Windows 實機證據。
- 獨立 UI 審查的拒絕結果文案問題已修正；合成 HTML 預覽問題已解決，**native disposition 仍為 fix/unexecuted**。預覽沒有連線／配對能力，沒有被當成人類比碼證據。

## 仍未啟用／未驗收

身分方案 P1／P2／P3 與訊息上限 L 尚未由使用者選定。一般 App 不自動建立身分或啟用配對／V2 私訊；本批不代答產品決策。合成 fixture 只在隔離資料下明確啟用 P1，配對 UI fixture 仍不選 L。原 N002P-v2 的退回與有限密碼學論證保留，不能改稱原提案通過。教材依使用者指示暫緩、另訂規則。

Windows WPF 實際字型／焦點／鍵盤／輔助工具、IME、DPI／多螢幕、DPAPI／CNG／ACL、OS 睡眠／重啟、安裝／更新／防火牆、雙實機／教室皆未執行。操作單：[PAIRING-VERIFICATION.md](PAIRING-VERIFICATION.md)、[IDENTITY-VERIFICATION.md](IDENTITY-VERIFICATION.md)、[BACKUP-RESTORE.md](BACKUP-RESTORE.md)。

這份交付完成目前可在 Mac 執行的工程與包裝工作；完整 N002B／N008 與產品可用驗收仍受未決政策及 Windows 證據限制。沒有 push、PR、合併 main、安裝或發布。
