# NekoNode / NekoChat Windows Lite

Windows 10/11 x64，桌面貓咪、好友文字聊天、全系統語音輸入與聊天語音草稿。本功能位於專用 Windows repo，與原有 Neighbor 語音程式並列；使用獨立安裝目錄、設定與憑證名稱，不取代既有安裝。測試 Lite 語音時請先退出原版，避免相同快捷鍵被占用。不含 AI Agent 或遠端控制。

## 同學安裝：一個安裝檔

帶到教室可先開 [老師操作單](../nekochat/docs/CLASSROOM-QUICKSTART.zh-TW.md#老師操作)、[同學操作單](../nekochat/docs/CLASSROOM-QUICKSTART.zh-TW.md#同學操作)，或 [下載兩頁 A4 PDF](../nekochat/docs/NekoChat-Classroom-Quickstart.zh-TW.pdf?raw=1)。本次 [Windows 一鍵安裝檔 EXE](https://github.com/yuipan0611/whats-up-my-neighbor-ai-input-windows/releases/download/nekochat-lite-v0.1.0-beta.1/NekoChat-Setup-win-x64.exe) 已改用 [GitHub Release](https://github.com/yuipan0611/whats-up-my-neighbor-ai-input-windows/releases/tag/nekochat-lite-v0.1.0-beta.1)，不受 Actions 的保存期限影響。

1. 從 [NekoChat Lite Release](https://github.com/yuipan0611/whats-up-my-neighbor-ai-input-windows/releases/tag/nekochat-lite-v0.1.0-beta.1) 直接下載 `NekoChat-Setup-win-x64.exe`，雙擊開啟，不用解壓縮。
2. 按 **安裝並啟動**。安裝到目前使用者的 `%LOCALAPPDATA%\Programs\NekoChat.WindowsLite`，建立開始功能表捷徑，隨即開啟 App。內含 .NET 與房間服務，**不需另外安裝 Python、.NET、Git，也不用執行指令或改 JSON**。
3. 沒有麥克風或只想聊天時，按 **跳過語音，先用文字聊天**，直接建立／加入房間。不需要 Groq Key；選擇會記住，語音按鈕與快捷鍵暫停。之後有麥克風時，再展開 **語音設定（可選，之後也能補）**，儲存自己的 Groq Key 即可恢復語音。

這是尚未簽署的試用安裝檔。學校若阻擋，請交由教室管理者審核，不需停用安全機制。安裝不要求管理員，不設自動啟動、不改防火牆。更新前需從貓咪右鍵選單離開舊版；個人設定與 OS 憑證保留，舊程式資料夾保留在同一 Programs 目錄的 `.nekochat-previous-*` 供復原。

## 全班同房：Ping 老師與 IP 私訊

1. 老師與學生連上可互連的教室網路，全部使用同一版 App；沒有麥克風時跳過語音。
2. 老師在「開始使用」按 **建立教室房間（老師）**，把 **同一份邀請碼** 分享給全班。老師的 App 內含房間服務，最多 100 台電腦（含老師），不需另外安裝 Python。老師開啟「聊天」頁即可看即時發言。
3. 每位學生貼上該邀請碼，按 **加入房間**。App 自動取得自己的教室 IP、個別連線憑證與老師 IP，不用填帳號或改設定檔。收件人預設為老師。
4. 學生輸入訊息後按 **Ping 老師／傳送草稿**，訊息只送老師。老師收到的聊天紀錄和貓咪氣泡會顯示學生的來源 IP；多則氣泡會依序顯示，點氣泡即可選好該學生的 IP 回覆。老師使用 Teams 分享這個畫面時，同學可透過分享看到老師收到的發言。
5. 要私訊其他同學，在 **對方內網 IP** 輸入班上發的完整 IPv4，再按 **傳送**。對方也須加入同一房間；只有該收件人的 App 收到。老師 App 不會額外顯示同學彼此的私訊，沒有全班廣播。IP 非成員、選到自己、重複 IP 或離線均會提示，未送達草稿保留。
6. 老師 **離開房間**或退出 App，所有連線與邀請失效。重新上課需建立新房間並交付新邀請。學生離開後可使用同一份仍有效的邀請重新加入，會取得新個人憑證。斷線未正常清理時最多等待 90 秒；入房後未完成連線的登記 30 秒失效。

邀請碼是本教室的加入授權，請只交給本班同學。每位加入者可向房內其他成員傳文字；不需逐一設定好友。邀請僅含入房能力，不含老師、其他學生的個人憑證或 Groq Key。每台直接連到房間服務的私有 IPv4 限一個身分；只知道他人 IP 不能加入、冒充或傳訊給未加入者。內網 IP 是電腦的尋址方式，**不是學生實名認證**；多人經同一 NAT／代理時不能以此區分。

同學彼此的私訊僅轉送目標連線；LAN 仍是可信內網明文 WS，房間主機能處理轉送內容，沒有端到端加密或永久歷史。Teams 分享老師畫面時，老師收到／顯示的內容會被觀看者看見。房間政策僅在記憶體；個別房間憑證在 Windows 認證管理員暫存，離開時移除。貓咪待顯示氣泡上限 100 則，聊天頁保留有限最近紀錄，退出即清除。

新教室邀請以 `NEKOCLASS1-` 開頭；全班須使用新版 App，舊雙人 `NEKO1-` 邀請不適用新教室操作。原本管理者手動 LAN／VPS 的雙向好友授權仍獨立維持。

Windows 可能詢問房間服務的網路存取權，請依學校規範允許教室網路。相同 Wi-Fi 不保證電腦能互連；AP/VLAN 隔離時仍需管理者協助或使用已部署的 VPS WSS。此版本不自動開放公開連接埠、不繞過學校網路，也沒有新部署 VPS。

## 語音測試

- **全系統輸入**：點進記事本的文字欄，`Ctrl + Alt + Space` 開始錄音，再按一次停止。原視窗／程序／可編輯控制項仍有焦點且未按修飾鍵時才送入 Unicode 文字事件，不按 Enter。焦點改變、密碼／唯讀欄位、UI Automation 不支援、換行或輸入限制時，改複製或保留最近轉錄供手動貼上；請確認欄位實際結果。
- **聊天語音**：按「語音草稿」開始、再按停止。辨識文字先成草稿，確認後手動傳送給選定收件人，不會自動送出。
- `Esc`／取消按鈕／貓咪右鍵可取消；錄音最長 60 秒。快捷鍵衝突會提示，需處理衝突後重開 App。
- 到 Windows 麥克風設定允許桌面 App 使用麥克風。語音需要能連到 Groq；LAN 聊天成功不代表學校允許 Groq 網路。
- Groq Key 僅存 Windows 認證管理員；錄音只在記憶體，送到固定 Groq HTTPS transcription 端點，不寫錄音檔、不傳給房間服務。Provider 為可替換的 `ITranscriptionProvider`，目前使用 `whisper-large-v3`。
- 最近轉錄與有限聊天紀錄只在記憶體，退出即清除。複製文字／邀請碼後，Windows 剪貼簿歷程可能保留，請自行管理。WSS 是傳輸加密，非端到端加密；LAN 只在明確選擇房間／LAN 模式時使用可信私有網路 WS。

## 管理者與開發者

「進階連線」保留原有 LAN／VPS 手動設定。VPS 必須 WSS，不能略過 TLS 驗證；不得拿 Office／VNC URL 代替聊天端點。原有獨立 Relay 與憑證／好友設定見 `nekochat/relay/README.md`，同學的一鍵房間不需要這些步驟。

原始碼開發需 Windows .NET 10 SDK；教室房間需使用 CI 完整套件內的 `Relay/NekoChatRelay.exe`。只 `dotnet run` 而未放入 Relay 時，語音與手動連線仍可使用，建房會提示缺少內建元件。

```powershell
dotnet run --project windows-lite/NekoChat.Tests -c Release
dotnet build windows-lite/NekoChat.Windows -c Release -p:RestoreLockedMode=true
dotnet build windows-lite/NekoChat.Installer -c Release
```

Windows CI 將 Python 服務凍結為獨立執行檔，發佈自帶 runtime 的 WPF App，再嵌入單一 Setup.exe。CI 在移除 Python／dotnet 的 PATH、指定不存在的 DOTNET_ROOT 後實測 App、房間互傳、結束撤銷與安裝包驗證／解壓縮；詳見工作流程與 `nekochat/docs/VALIDATION.md`。另外保留 `nekochat-windows-lite-trial` portable ZIP，不需要重新執行 PowerShell 安裝腳本。

## JSON／外觀相容

直接複用 `Examples/CatAppearance/*.json` 的九種版本 2 外觀與原版 SVG；PNG 由既有 Swift 繪製器匯出。右鍵可匯入 `cat.config.json`（版本 1/2）：支援 `appearance.preset`、`seed`、16 個部位的 `color`／`pattern`／`patternColor`，以及設定資料夾內的相對 PNG `image`。PNG 上限 5 MB、2048×2048；JSON 64 KiB。不下載 URL，不讀設定資料夾以外的圖片。

預設九種花色使用原生匯出的圖片；自訂部位使用相同解剖座標與原生虎斑遮罩繪製，邊緣與抗鋸齒可能有細微差異。WPF 桌面貓已接入核准的九色原生動畫：散步、舔前掌、打哈欠、趴睡、好奇、被摸與抓起／放下。抓起 850ms、放下 400ms，坐好即停；播放中切色保留動作和位置。聊天氣泡／錄音提示顯示與右鍵選單開啟時暫停自主散步；系統關閉介面動畫時停用自主動作。自訂 PNG／部位匯入仍使用靜態圖，避免拿預設動畫覆蓋自訂作品；切回內建花色恢復動畫。macOS 的招呼 triggers、字型、氣泡配置與冷卻秒數仍不在 Lite 範圍。匯入外觀此次啟動有效，內建花色選擇會記住。

Windows 建置直接使用已提交素材，不需要 Swift 或 macOS 原始碼。素材原始繪製器與匯出工具保留於 [macOS 原始來源](https://github.com/yuipan0611/whats-up-my-neighbor-ai-input-macos/tree/ed340c4556ae9794bb2bf9b81319d62ae1a857d9/windows-lite/tools)，此 Windows repo 不包含／依賴 Swift App。匯出與搬移來源見 [搬移紀錄](../nekochat/docs/WINDOWS-REPO-MIGRATION.md)。

## 驗證與已知限制

CI 包含獨立房間服務與安裝器測試、Linux Relay／C# 真實 WebSocket 整合、Windows WPF／花色／憑證與流程畫面。CI 不代表實體教室網路、麥克風或任意前景 App 的實機驗收。

手動驗收：見儲存庫 `nekochat/docs/LAN-TEST.md`。本機與 CI 的實際結果記錄於 `nekochat/docs/VALIDATION.md`；未經互動式 Windows 使用者驗證前，請視為試用 MVP。macOS App 尚未接上 NekoChat；現有 Office／VPS remote 協議不相容，不能拿 Office URL 當聊天 Relay。

移除：先離開 App，在設定按刪除 Groq Key／Relay 憑證，刪除安裝資料夾、開始功能表捷徑及 `%LOCALAPPDATA%\NekoChat.WindowsLite`。也可在 Windows 認證管理員刪除 `NekoChat.WindowsLite/` 開頭的 Generic Credentials。解除安裝不撤銷 Relay 憑證；請管理者刪除帳號或輪替 token 後重啟 Relay。


## 已整合的口鼻與動畫

賓士貓口鼻白斑使用與 Mac 同源的曲線覆蓋，嘴線完整落在白毛內；其他八色與原畫輪廓不變。`NekoChat.Animation` 封裝九色原生 PNG 與時間表，作為單一內嵌資源，不增加安裝器檔案數限制、不需要 Swift、不下載素材。來源與重現步驟見 `NekoChat.Animation/README.md`。現有花色 ID、設定資料夾、語音／聊天憑證與手動連線流程不變。程式碼整合不等於新版安裝檔已正式發布。
