---
name: Neko Chat Lite — Windows Inbox
description: 已核准 themeA 方向的繁中 WPF 原始碼設計記錄；Windows 視覺驗證待完成。
colors:
  primary: "#2859C5"
  primary-soft: "#E9EFFD"
  canvas: "#FFFFFF"
  surface: "#F3F6FA"
  row-hover: "#EDF2F8"
  text: "#1C2C41"
  muted: "#596B80"
  divider: "#DCE3EC"
  warning-text: "#916015"
  warning-surface: "#FFF8E8"
  error-text: "#B43E3E"
typography:
  headline:
    fontFamily: "Microsoft JhengHei UI, Microsoft JhengHei"
    fontSize: "18px"
    fontWeight: 700
  title:
    fontFamily: "Microsoft JhengHei UI, Microsoft JhengHei"
    fontSize: "16px"
    fontWeight: 700
  body:
    fontFamily: "Microsoft JhengHei UI, Microsoft JhengHei"
    fontSize: "13px"
  label:
    fontFamily: "Microsoft JhengHei UI, Microsoft JhengHei"
    fontSize: "12px"
  code:
    fontFamily: "Consolas, Microsoft JhengHei UI"
    fontSize: "13px"
  sas:
    fontFamily: "Consolas"
    fontSize: "32px"
    fontWeight: 700
rounded:
  inset: "4px"
  control: "8px"
  message: "12px"
spacing:
  xs: "4px"
  sm: "8px"
  md: "12px"
  lg: "16px"
components:
  button-primary:
    backgroundColor: "{colors.primary}"
    textColor: "{colors.canvas}"
    padding: "8px 18px"
  button-default:
    backgroundColor: "{colors.canvas}"
    padding: "4px 8px"
  input:
    padding: "4px 6px"
  pairing-button:
    backgroundColor: "{colors.canvas}"
    textColor: "{colors.text}"
    padding: "6px 12px"
  pairing-confirm:
    backgroundColor: "{colors.primary}"
    textColor: "{colors.canvas}"
    padding: "6px 12px"
  inbox-navigation:
    backgroundColor: "{colors.primary-soft}"
    textColor: "{colors.primary}"
    rounded: "{rounded.control}"
    padding: "8px 12px"
  message-incoming:
    backgroundColor: "{colors.surface}"
    rounded: "{rounded.message}"
    padding: "8px 12px"
  message-outgoing:
    backgroundColor: "{colors.primary-soft}"
    rounded: "{rounded.message}"
    padding: "8px 12px"
---

# Design System: Neko Chat Lite — Windows Inbox

## Overview

**Creative North Star: "Operate — themeA 平面淺色"**

Operate：沿用已核准的 themeA 平面淺色 Inbox／對話方向，以清楚的名單、訊息與可操作狀態支援課堂交流。這是現有 WPF 原始碼的視覺系統記錄，並非新的視覺身份提案；既有貓與五項課堂反應資產保留。

記錄範圍為 `NekoChat.Windows/ChatWindow.xaml`、`ChatWindow.Inbox.cs`、`MessageHistoryView.cs`、`RosterImportWindow.cs`，並延伸記錄 `PairingWindow.xaml`／`.xaml.cs`、`ChatWindow.Pairing.cs` 與 Core 的 `PairingDialogPresentation.cs`；配對方向依 `design/pairing-dialog.md`。視覺方向權威為 `/Users/panh.c/Desktop/NekoChat Lite/docs/team/ui/preview/evidence/01-default-1440.jpg` 與 `05-narrow440-conversation-optA.jpg`；它們是瀏覽器 preview，並非 Windows 執行截圖。本文件的 token 來自 WPF 宣告，所有尺寸以 DIP 解讀；YAML 的 `px` 僅為跨工具表示法。

Windows 原生執行截圖尚不存在；Mac cross-build 不構成字型、DPI、IME、控制項主題或視覺還原的證據。原三項來源審查問題已修正，原生驗證 disposition 仍為 `fix`，不得將此文件視為通過視覺驗收。配對的來源／瀏覽器拒絕結果問題已修正並重擷取，但原生 disposition 仍為 `fix/unexecuted`；Windows 截圖、鍵盤、輔助技術、DPI、生命週期與教室雙機證據尚缺。

**Key Characteristics:**
- 白底、淡藍選取與低彩度中性色。
- 窄寬改為名單／對話單頁切換；短高保留 compact composer。
- 主要狀態同時提供文字，不只依賴圓點與顏色。

## Colors

主色只用於傳送、目前導覽、選取與未讀提示；中性色承擔大面積閱讀與分隔。

### Primary

- **清晰藍**（`primary`）：傳送按鈕、Inbox 導覽文字、未讀徽章與名單鍵盤焦點。
- **淡藍**（`primary-soft`）：目前導覽、選中同學、頭像底色與送出訊息。

