# 班級聊天介面修正 · 2026-11-05

**最新本機候選：** 已核准並接上課堂共用上傳、撤下及同學完整接管；見 [課堂檔案驗證與交付](CLASSROOM-SHARING-VALIDATION.md)。下文保留原 UI-3 實作紀錄。

本機候選，未安裝、未 push、未發布。基於附件驗收紀錄 `4264e09`，分支 `codex/lite-classroom-ux-20261105`；原附件 ZIP 與來源分支保留。

## 使用方式

- 左側頂部是獨立老師區，按頭像／名字進入老師聊天室。四款課堂貼圖只出現在老師聊天室，而且只傳給該聊天室的 IP；不再偷偷改送設定裡的老師 IP。
- 同學聊天室顯示「你再給我滑！」。窄視窗功能選單遵循同一收件規則；移除、不可傳送的對象不能傳貼圖。
- 已依使用者確認的預覽套用至原生 WPF；移除側欄「匯入班級名單」按鈕。加入好友改用獨立小視窗，不再開啟整頁設定。
- 加入好友只需輸入內網 IP；初始名稱為 IP。沒有老師時可按「加入老師」，同樣只需 IP。
- 老師及同學名單可按右鍵「編輯綽號…」，Windows 鍵盤內容選單亦可操作。綽號僅存本機，保留 IP、角色、學號及聊天紀錄；名單變動時拒絕過期儲存。
- 移除設定裡重複的老師 IP、目標說明、提醒螢幕、桌面貼圖預覽及已讀設定的可見入口。沿用原先儲存值，不自動開啟桌面圖片顯示。

## 班級專用包

公開程式本身不含真實名單。班級專用包將 `classroom-roster.csv` 放在 exe 同一層：UTF-8，欄名 `student_number,name,ip,role`。同學的 student_number 留空，name 等於 IP；老師 name 為「老師」、role 為 teacher。不轉錄提供圖片中的任何姓名或學號。實際檔案與 ZIP 留在被 Git 忽略的 build/ 中；`.gitignore` 另防止誤提交 sidecar。

第一次啟動：先完整驗證檔案（上限 1 MiB），略過自己本機 IP，再透過既有 SQLite 匯入與備份流程套用空名單。成功後寫入 settings 的 `ClassRosterInitialized`，之後不會因重新啟動而還原刪除項目或覆蓋綽號。匯入後不存在身分配對／授權。

**已有名單的使用者：不自動覆寫，保留目前名單與綽號。** 現在不提供手動匯入按鈕；班級包已附 IP 名單，新聯絡人可按「加入好友」。 自動載入限首次空名單；這避免覆蓋既有綽號。檔案錯誤時不套用部分資料，顯示檢查提示；目前不提供遠端同步或自動更新班級位址。老師標籤只決定介面，不代表權限認證。

## 影響與驗證

中度風險：調整收件對象路由與名單初始化。沿用 roster snapshot token、SQLite 原子匯入及備份，不改資料庫 schema、文字／附件 wire 協定、動畫資產。

- 修改前 Inbox workspace：25 項通過。
- 新增 Classroom UX：26 項通過，涵蓋老師／同學貼圖界線、移除／不可送對象、SQLite 初始化、自己的 IP 排除、錯誤檔案整份拒絕、既有名單保留、過期 snapshot 拒絕與重啟保存。
- 現有 classroom-all 15 套全過（另先跑新的 UX 檢查），附件真 TCP／SQLite 69 項通過。
- 修改後 Inbox workspace 25 項通過。
- win-x64 WPF 交叉建置 0 警告／0 錯誤；self-contained publish 成功。首次使用絕對輸出路徑的 publish 失敗 MSB3094，原 log 保留；改用專案相對 PublishDir 成功。
- HTML 預覽只做設計說明，不是 WPF 驗收。
- 舊的 archived `App.ReactionSmokeTest` 仍包含「老師貼圖忽略目前同學」的歷史情境，不能當作本次 UI 驗收；需以實際老師／同學名單重做原生測試。
- **未執行：本次新 UI 的 Windows 原生操作、右鍵綽號、首次班級包啟動、不同 DPI、兩台 Windows 收件路由。** 前次附件原生證據不代表本次 UI 已驗收。

原始 log、IP-only sidecar 與班級測試 ZIP：`build/classroom-ux-20261105/`。這份本機候選不能直接宣稱正式發布。


## 使用者確認預覽後套用

2026-11-05 使用者確認移除匯入按鈕後的旁邊預覽，授權套用 Windows 版。此輪只調整原生介面及提示，保留班級包初次自動載入、老師置頂、依聊天室傳貼圖和本機綽號。測試候選標示 `0.1.0-beta.9-classroom-ux.2.local`。原生 Windows／DPI／雙機驗收仍未執行，不把先前 HTML 預覽或交叉建置列為原生通過證據。

本輪驗證：修改前 Inbox workspace 25 項、修改後 UX 26 項＋classroom-all 15 套通過（含附件 69 項）；win-x64 self-contained publish 成功。首次 publish 的 MSBuild 子節點異常退出（MSB4166），原 log `approved-publish.log` 保留；以單節點、停用節點重用再執行成功，見 `approved-publish-retry.log`。未改 schema／wire／動畫資產，沒有執行原生 Windows 驗收。


使用者回報 ca9fad3 原生啟動失敗後，已另做 [connectionId 修正](STARTUP-FIX.md)，請測試 UI-3。原啟動失敗未以交叉建置結果掩蓋。
