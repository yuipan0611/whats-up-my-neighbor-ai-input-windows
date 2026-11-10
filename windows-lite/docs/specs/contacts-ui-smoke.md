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
- Hook 在 AttachRuntimeAsync **之前**以明確 runtime/profile 安裝；允許待綁定視窗，但驗證 IsolatedData、Settings／DB 完整路徑。每次使用 hook 再驗證當前 runtime 就是該實例，不提供日常 profile 或錯 runtime 的回退。
- 新增／重複 IP 的原生 Window 表單由 Loaded 掛鉤填入並觸發真正按鈕。確認與已讀提示由每視窗掛鉤記錄原文並自動回答，不彈阻塞 MessageBox；截圖不冒稱是原生 MessageBox 圖像。
- 每一步等待真實視圖／Popup 尺寸，不從未完成的 refresh 猜測畫面。單步 12 秒、整體取消 100 秒、獨立 watchdog 115 秒寫失敗 JSON 並 exit 1；CI 再以 150 秒強制失敗，沒有逾時通過。
- 聯絡人動作掛鉤只觀察真選單啟動的 Task，不代替操作；在原本同一個 12 秒 Wait 內等動作完成與 UI 條件，完成的錯誤會直接傳出。CT-13 封存列以穩定 principal／id 比對，不以其刻意清空的 endpoint 比對；列表 idle 還須等歷史與附件草稿載入完成。
- 訊息 ⋯ 也沿用同一個 12 秒 Wait：重取目前訊息卡／Content 的已載入按鈕，再等該按鈕的 popup 已開啟、有尺寸、且「複製整則原文」與刪除項目都存在。保留 Single 的唯一性檢查；缺項會逾時失敗並列出最近／目前按鈕與選單文字，不用 FirstOrDefault 略過。MD-03 會先觸發真 RefreshActions→Build，覆蓋 Content 重建後的視覺樹時序；舊 message-recovery smoke 也使用同一 helper。
- 移除／封鎖／取代老師各先回答一次 No，對照 UI 完整列／選取、Settings 記憶體／磁碟／SQLite 老師與全部草稿 text/epoch/revision/edit/dirty，再回答 Yes。No 回答有預期 prompt 文字配對、必須恰好消耗三次並寫入 JSON。
- 新增對象、標題 ⋯、取消傳輸在觸發前驗證 IsVisible、實際尺寸及 IsEnabled；新增對象使用既有原生左鍵機制並檢查目標程序，非只 RaiseEvent。
- 右鍵只作用在已確認屬於本程序的視窗位置；最後還原游標。截圖來自 WPF RenderTargetBitmap 96，不是桌面實體 DPI 截圖。

## BDD 驗證與圖像

