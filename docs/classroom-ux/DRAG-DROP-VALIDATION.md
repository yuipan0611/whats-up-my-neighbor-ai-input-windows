# Windows 真實拖曳驗收與修正

基準 878a905，tracked clean；既有 .impeccable/、reports/ 保留。沿用 safely-modify-codebase。

中度風險：WPF routed drag events 決定私人附件草稿或公開課堂分享。只改輸入路由與共用上傳入口，不改附件協定、權限、資料庫、核准素材。

1. Windows 檔案總管 → 目前候選：聊天室及課堂檔案區，先保留失敗／成功原始畫面。測試檔位於獨立 DND-QA-20261105；不對學校 IP 傳送聊天。
2. 修正共用拖曳路由，與選檔共用單檔驗證／上傳；拖曳目標保持明確，資料夾、多檔、超限有具體錯誤，私人草稿不被改送到課堂。
3. focused 回歸、WPF 建置與新本機候選。以實際 Windows Explorer 拖曳重測，核對受管檔案大小及雜湊，測撤下；保留原候選與日常資料。
4. Windows 同機入口證據與 Core TCP 證據分列；未測雙 Windows／教室／其他 DPI 不宣稱通過。

## 修正與原生結果（2026-11-05）

已修正：課堂區沿用聊天室的 `Draft.IsEnabled` 判斷，該頁輸入框停用時導致整個檔案拖放被拒絕。現在依目前畫面路由，聊天室建立私人附件草稿，課堂區與選檔共用分享流程；課堂拖入不會變成私人草稿或檔名文字。拖曳時顯示「放開即分享給全班」，複製效果遵守來源允許的效果；忙碌／已有失敗待重試檔案時不覆蓋。資料夾錯誤改為明確中文，取消失敗檔案後可重新上傳。沒有改協定、資料庫、憑證授權或核准素材。

原生工具 `App.DragDropSmokeTest.cs` 在使用者明確核准後執行。只用全新 C:\ 隔離 profile、合成對象，listener 綁 Loopback，停用課堂背景複寫。Windows 檔案總管提供真正的 OLE FileDrop；工具使用 OS 滑鼠拖動，沒有建立 DragEventArgs、直接呼叫 drop handler 或分享服務來代替拖曳。來源用 UI Automation 選取；滑鼠操作前核對目標 PID，遮擋時取消，不對同學發送聊天、不改防火牆或日常設定。

最終 **R11 / Run-20261105-123816-dfd631：pass，36 項檢查、6 次實際 OLE Drop**。

- 聊天室文字檔與 PNG：檢查受管內容位元組、大小、SHA-256 與來源一致，PNG 產生真正預覽；移除草稿後再換檔。
- 課堂 `.py`：檢查真實內容及 SHA-256，相同位元組亦代表長度一致。已新增自有分享，且沒有產生私人聊天草稿。
- 資料夾、25 MiB + 1 byte、多檔：顯示具體錯誤，分享數保持不變；可以取消再拖其他檔案。
- 實際按 WPF「撤下…」，背景原生滑鼠按 Windows 確認。核對資料庫撤下，以及畫面清單已消失、空清單狀態顯示後才擷取畫面。測試沒有送出私人聊天。

證據是實際輸入後的 WPF RenderTargetBitmap；HTML 僅排列原圖、加箭頭與文字並用白底展示透明區，不修改原圖像素。R11 原始 result.json 包含原生滑鼠座標、Shell FileDrop formats、收到的檔名與逐項斷言。回傳只複製 QA 證據，不複製日常資料庫或私人憑證。

## 自動回歸與保留失敗

`build/dnd-qa-20261105` 保存原始紀錄：

| 檢查 | 結果 | 紀錄 |
| --- | --- | --- |
| 修改前 classroom-files | 53 項通過 | baseline.log |
| 最終 classroom-all | 15 組全部通過；另含 UX26、老師索引14、共用53 | classroom-all-final.log |
| 完整 Core | 6432 項通過 | core-full.log |
| win-x64 自含原生工具及一般功能包 | publish 成功 | publish-native-r11.log、publish-functional-final.log |
| 安全設定 strict／差異空白檢查 | 通過 | manage_config.py validate --strict、git diff --check |

保留的失敗：最初 WPF 缺少 AttachmentException namespace 已修正；原生工具全域 AssemblyName 誤套 Core 參考已改成 Windows 專案專用屬性。早期原生測試因窗口遮擋／快速雙擊打開來源／草稿 UI 尚未更新而停止，沒有把零 Drop 當成功。R6 完成六次拖放但撤下時序失敗；R10 的 35 項已通過，但擷取在畫面更新前，因此 R11 多等 UI 清單空狀態並重測，36 項通過。所有原始失敗與 R10 記錄仍保留，不覆寫成成功。

## 候選 2 與驗收界線

`NekoChat-交付/00-LATEST-Classroom-Files-2/App/NekoChat.Windows.exe`，版本 `0.1.0-beta.9-classroom-files.2.local`。單獨執行檔需留在完整 App 目錄內。

為保留候選 1 的資料與執行狀態，此候選編譯旗標的預設測試 profile 改為 `C:\NekoChatClassroomFilesTest20261105\Files2`、測試埠 18775；所有參與測試的電腦要使用相同候選／埠。正式無旗標啟動的日常預設不改。`QA-DND` 另為會控制滑鼠的隔離工具，非一般手動測試 App。

實際流程：`Evidence/index.html`，旁邊預覽 `/native-dnd-flow/index.html`。舊 helper 包移至本機 `build/dnd-qa-20261105/Retired-VM-Packages` 保留，避免使用者誤開；候選 1 與其他工作樹未移除。

**未執行**：兩台 Windows 之間的聊天附件／課堂備援及撤下同步、其他 DPI、教室 AP/VLAN／多人負載、Mac Finder 跨 Parallels 拖入、這輪選檔與 Ctrl+V 的原生回歸。此次是 Windows 檔案總管 → WPF 真實拖放與本機撤下驗收，並不取代上述驗收。未安裝、未 push、未正式發布或部署。
