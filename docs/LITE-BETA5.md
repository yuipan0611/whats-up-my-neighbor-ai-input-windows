# Windows Lite beta.5 影響範圍與驗證

## 實作計畫

1. 保留既有 IP 直連、送達確認、九色動畫、雙擊聊天與氣泡回覆；補上同一 Windows 工作階段的單一執行個體保護，以及接收啟動失敗的持續可見提示。失敗時不能透過按鈕或 Enter 傳送。
2. 保留失敗草稿，傳送中不接受重複 Enter；送出成功僅清除當次送出的草稿。移除 Lite 中已無呼叫端的語音轉錄與憑證存取程式，保留使用者磁碟設定與既存憑證。
3. 沿用 Windows CI，對完整包、免安裝單檔及 Setup 安裝後 App 驗證雙向訊息／氣泡回覆、離線失敗、重複啟動及 TCP 8768 被占用。建置 beta.5 安裝檔，核對來源、版本與 SHA-256。

## 影響與限制

- 主要修改：`windows-lite/NekoChat.Windows/App.xaml.cs`、`ChatWindow.xaml(.cs)`、`NekoChat.Core/DirectLanMessaging.cs`、Lite 專屬測試與包裝版本。
- 直接消費者：Lite WPF App、Lite Installer、Lite 核心測試與 Windows CI。標準版 Neighbor 的錄音／詞庫及 macOS 不在修改範圍。
- 保護：不改設定路徑、安裝路徑與原生動畫；不讀取或刪除既有憑證；不增設背景服務、不修改防火牆、不新增套件。
- 基準：`4d202fd` 的 3874 項核心檢查通過，包括 9 項同機真實 TCP 檢查。
- 風險中等：啟動與傳送狀態會影響所有 Lite 使用者，須驗證三種包裝的實際 Windows 行為。
- 現場限制：同台 Windows 的兩程序測試不代表兩台實體電腦、學校防火牆與 AP／VLAN 已通過。
