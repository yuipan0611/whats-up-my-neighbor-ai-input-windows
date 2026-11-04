# Parallels 原生驗收：部分完成

2026-11-05 透過 Computer Use 操作使用者既有 Windows 11 VM。測試來源為 `cbc540bf3d2b71d7966dc7299894815f682e57af`，ZIP SHA-256 `3facedff57444a1849cf6b1ce9ba73a528e828275b8f1b032dbd9d0fe70bea47`。解壓至 `C:\NekoChatAttachmentQA20261105`，使用獨立 Profile 與 TCP 18768；未覆蓋日常 App，未安裝或發布。

控制中心只有一台 Windows VM。本次另一端是 Mac 上的實際 Core/TCP/SQLite 測試端，因此以下傳輸結果是 **Windows WPF ↔ Mac Core**，不是兩台 Windows 驗收。

## 原生操作結果

| 項目 | 實際觀察 | 判定 |
|---|---|---|
| 迴紋針選 PNG | 原生檔案選擇器建立 photo.png 草稿，縮圖、名稱、3.39 KB、移除及純附件傳送啟用 | 通過草稿操作；此圖未送出 |
| Windows 截圖 Ctrl+V | Snipping Tool 全螢幕截圖貼入 WPF，產生 PNG；再次貼圖明確拒絕第二個附件 | 通過；其中一份已真實傳至 Mac |
| Python 選檔加文字 | sample.py 43 bytes 完成傳至 Mac；一般文字 Ctrl+A/C/V 保留 caption | 通過；收件 caption 保留 CRLF 與四個空格縮排 |
| ZIP 選檔 | sample.zip 158 bytes 待傳草稿 | 通過建立草稿；尚未送出 |
| 名單小圖自動收件 | QA-cat.png 80,131 bytes 自動接收，沒有接受彈窗 | 通過本次跨系統案例 |
| 6 MiB 手動下載 | QA-large.bin 先顯示等待下載，點下載才接收，Mac 收到完成確認 | 通過本次跨系統案例 |
| 原生另存 | 已收到的小圖與大檔另存至 Evidence，沒有自動開啟 | 操作通過；獨立 SHA 核對待回傳證據 |
| 一般與窄視窗 | 250% 下寬版、約 1075 physical px 窄版、約 985 physical px 高短窗，附件／移除／傳送可操作，短窗改用功能選單 | 部分通過；短窗長狀態文字疑似裁切待重現 |
| DPI 100/150/200% | 系統為自訂 250%，標準選單鎖住；改變需登出 | 未執行，未變更原縮放或登出 |
| 拖曳 | Computer Use 的 drag 沒有形成來賓檔案拖放，之後一般來賓輸入間歇失效 | 未完成，不能判為產品通過或失敗 |
| 重開恢復 | 關閉前 ZIP 草稿已建立；重開命令因輸入工具漏字遭拒，未成功重新啟動 | 未完成，不算恢復測試通過 |

## 跨系統實際檔案

Mac 已重新計算其本機檔案 SHA-256，四筆資料庫紀錄均為 complete。Windows 的送出截圖卡片也顯示「附件已送達」。原始結果與不含下載憑證的紀錄在 `build/native-vm-20261105/mac-peer-observed.json`。

| 方向／檔案 | Bytes | SHA-256 |
|---|---:|---|
| Mac → Windows QA-cat.png | 80131 | 680bd1e85ff5a20311bb8248235fb976ad1d06c72ef7410f7a40726492970a89 |
| Mac → Windows QA-large.bin | 6291456 | 3d193d032558f325b4d1242f223050a5520f96e7366ed190e4fe9874011b97bf |
| Windows → Mac 截圖-20261105-005737.png | 472058 | eeef158bd23729e01393ac4150cda1398995d5335788eda7f2963be7e91a6e2c |
| Windows → Mac sample.py | 43 | ac3d6869faff9898e2041acc861d065e10ccc6ee9de58cf50a0d593c35c2b8bc |

Python caption 的實際 SQLite 字串為 `Python attachment caption\r\n    indent preserved`。這裡顯示的是逸出表示，資料中是實際換行。

## 暫時網路權限與收尾

初次 Windows 防火牆提示選取消。使用者另外明確同意後，才新增 `NekoChat-QA-cbc540b-20261105`：限定本次 EXE、local 10.211.55.4、remote 10.211.55.2、TCP local port 18768。僅暫停同一 EXE 的既有 enabled inbound block 規則並保存名稱，沒有停用任何防火牆 profile，沒有更改網路種類或共享目錄範圍。

Mac 測試端已停止。後續 Computer Use 來賓輸入再次失效，由使用者執行 FIREWALL-RESTORE.cmd；原生畫面已確認 `QA allow rule removed; pre-existing candidate block rules re-enabled.` 與 transcript 結束。**還原的原生成功訊息已確認；回傳 JSON 前後比對與另存雜湊仍待匯出**。檢查程式 `build/native-vm-20261105/verify_export.py` 將逐檔核對回傳 manifest、另存 bytes、Windows Get-FileHash，以及既有防火牆規則還原前後狀態。

## 尚未執行

兩台 Windows、教室網路、DPI 100/150/200%、拖曳、右鍵／Shift+Insert 貼圖、JPG/PDF、微軟注音、Windows 上的完整門檻邊界／名單外／取消／中斷／額滿／惡意內容／清除中傳輸／舊版相容均未完成。先前 Core 測試不能替代這些原生驗收。完整操作單仍見 WINDOWS-ACCEPTANCE.md。
