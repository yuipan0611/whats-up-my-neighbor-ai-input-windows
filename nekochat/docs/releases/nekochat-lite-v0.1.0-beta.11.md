# NekoChat Lite v0.1.0-beta.11 — 離線續送與已讀定位修正

**[下載 Windows 安裝檔（建議）](https://github.com/yuipan0611/whats-up-my-neighbor-ai-input-windows/releases/download/nekochat-lite-v0.1.0-beta.11/NekoChat-Setup-win-x64.exe)** · [免安裝版](https://github.com/yuipan0611/whats-up-my-neighbor-ai-input-windows/releases/download/nekochat-lite-v0.1.0-beta.11/NekoChat-Lite.exe)

## 本版更新

- 文字、程式碼、圖片與檔案會先安全保存到本機待送佇列；網路恢復或 App 重新開啟後會自動續送，重試沿用原訊息 ID，避免重複訊息。
- 送出時保留後續草稿；失敗訊息可從原位置重試，已取消或舊失敗項目不會自行送出。
- 已讀依聊天窗實際呈現的位置更新，包含之前尚未載入的舊訊息；停在較早位置時，新訊息仍保留未讀。
- 修正背景自動保存與按下傳送同時發生時的交接，避免內容未保存或送出按鈕錯誤拒絕。

## 驗證

來源為此版本的 main commit。NekoChat Lite Windows、Relay 與原生封裝／安裝 CI 全數通過；驗證封裝版、免安裝版、安裝後版本的訊息恢復、傳送／接收、更新下載及 Setup 安裝流程。

- [NekoChat Lite Windows／Relay CI 與發布產物](https://github.com/yuipan0611/whats-up-my-neighbor-ai-input-windows/actions/runs/42077063544)

## 已知驗收範圍

這是未簽署的 Windows x64 預發布測試版。實體教室雙 Windows、教室網路、中文輸入法及實體多螢幕 DPI 尚未完成現場驗收；CI 的 loopback 與合成資料不能取代這些測試。第一次執行時 Windows SmartScreen 可能顯示提示，請只從本 Release 下載並核對同版 SHA-256。
