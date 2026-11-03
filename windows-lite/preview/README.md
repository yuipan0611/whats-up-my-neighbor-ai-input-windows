# 核對視窗排版預覽

開啟 `pairing-dialog.html`，可切換未開始、等候、核對碼、結果等候、本機完成、到期與資料變更。內容為合成資料，按鈕只改本頁狀態，沒有連線／配對能力。正文取自 WPF 與 Core 的產品文案；HTML 排版、字型和控制項不是 Windows 原生驗收。

正式來源為 `../NekoChat.Windows/PairingWindow.xaml` 與 `.xaml.cs`。一般 App 仍未選定身分方案，不會自動啟用配對或建立金鑰；L 也未選定。Windows 合成原生檢查入口：`NekoChat.Windows.exe --pairing-ui-smoke-test <輸出目錄>`，詳見 `../PAIRING-IMPLEMENTATION.md`。目前未在 Windows 執行。
