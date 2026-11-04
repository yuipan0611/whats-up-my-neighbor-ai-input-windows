# connectionId 啟動錯誤修正 · 2026-11-05

使用者在 Windows 啟動 `ca9fad3` 候選時，回報「設定 connectionId 時擲回例外狀況」，介面未顯示。不是已通過原生驗收的版本。

原 XAML 把有 Click 處理器的 MenuItem 放在 ListBoxItem Style 的 ContextMenu Setter 裡。實際產生的 ChatWindow.g.cs 第 1 個 IComponentConnector 分支，將 target 轉為 Grid 再接 MenuItem.ClickEvent；這是錯誤的編譯接線。修改後移除 Style 中的 Click，改由兩個名單的 ContextMenuOpening 建立選單並接 Click，依實際開啟選單的列取綽號目標，保留虛擬化與鍵盤來源處理。新的產生碼第一個分支回到 Shell Grid，沒有該錯誤的選單事件轉型。

- 自含 win-x64 交叉 publish 成功。
- Classroom UX 26 項通過。
- 原產生碼與 publish/focused log：`build/classroom-ux-startup-fix-20261105/`。
- 共用交付位置：`\\Mac\NekoChat-交付\00-LATEST-Classroom-UI-3\App\NekoChat.Windows.exe`；版本 `0.1.0-beta.9-classroom-ux.3.local`，435 個檔案逐一比對 SHA-256。
- 未安裝／正式發布／修改防火牆。此修正本身仍需原生啟動與右鍵驗收；交叉建置不視為原生通過。

`ca9fad3` 的原 ZIP 與交付檔仍留作失敗證據，測試請改用 UI-3。