| 規格 | 真實視窗操作／斷言 | 截圖階段 |
|---|---|---|
| CT-01 | 新合成來源先不存在，loopback 收兩次相同 id，SQLite auto 聯絡人／訊息各一筆，實際列表一筆 | incoming-one-auto-contact |
| CT-02 | 舊 v13 名單外歷史／草稿升級保留；新來訊後歸同一聯絡人，畫面只一條對話 | upgrade-notice-once-and-settings、old-history-merged-one-contact |
| 設定頁 | 實際座標驗證已讀說明直接跟在勾選框下方，附件清理按鈕與附件說明排在之後 | upgrade-notice-once-and-settings |
| CT-14 頭貼 | 真列表 Image 的 pixels 必須符合穩定識別鍵選出的既有花色；不是在線／離線顏色。Core 另跑兩個 child process 驗證固定向量、一致性、多花色分布 | incoming-one-auto-contact |
| CT-10/11 | 真表單輸入 IP＋備註，儲存後選取；先換到別人，再重複新增並點「開啟既有對話」，不得建第二筆 | add-ip-note、added-selected、duplicate-* |
| CT-12 | 一般聯絡人／老師／已封鎖／封存四種狀態，右鍵與標題 ⋯ 分別比對完整固定標籤、順序、分隔線與 disabled 狀態，不只兩選單彼此相等 | contact-*、teacher-*、blocked-*、archive-* |
| 取消路徑 | 取代老師／移除／封鎖先 No，完整 UI／老師投影／草稿快照不變，再 Yes 繼續 | TC-02-teacher-replacement-cancel-unchanged、CT-13-remove-cancel-unchanged、BK-01-block-cancel-unchanged |
| TC-01/02/03 | 從真選單設／取代老師，老師一筆且同學區無副本，舊老師回一般區，Settings 檔與 SQLite 一致 | teacher-single-pinned、teacher-replaced |
| MD-01/03 | 真訊息 ⋯ 與正文右鍵有本機刪除；真 Core sending attempt 的刪除停用且有理由 | message-more-delete、message-body-right-click、sending-delete-disabled |
| MD-05 | 草稿「移除附件」、傳輸卡「取消傳輸」、已存訊息「從這台電腦刪除」分開；真取消操作更新卡片 | draft-remove-attachment、transfer-cancel-distinct |
| CT-13 | 真移除後保留一般篩選、改選第一位／空狀態並顯示封存提示；手動切封存後依穩定 principal 選取（封存列無 endpoint），輸入框只顯示唯讀提示；附件／程式碼／貼圖停用、迴紋針模板變淡，所有重試傳送控制停用、標題／副標明示移除；仍可停止舊待送；SQLite 草稿的文字／epoch／revision、記憶體 buffer、reopen 不變 | removed-stays-general-first-contact、removed-archive-readonly-prompt-draft-retained、removed-last-contact-general-empty、removed-last-contact-explicit-archive |
| BK-01/06/07 | 真封鎖後獨立分區／唯讀提示，附件／程式碼／貼圖停用並顯示停用外觀；解除後原草稿重現；封鎖老師先有警告，老師區清空、加入老師按鈕可見、Settings 投影清空 | blocked-readonly、unblocked-retained-draft-restored、blocked-teacher-add-teacher-visible |
| RR-02 | 真 v13 未設定偏好升級後顯示一次提示並持久化；reopen 前裝好 hook，等 view idle 並連續 500ms 沒有新 prompt，才判定同 profile 不再提示 | upgrade-notice-once-and-settings、reopen-no-second-notice |
| RR-06 | 真合成子視窗取得 foreground，fresh incoming 保持 1 未讀作負控；回到真主視窗，驗證 GetForegroundWindow、IsActive、view idle、未讀0與 badge collapsed；不可直接改 read 或放寬 generation guards | teacher-background-unread-control、teacher-replaced |
| N1 | 移除最後一位後注入一次 UI inbox read 失敗，確認清單真的仍有 stale removed id、但沒有選取／可送；明確刷新後復原正常空清單 | removal-refresh-failure-no-stale-selection、removed-last-contact-general-empty |

JSON 額外記 foregroundVerified、backgroundReadGuardVerified、foregroundObservation、removalRefreshFailureExercised、hooksInstalledBeforeAttach；若平台不能取得真前景會明確失敗並留下觀察值，不把未驗證的前景判為通過。截圖序號因新步驟增加會改變，以名稱識別。

JSON 逐條記 checks、截圖檔名、實際提示文字、合成狀態旗標、GitHub SHA/run id。所有資料皆合成。

## 舊 smoke 相容性

既有 `--message-recovery-smoke-test` 改用目前訊息 ⋯ 的本機刪除入口，以及「編輯本機備註」選單／視窗名稱；保留原本資料、去重、晚到 ACK、重試與暱稱斷言，並加入停用即失敗。未刪除舊斷言來讓 CI 通過。

本機 macOS 只能跑 Core／fixture seed 檢查與 win-x64 交叉建置。原生 WPF、runner JSON／PNG 的實際結果需等 PM push／Draft PR 後查 CI；IME、實體 DPI、雙機與教室驗收仍未執行。

## D-32 課堂反應（RX-01～06）

contacts smoke 最後另建全新 reaction-profile（相同安全路徑檢查、無日常資料），不重用 v13 聯絡人 seed。真 loopback legacy 接收 → 真 store event → 與產品共用 NotifyReactionOrText → 真 PetWindow。圖片設定未預先 opt-in；以合成老師角色先驗證預設貓咪圖、畫面可見／有圖片及不搶焦點，再以舊設定檔 explicit OFF（D-37：設定頁已無可見開關）驗證重讀與純文字提醒；以設定檔切為學生角色並載入設定重跑 Attach，驗證 TeacherOnly 不跳圖也不跳文字、同學貼圖正常。封鎖後同一來源真 TCP 正常 ACK，但無 event、無新增正文、無文字／圖片提醒。四款 TeacherOnly 與五款雙接收路徑的封鎖情境由 --reactions 全覆蓋。