### Neutral

- **白底**（`canvas`）：視窗、一般按鈕及內嵌程式碼閱讀面。
- **淡灰藍面**（`surface`）：收到的訊息、設定覆層與程式碼標頭。
- **懸停灰藍**（`row-hover`）：名單列滑鼠懸停。
- **墨色**（`text`）：主文、姓名與訊息內容。
- **輔助灰藍**（`muted`）：狀態、學號、時間、操作提示與輸入框邊線。
- **分隔灰藍**（`divider`）：欄、對話標頭、輸入區與程式碼容器的細線。
- **警示文字／警示底**（`warning-text`／`warning-surface`）：舊版未驗證訊息與當次傳送嘗試；草稿提示亦用警示文字色。這些是狀態角色，不是第二品牌主色。
- **錯誤紅**（`error-text`）：既有主視窗儲存錯誤與配對動作錯誤文字，搭配具體說明；不新增錯誤底色或品牌主色。

**The State Has Words Rule.** 主要狀態同時提供文字，不只依賴圓點與顏色。在線圓點使用 `#237353`；警示外框使用 `#916015`，未知狀態另有虛線。

## Typography

**Body Font:** `Microsoft JhengHei UI, Microsoft JhengHei`。**Code Font:** `Consolas, Microsoft JhengHei UI`。這是來源的字族宣告，實際可用字型、fallback 與繁中字形均待 Windows 驗證；沒有獨立 display 字體。

### Hierarchy

- **Headline**：品牌名稱與設定標題，粗體（700）；使用 frontmatter 的 headline。
- **Title**：目前對話姓名，粗體（700）；使用 title。
- **Body**：訊息、輸入與一般控制項；使用 body；姓名列另用 SemiBold。
- **Label**：學號、狀態、時間、未讀徽章與輔助說明；使用 label。
- **Code**：保留換行的程式碼內容；使用 code；不套語法上色。

配對標題沿用 headline（18 DIP Bold），原名單姓名為（16 DIP SemiBold），狀態副標為（16 DIP Bold），正文（13 DIP），IP 提示小字（12 DIP）。六位 SAS 使用 `sas`（Consolas、32 DIP Bold），以三位一組置中顯示，是供逐位核對的資料，不是裝飾字體。

來源未建立統一行高或字距 token。主介面為緊密的工具型文字階層；不得臆造展示字階。CSV 匯入另為原生 Window，未明訂同一字族與字級，不能宣稱全應用已統一。

**The Source Is Not Acceptance Rule.** 原始碼 token 與跨平台編譯只能證明宣告及編譯；Windows 字型、DPI、IME 與視覺還原須另驗。

## Layout

主視窗初始尺寸（1100 × 740 DIP），最小尺寸（440 × 360 DIP）。正常寬度為 Inbox 固定欄（288 DIP）與彈性對話欄。名單搜尋／篩選固定在上方，新增／匯入／設定在底部，中央名單獨立捲動。對話依序為標頭、條件式信任提示、可捲動訊息、輸入區；底部保留可捲動服務／儲存狀態。

Shell 寬度（≤700 DIP）切換為全頁同學名單或全頁對話；對話標頭提供「同學」返回按鈕。這是 WPF 可用 Shell 寬度，不是螢幕像素或瀏覽器 viewport。Shell 高度（<430 DIP）啟用 compact composer：草稿高度由（72 DIP）變（55 DIP），傳送移至右側並跨兩列，程式碼工具列收起。上方工具區高度隨可用空間縮減；compact 或其可用高度（<40 DIP）顯示「功能」選單，提供既有反應及插入程式碼。

間距主要使用 frontmatter 的四級節奏。主要欄邊界留白（16 DIP），訊息最大寬度（520 DIP），送出靠右、收到靠左；文字折行，程式碼不折行且雙向捲動，程式碼閱讀區最高（160 DIP）。CSV 預覽使用資料表而非訊息卡，初始（860 × 640 DIP），最小（440 × 360 DIP）；不推定其窄高可用性已驗證。

配對為由主視窗擁有的原生對話框，名義尺寸（520 × 700 DIP）、最小（440 × 360 DIP）；載入時由 `DisplayScreens.FitChat` 依工作區與 DPI 約束，工作區較小時亦放寬最小值。本文尺寸不代表實機量測通過。內容區可垂直捲動、動作換行；「關閉」保留於固定底列。

## Elevation & Depth

主畫面以白底、淡灰藍、淡藍填色及細分隔線分層，未宣告自訂陰影或漸層。設定為有邊線的內嵌覆層；ContextMenu、原生對話框與一般控制項仍由 Windows 主題繪製，不推定它們沒有系統陰影。

