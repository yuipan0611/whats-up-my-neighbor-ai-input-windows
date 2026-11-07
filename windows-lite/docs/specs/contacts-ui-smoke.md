# 第 1 批原生 WPF smoke test（D-31）

D-31 授權在 GitHub `windows-2025` runner 自動驗證 UI。本次交付只準備本機 commit；push 與 Draft PR 由 Emily 處理，沒有合併／發布／安裝授權。

```powershell
.\NekoChat.Windows.exe --contacts-ui-smoke-test "C:\isolated-proof\contacts-ui"
# portable: NekoChat-Lite.exe，參數相同
```

輸出：`contacts-ui-result.json`、依序編號的 PNG。CI 對 package、portable 各跑一次，檢查 exit 0、passed、隔離旗標、至少 18 張截圖且檔案皆存在。Artifact：`nekochat-lite-contacts-ui-smoke`，失敗也上傳 JSON／PNG，保留 14 天。

## 隔離、合成資料與失敗界線

- 入口在一般啟動、日常設定載入與單一實例處理之前；先檢查輸出安全路徑，使用真的 `--ui-test-data` 路徑解析，再於 output 下建立唯一 `contacts-profile-*`。
- Settings 路徑與 runtime root 在任何 Save/Open 前必須吻合；不使用日常 profile、開機啟動、外部 relay。掛鉤只能裝在 IsolatedData runtime，且 Settings／DB 必須位於完全相同的合成 profile。
- Shared fixture 反射取得 Core 原有的前 13 個 migration，建立真正的 v13 舊資料，再由正式 Open 升級。反射僅用於建立新的合成資料；不新增 Core friend/API、不複製 frozen SQL，不可覆寫既有 data。`--contacts-ui-seed-check` 在 macOS/Linux/Windows 可驗證相同來源的種子、升級、草稿／歷史、提示、備份及覆寫拒絕。
- TCP 一律 loopback；fixture sink 將觀察到的 loopback 對應到固定合成私人 IP，因產品刻意禁止把 loopback 當聯絡人。正式 endpoint/session validator、存入交易與去重照常執行，不放寬產品 IP 規則。這不是實體 LAN／身分驗證證據。
- 只啟動 loopback 接收器以讓 UI 具備正常操作能力；不啟動背景 outbox、回條或課堂 LAN 同步迴圈。傳送中與附件傳輸中狀態由真 Core API 寫入隔離資料庫，測 UI 綁定；不宣稱完成真實檔案傳輸。
- 新增／重複 IP 的原生 Window 表單由 Loaded 掛鉤填入並觸發真正按鈕。確認與已讀提示由每視窗掛鉤記錄原文並自動回答，不彈阻塞 MessageBox；截圖不冒稱是原生 MessageBox 圖像。
- 每一步等待真實視圖／Popup 尺寸，不從未完成的 refresh 猜測畫面。單步 12 秒、整體取消 100 秒、獨立 watchdog 115 秒寫失敗 JSON 並 exit 1；CI 再以 150 秒強制失敗，沒有逾時通過。
- 右鍵只作用在已確認屬於本程序的視窗位置；最後還原游標。截圖來自 WPF RenderTargetBitmap 96，不是桌面實體 DPI 截圖。

## BDD 驗證與圖像

| 規格 | 真實視窗操作／斷言 | 截圖階段 |
|---|---|---|
| CT-01 | 新合成來源先不存在，loopback 收兩次相同 id，SQLite auto 聯絡人／訊息各一筆，實際列表一筆 | incoming-one-auto-contact |
| CT-02 | 舊 v13 名單外歷史／草稿升級保留；新來訊後歸同一聯絡人，畫面只一條對話 | upgrade-notice-once-and-settings、old-history-merged-one-contact |
| CT-10/11 | 真表單輸入 IP＋備註，儲存後選取；先換到別人，再重複新增並點「開啟既有對話」，不得建第二筆 | add-ip-note、added-selected、duplicate-* |
| CT-12 | 原生滑鼠右鍵與標題 ⋯ 各開真正 ContextMenu，比對項目順序與啟用狀態 | contact-right-click、contact-title-menu |
| TC-01/02/03 | 從真選單設／取代老師，老師一筆且同學區無副本，舊老師回一般區，Settings 檔與 SQLite 一致 | teacher-single-pinned、teacher-replaced |
| MD-01/03 | 真訊息 ⋯ 與正文右鍵有本機刪除；真 Core sending attempt 的刪除停用且有理由 | message-more-delete、message-body-right-click、sending-delete-disabled |
| MD-05 | 草稿「移除附件」、傳輸卡「取消傳輸」、已存訊息「從這台電腦刪除」分開；真取消操作更新卡片 | draft-remove-attachment、transfer-cancel-distinct |
| CT-13 | 真移除確認後封存、Draft 唯讀、Send 停用 | removed-archive-readonly |
| BK-01/06/07 | 真封鎖後獨立分區／唯讀；封鎖老師先有警告，老師區清空、加入老師按鈕可見、Settings 投影清空 | blocked-readonly、blocked-teacher-add-teacher-visible |
| RR-02 | 真 v13 未設定偏好升級後顯示一次提示並持久化，dispose/reopen 同 profile 不再提示 | upgrade-notice-once-and-settings、reopen-no-second-notice |

JSON 逐條記 checks、截圖檔名、實際提示文字、合成狀態旗標、GitHub SHA/run id。所有資料皆合成。

## 舊 smoke 相容性

既有 `--message-recovery-smoke-test` 改用目前訊息 ⋯ 的本機刪除入口，以及「編輯本機備註」選單／視窗名稱；保留原本資料、去重、晚到 ACK、重試與暱稱斷言，並加入停用即失敗。未刪除舊斷言來讓 CI 通過。

本機 macOS 只能跑 Core／fixture seed 檢查與 win-x64 交叉建置。原生 WPF、runner JSON／PNG 的實際結果需等 PM push／Draft PR 後查 CI；IME、實體 DPI、雙機與教室驗收仍未執行。