每款先等前張消失，再送出並截取真 PetWindow；五張固定檔名為 reaction-teacher-too-fast.png、reaction-teacher-cant-keep-up.png、reaction-teacher-dont-understand.png、reaction-teacher-say-again.png、reaction-stop-scrolling.png（不加流水號）。逐款以獨立的字面預期釘選 caption 與資產名稱，並將實際 Image.Source 的完整像素和指定內嵌 PNG 解碼結果比對；「跟不上／聽不懂」必須 close-up.png，其餘 raised-paw.png。Caption 必須完全相等，不能混入 ×2。前四款在尚未操作開關的老師 profile 拍攝，最後一款仍在學生角色拍攝。原有 reaction-settings-default-on、reaction-off-text-only、reaction-settings-explicit-off、reaction-blocked-no-notice 保留，全部進既有 contacts JSON 的 checks/captures 和既有 artifact。沿用原 step/global/watchdog deadline，不延長逾時。Mac 僅交叉建置；新原生斷言、截圖、投影／多螢幕實測仍未執行。

D-33 原生啟動負控：另兩份隔離的舊 JSON，注入本機 10.x IP 清單，實際 Attach→空名單 TeacherIp 投影→儲存，分別證明舊 TeacherIp 是本機時保留老師、是遠端時保持學生。主反應 profile 從 ThisDeviceIsTeacher=false／TeacherIp 空開始，注入含固定 DefaultTeacherIp 的清單；明確斷言沒有 CSV teacher-node 捷徑，故必須靠固定 .240 自動提升為老師。安裝 hook 先於 Attach，且只可用精確隔離 runtime。前四張不靠 loopback 角色，loopback 僅用來傳輸。

角色勾選框可見且預設勾選；以實際 Click 明確取消，再重新載入 JSON、建立新 ChatWindow 並 Attach，在同一 .240 本機清單下仍維持學生。真收齊四款並確認每次 NotificationKind=None、沒有圖片／文字／待排文字提醒；開啟真歷史、逐張實現 MessageCard，斷言 11pt 對應標示與原始正文保留，截 reaction-student-ignored.png。無 reaction id 的手打同文字是普通通知、沒有標示。所有新斷言／截圖依舊待 Windows CI，不延長原逾時。

## D-34 預設全班名單（CT-16）

既有 contacts smoke 在 reaction checks 後，以四份全新隔離 profile 跑真正 Attach→角色初始化→Bootstrap→TC-03 投影。受控本機 IP .5：36 位學生＋老師 .240，老師只在 TeachersList 一筆，擷取 default-classroom-roster.png（沿既有數字前綴）；.240：37 位學生、無老師列，但 ThisDeviceIsTeacher=true。不存在的 sidecar 路徑限於該 profile 內，故確實驗證內嵌來源；sidecar case 寫入該處的單列 CSV，實際名單只含該列，不混合預設名單。

舊使用者 case 預先存一筆合成備註，ClassRosterInitialized=false；Attach 後仍只有該筆、備註不動。顯示設定頁，斷言「載入全班名單」可見、有尺寸、可用；實際按鈕先回答 No（完整 roster token 不變），再 Yes。N1 補強 fixture 另有一位先前移除的 .15：確認與取消／完成狀態均顯示跳過1位，No 完整 roster token 不變；Yes 只補35個，排除自己及 .15、保留備註、老師 .240。截取 default-classroom-manual-fill。所有網路迴圈不啟動，只用隔離 DB／合成資料；逾時界線不延長。新原生畫面與斷言待 PM push CI。

B1 小修：reaction-student-ignored 的 11pt 標示卡片將「⋯」移到右側疊層，保留操作入口，取消下方透明按鈕的空白列。原生逐款量測卡片高度不得超過內容高度＋上下 padding（容許1px layout rounding），內容 StackPanel 不得再含按鈕列。新畫面與斷言仍待下一輪 Windows CI。
