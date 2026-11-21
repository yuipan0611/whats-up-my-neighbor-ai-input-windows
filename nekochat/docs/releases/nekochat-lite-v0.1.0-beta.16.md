# NekoChat Lite v0.1.0-beta.16 — 新教室 IP 更新

**[下載 Windows 安裝檔（建議）](https://github.com/yuipan0611/whats-up-my-neighbor-ai-input-windows/releases/download/nekochat-lite-v0.1.0-beta.16/NekoChat-Setup-win-x64.exe)** · [免安裝版](https://github.com/yuipan0611/whats-up-my-neighbor-ai-input-windows/releases/download/nekochat-lite-v0.1.0-beta.16/NekoChat-Lite.exe)

- 新教室網段改為 `192.168.21.*`；學生座位末段維持 `.5`、`.10` 至 `.185`，老師為 `192.168.21.240`。
- 偵測到本機位於 `192.168.21.*` 時，啟動會將既有名單的 `192.168.24.*` 位址改為 `192.168.21.*`，只改第三段，保留聯絡人 ID、備註與老師身分。若新 IP 已存在，保留原聯絡人並提示確認，不自動合併。
- 聊天紀錄、訊息原始 IP、草稿及附件內容保持原樣；已移除的聯絡人仍不會被預設名單補回，封鎖名單同步搬到新網段。
- 轉換在網路服務啟動前以資料庫交易完成；重複啟動不會重複轉換，資料庫寫入失敗時整批回復。
- `beta.15` 已啟用自動更新者會沿用原本的背景更新流程；也可從貓咪選單「檢查更新…」手動更新。安裝升級保留本機設定與聊天資料。

這是未簽署的 Windows x64 預發布測試版。自動轉換需在新教室網路啟動程式；`classroom-roster.csv` 自訂名單仍優先於內嵌預設名單。歷史待送項目不會因本次升級改寫目的地，請檢查並按需要重新傳送。
