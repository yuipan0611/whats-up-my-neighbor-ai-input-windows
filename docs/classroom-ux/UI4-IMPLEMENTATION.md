# UI-4：核准預覽轉 WPF

2026-11-05，核准稿為本機 `build/classroom-ui4-preview`，包含使用者最新修正：不設草稿／失敗情境分類；老師下方獨立一格老師檔案。

## 影響範圍與護欄

中度風險：調整 ChatWindow 排版、導航與可見訊息狀態；新增唯讀老師附件索引及原訊息定位。收件來源與名單 principal 的對應必須正確，檔案頁不能誤標整個聊天室已讀。

- 保留現有文字／附件協定、SHA-256 校驗、下載／另存流程、草稿版本與清除世代。
- 不改 schema、配對、安全設定、核准貓素材或任何日常 profile。
- 老師檔案只收錄目前老師名單 principal 的 incoming 附件，分頁查詢；不是公開上傳空間或老師權限授予。
- 檔案頁不啟動聊天已讀；切回原訊息才依既有視窗與 viewport 規則標記。
- 原 UI-3、工作樹與候選交付保留。新包獨立 UI-4，未 push／安裝／發布。

## 實作順序

1. 建立 additive 唯讀老師附件索引，用 SQLite 檢查老師／同學、收／送、分頁、清除、名單變更與重啟。
2. 套用名單、標頭與輸入區；把原本待傳／失敗嘗試放到訊息旁，保留原 retry/cancel 規則。
3. 串接老師檔案、既有附件操作與原訊息定位，防止切頁後晚到結果及錯誤已讀。
4. 執行 focused、教室回歸與 win-x64 自含建置，產生獨立交付包；嘗試取得 Windows 原生證據。

## 基準

HEAD `90b9cb6`，修改前 tracked clean（另有既有 reports 與 .impeccable 未追蹤輸出）。安全設定 strict validation 通過；原生／雙機仍為未解確認事項。Focused 基準紀錄在 `build/ui4-native-20261105/baseline-classroom-ux.log`。本機 HTML 原型不代替 Windows 驗收。

## 已完成與驗證

- WPF 精簡名單／搜尋提示／全部未讀、標頭選單、依需展開的程式碼對話框、同學貼圖面板。小視窗仍保留附件工具。
- 老師下方獨立老師檔案格，最多 50 筆一頁；沿用實際下載、另存與進度。原訊息可定位舊頁，檔案頁不標整個對話已讀，晚到結果需通過視圖世代檢查。
- 失敗／待確認傳送嘗試改為對應原文下方的訊息卡，不再顯示頂部嘗試橫幅。重試沿用既有內容／草稿版本規則，沒有假成功。
- Core 完整回歸 6,368 checks 通過；classroom-all 15 套及 UX 26／老師索引 14 檢查通過；附件 69 項含真 TCP／SQLite。
- WPF 建置 0 warnings / 0 errors，win-x64 自含 publish 成功。原始編譯失敗及過時按鈕名稱靜態測試失敗均保存在 build/ui4-native-20261105，沒有覆寫。
- 本機交付以 UI4_LOCAL_PREVIEW 編譯旗標預設進隔離 QA 目錄，直接雙擊 exe 可用。普通建置沒有此旗標，不改一般啟動。隔離模式只在本機造示範資料及 WPF RenderTargetBitmap；不自動傳訊，不碰日常 profile、防火牆。原生圖不等於滑鼠／鍵盤或雙機驗收。

## 新增需求另列（尚未包含）

使用者之後提出同學也可分享與撤下教材，並指定老師電腦保管共用清單／檔案、同學可作備援節點。**目前 UI-4 是老師收件附件索引，不包含班級共用上傳／撤下／分散備援。** 新功能先補預覽與一致性規格，不以 UI-4 的索引或本機附件複本冒充已完成共用功能。

## 第一輪 Windows 證據與修正

透過 Finder「打開檔案的應用程式 → Parallels Desktop」成功啟動 UI-4，無需 prlctl Pro。隔離程序在 Windows 192 DPI（200%）產生老師對話、老師檔案及 520 DIP 窄視窗的實際 WPF client render。原始證據位於 NekoChat-回傳/UI4-native-20261105/20261105-072035-544601；不是 HTML，也不是滑鼠／鍵盤或雙機驗收。

原生圖暴露兩個問題：老師檔案頁同時選中老師列；新增失敗嘗試卡時，捲動可能停在前一則附件。UI-4A 改為獨佔檔案頁選取、穩定保留失敗訊息卡，再捲到最後。QA 另外檢查失敗卡在 viewport 內、檔案頁沒有同時選中聊天列。原生 renderer 直接擷取 client 根視圖，避免非 client 留白。新隔離 profile 按版本分開，測試埠 18771；不改既有版本或防火牆。

## UI-4A 原生結果

`20261105-072946-7cbbff/native-result.json` 回報 WPF rendering captured，192 DPI（200%），兩個老師檔案、`retryCardVisible=true`、`exclusiveFileSelection=true`。對話、檔案與窄視窗 client 原圖已保留；可檢視 build/classroom-ui4-preview/native-ui4a-flow/index.html。原生 renderer 保留透明背景，檢視頁以白色呈現，沒有修改原圖像素。Windows 輸入、IME、真拖曳／剪貼簿、其他 DPI、雙 Windows 仍未執行。

交付：NekoChat-交付/Classroom-UI-4A-WPF-Preview/App/NekoChat.Windows.exe；437 個檔案 SHA-256 核對通過。未安裝、push、發布或部署。
