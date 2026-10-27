# NekoChat Windows Lite 0.1.0-beta.1（教室試用版）

[下載 Windows 安裝檔](https://github.com/yuipan0611/whats-up-my-neighbor-ai-input-windows/releases/download/nekochat-lite-v0.1.0-beta.1/NekoChat-Setup-win-x64.exe) · [Release 版本頁](https://github.com/yuipan0611/whats-up-my-neighbor-ai-input-windows/releases/tag/nekochat-lite-v0.1.0-beta.1)

## 安裝

Windows 10/11 x64：直接下載並雙擊 `NekoChat-Setup-win-x64.exe`，按「安裝並啟動」，再按「跳過語音，先用文字聊天」。不用解壓縮，也不用另裝 Python、.NET、Git 或填 Groq Key。若舊版仍在執行，請先右鍵貓咪，選「離開 NekoChat」。

本 Release 是獨立的 **NekoChat Lite**，原版 Neighbor 的版本與發布流程維持不變。附件不受 GitHub Actions 的測試產物保存期限影響。

## 本版功能

- 透明置頂、可拖曳桌面貓咪，沿用九種花色及現有 JSON 外觀素材。
- 老師建一間教室，全班使用同一份邀請碼加入；設定上限 100 台（含老師）。
- 學生可 Ping 老師，或填同學內網 IP 私訊；只轉送指定收件人。老師在 Teams 分享自己的畫面，即可呈現收到的發言。
- 多則氣泡依序顯示，點擊可回覆原發送者。
- 語音設定可跳過；有麥克風時，可使用 Groq Whisper 語音輸入／聊天草稿，個人 Key 存 Windows 認證管理員。
- 沒有 AI Agent 或遠端控制功能。

## 驗證與限制

發布附件取自通過 Windows／Linux CI 的同一個來源版本，校驗碼與具體來源／CI 紀錄見 Release 的 `release-provenance.json`。CI 包括 C#、Relay 真實 WebSocket、三來源 IP 訊息隔離、Windows 房間／氣泡／憑證與安裝／升級／啟動驗證。

此為尚未簽署的教室試用版。實體教室網路／AP 隔離、Teams 分享、100 台同時負載及實際麥克風／前景輸入仍需現場驗收。學校若阻擋安裝或連線，請由管理者協助；不需停用防護。

操作步驟見 Release 附件的兩頁 PDF，或儲存庫中的[老師／同學操作單](../CLASSROOM-QUICKSTART.zh-TW.md)。