**The Flat Surface Rule.** 主畫面使用填色與分隔線分層，未宣告陰影或漸層。

## Shapes

程式碼容器與當次傳送提示使用較小圓角（`inset`）；Inbox 導覽與輸入容器使用中圓角（`control`）；訊息使用較柔和圓角（`message`）。名單頭像與在線提示為圓形。一般 Button／TextBox 未自訂 ControlTemplate 的圓角，保留原生主題表現，不將其推定為同一圓角族。`preview/pairing-dialog.html` 按鈕的（4px）圓角只是瀏覽器近似，並非 Windows 控制項規格。

## Components

### Buttons

傳送為藍底白字粗體，普通狀態最小高（40 DIP）；compact 為（55 DIP）。一般動作為白底、灰藍邊線，最小高（28 DIP）。共用 Padding 在 frontmatter。Button 的 hover、pressed、disabled 與焦點多由原生 WPF template 決定；目前沒有可宣告的統一自訂狀態動畫。

### Inputs / Fields

一般 TextBox 有灰藍邊線與緊密內距；草稿位於中圓角外框中，內部 TextBox 無邊線。草稿接受換行、折行與垂直捲動。輸入提示為「Enter 傳送 · Shift＋Enter 換行 · 組字時不送出」，實際 IME 行為待 Windows 驗證。

### Navigation

Inbox 導覽為淡藍底、藍字粗體。名單列使用 hover／selected 填色，鍵盤焦點有藍色（2 DIP）外框；列內含姓名、學號、完整文字狀態、草稿提示及未讀徽章。徽章（>99）顯示 `99+`，不以顏色單獨代表未讀。

### Cards / Containers

訊息卡採收到／送出兩種底色與左右對齊，附本地時間及傳送／已讀狀態。內容為可選取唯讀文字。程式碼有語言與唯讀狀態標頭、複製按鈕及保留原文／LF／CRLF 的原生選單；整則訊息另提供複製原文。

### Status and native surfaces

信任與傳送嘗試用條件式警示面，送達未確認時提供手動重送／取消當次。CSV 先顯示新增、更新、略過、歧義等預覽，再套用；它不是已核准的額外視覺方向。名單標籤不代表身分信任，畫面須維持明確區分。

### Pairing dialog

沿用白底、灰藍文字、分隔線與藍色明確確認動作。開啟時固定原名單的姓名、學號、老師標籤（不授權）、既有綁定身分及綁定意圖；已有 IP 唯讀沿用，未有 IP 可輸入本輪位址，不自動寫回名單。發起／等待與六位核對碼分區，按鈕最小高（34 DIP）、內距依 `pairing-button`／`pairing-confirm`；外觀仍由原生 template 決定。

這是 protected-focus 來源實作：初始焦點在「關閉」，沒有預設確認按鈕；面對面核對勾選預設未選，核對票更換或視窗失焦即清除，只有本輪可用、視窗作用中且明確勾選時才啟用確認。關閉、隱藏與 scope／恢復等變更的失效處理是來源行為，尚未通過 Windows 生命週期驗收。

狀態以標題及文字區分尚未開始、交換中、核對碼／倒數、等候結果、到期、名單／身分／信任變更、明確拒絕及結果未確認。拒絕顯示「本輪已中止」，不宣稱撤銷既有信任；完成僅顯示「本機已完成配對與綁定」，要求查看對方畫面，不能宣稱兩台同時提交。到期或失效後不自動恢復。狀態與錯誤區有來源宣告的 polite live setting，輔助技術實效待原生驗證。

`preview/pairing-dialog.html` 僅有合成資料與本機控制模擬，不能傳輸或配對；原 Inbox 兩張核准圖不等於新對話框已核准。普通 App 啟動尚未設定配對、入口停用且不自動建立金鑰；隔離 smoke 為來源準備，不是已執行的 Windows 證據。

P1／L 決策仍未定，不在設計文件補預設值。教材功能暫緩；目前「教材功能保留」為既有占位文案，不代表已實作。桌面一般訊息提醒僅姓名與「新訊息」，不顯示正文；此為隱私文案約束，不是新增通知外觀規範。

## Do's and Don'ts

### Do:
- Do 沿用核准貓／反應資產與繁體中文介面文案。
- Do 使用既有白底、淡藍選取、細分隔線與訊息左右對齊。
- Do 在窄寬保留「同學」返回路徑，在短高保留「功能」選單。
- Do 保留身分未驗證、草稿與送達狀態的文字說明。

### Don't:
- Don’t 把原生預設控制項的未驗證外觀寫成新的品牌規則。
- Don’t 以 Mac cross-build、HTML sidecar 或 preview 截圖宣稱 WPF 還原驗收通過。
- Don’t 新創貓圖、反應圖或動畫風格。
