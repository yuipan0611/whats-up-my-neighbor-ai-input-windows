# Windows 版改善交付說明

本文件整理 Windows 版自原作者 repo 拆分後，在 `andrewchu0101` 的 fork 中完成的改善，供原作者審查與決定是否合併。

對照版本：0.1.0-beta.2（`VERSION`）。本文件所列的 commit 皆以上游 `main`（`9e767ea`）為基準。

## 1. 來源與署名

- **原有程式碼**：來自原作者 yuipan0611 的 `whats-up-my-neighbor-ai-input-macos` 與 Windows 分支（見 [MIGRATION.md](../MIGRATION.md)）。授權為 MIT，版權聲明保留在 [LICENSE](../LICENSE)。
- **本 fork 的改動**：由 andrewchu0101 維護，包含本文件列出的所有 commit。這些改動沒有改變原授權。
- **原作者的 macOS 版**：本文件不改動 macOS 版，也不推定 macOS 版存在相同缺陷。

## 2. 本批改善摘要

| 類別 | 內容 |
|---|---|
| 版本與發布 | 版本號統一由 `VERSION` 決定；發布流程只在全部測試與 smoke 通過後建立預發布版本 |
| 錄音 | 支援只接受混音格式的麥克風（例如 48 kHz 立體聲浮點），錄音時轉為 16 kHz 單聲道 |
| 錄音浮窗 | 改用逐像素 Alpha 的分層視窗，移除色鍵透明造成的紫色外框；貓圖快取降低每幀成本 |
| 桌寵（第一階段） | 預設關閉；沿用原 SVG 素材的散步與休息；可拖曳，放開後水平散步；控制器集中管理建立、狀態與失敗處理 |
| 無障礙 | 錄音浮窗的左右眼以 MSAA PushButton 暴露，名稱隨狀態變化，預設操作與滑鼠共用同一判斷 |
| 啟動穩定性 | 修正**本分支開發期間引入**的分層視窗啟動崩潰（DIB 寫入錯誤）；分層視窗失敗改為明確拋出。此問題不存在於上游 `main` |
| 測試基礎 | 離線測試 563 項；新增 smoke 閘門（會實際啟動程式）與閘門自測試 |

## 3. 各項改善

### 3.1 版本來源與發布流程（`613af43`）

- **問題**：版本號散落在 csproj、安裝檔、打包與測試腳本中，容易不一致。
- **方案**：根目錄 `VERSION` 為唯一來源；`release.yml` 在全部步驟通過後才建立預發布版本，並附上同次建置的安裝檔、校驗碼、清單與測試紀錄。
- **主要檔案**：`VERSION`、`packaging/neighbor.iss`、`scripts/package.ps1`、`scripts/test-install.ps1`、`.github/workflows/release.yml`、`RELEASE_NOTES.md`。

### 3.2 麥克風格式相容（`2c9d015`）

- **問題**：直接要求 16 kHz、16 位元、單聲道的共享模式串流，部分麥克風會拒絕，導致錄音無法開始。本機探測的 USB 麥克風只接受 48 kHz 立體聲浮點格式。
- **方案**：擷取時使用裝置的混音格式，再由 `CaptureFormatConverter` 轉換為 16 kHz 單聲道 PCM。轉換器只輸出有足夠輸入支撐的樣本，不補零，因此讀取迴圈一定會結束。
- **主要檔案**：`Neighbor.Windows/Platform/CaptureFormatConverter.cs`、`WasapiRecorder.cs`、`Neighbor.Tests/PlatformTests.cs`。
- **限制**：合成訊號測試通過；真實麥克風的錄音品質尚未以人工驗收。

### 3.3 桌寵第一階段與分層視窗（`fec4925`、`f75421c`）

- **問題**：色鍵透明沒有 Alpha，抗鋸齒邊緣與洋紅混色，形成紫色外框。桌寵與錄音浮窗都受影響。
- **方案**：`LayeredWindow` 以 `UpdateLayeredWindow` 與預乘 32bpp ARGB 位圖送出畫面。拖曳由 `DesktopPetMotion` 模型處理（保持抓取位置、拖曳中暫停、放開後水平散步、螢幕改變時夾回可見範圍）。
- **啟動崩潰修正（開發期間引入，已修復）**：`Present` 曾把 DIB 控制代碼當成像素指標寫入，每次顯示 HUD 或桌寵時都會存取違規。這是本分支分層視窗改動期間產生的錯誤，上游 `main` 沒有此問題。已改為寫入像素指標，並新增回歸測試（修正前紅燈、修正後綠燈）。
- **主要檔案**：`Neighbor.Windows/UI/LayeredWindow.cs`、`DesktopPetForm.cs`、`RecordingHud.cs`、`Neighbor.Core/DesktopPetMotion.cs`、`assets/desktop-pet/`。
- **素材與授權**：SVG 素材複製自原 macOS 專案的 `Resources/DesktopPet/`，來源與授權見 `assets/desktop-pet/README.md`。

### 3.4 工作區語意統一與初始位置（`81da6dc`）

- **問題**：`Advance` 的 `right` 參數已扣除寵物寬度，`Fit` 的 `right` 則是完整工作區，兩套語意容易混用。
- **方案**：所有方法都接收完整工作區與 `scale`，寵物尺寸只在模型內計算；初始位置集中到 `PlaceInitially`。速度、休息與朝向規則不變。
- **測試**：新增 15 項特徵測試（代表點、狀態轉換、左右邊界、負座標、狹窄工作區、縮放、放開後水平移動）。

### 3.5 桌寵控制器（`4f99c68`）

- **問題**：桌寵的建立、開關、狀態傳遞與失敗處理散落在 `NeighborApplicationContext` 中，約 6 處。
- **方案**：`DesktopPetController` 集中管理。建立或執行失敗時停用桌寵、只記錄一次、只通知一次，不改使用者保存的偏好，也不影響聽寫。`IDesktopPetView` 讓測試使用假視窗，不建立真實視窗。
- **測試**：17 項控制器測試。
- **主要檔案**：`Neighbor.Windows/DesktopPetController.cs`、`NeighborApplicationContext.cs`。

### 3.6 smoke 閘門與 Present、GDI 測試（`9790e25`）

- **問題**：本機建置無法在打包前攔截真正的啟動崩潰；分層視窗的錯誤路徑與資源釋放沒有測試。
- **方案**：`scripts/smoke-gate.ps1` 以獨立的 `TEMP` 資料夾執行 `Neighbor.exe --smoke-test`，超時或非零退出碼都會讓建置失敗，只刪除自己建立的資料夾。閘門自測試使用 `cmd.exe` 與 `ping.exe`，不破壞正式程式。
- **GDI 測試**：暖機後量測 5 批次，成功路徑基準 22、每批皆 22（增長 0）；失敗路徑基準 16、每批皆 16（增長 0）；門檻 4（理由見 `docs/TESTING.md`）。
- **Win32 錯誤碼**：無效與已銷毀的視窗控制代碼實測為 1400（`ERROR_INVALID_WINDOW_HANDLE`）。

### 3.7 錄音浮窗貓圖快取（`e086ce0`）

- **問題**：每幀以 `HighQualityBicubic` 把 1774×887 的貓圖縮放到 340 寬。
- **方案**：`ScaledCatCache` 依實際尺寸（含 DPI）只縮放一次。尺寸或來源改變時重建，舊圖立即釋放；釋放後的存取會明確拋出例外。
- **畫面不變**：改動前後 27 組 `RenderFrame`（狀態、音量、懸停）的像素指紋完全相同（僅本機比對）。
- **測試**：快取失效、尺寸與 DPI 切換、來源改變、重複釋放、與直接縮放逐像素相同（100% 與 150%）。

### 3.8 錄音浮窗的操作判斷與無障礙（`3298d91`）

- **問題**：左右眼不是 UI 元素，輔助工具看不到；點擊判斷只存在於 `OnMouseUp`。
- **方案**：
  - `RecordingHudLayout.ActionFor(target, state)` 是唯一判斷來源。滑鼠點擊、懸停與無障礙預設操作都使用它。
  - `Activate` 是共用入口，在執行時讀取狀態。
  - 右眼在 `RetainedAudio` 時觸發重試（`RetryRequested`），不再經由開始事件轉換。
  - 覆寫 `CreateAccessibilityInstance` 無效（公開屬性為 null，MSAA 子元素為 0）。改在 `WndProc` 處理 `WM_GETOBJECT`（`OBJID_CLIENT`），用 `LresultFromObject` 回傳容器。
  - 位置、尺寸與狀態改變時，以 `NotifyWinEvent` 送出 LocationChange 與 NameChange，子 ID 為 1（左眼）與 2（右眼）。
- **測試**：第一批 42 項操作判斷測試；第二批 35 項 MSAA 檢查；突變測試確認兩批測試都會抓到錯誤。
- **主要檔案**：`Neighbor.Windows/UI/RecordingHud.cs`、`NeighborApplicationContext.cs`、`Neighbor.Tests/RecordingHudInteractionTests.cs`、`RecordingHudAccessibilityTests.cs`。

## 4. macOS 可參考的設計原則，與僅適用 Windows 的實作

**可參考（設計原則，不代表 macOS 有相同缺陷）**

- 版本號只由一處決定，發布產物必須來自同一次通過全部測試的建置。
- 桌寵與浮窗的透明效果應使用逐像素 Alpha，而不是色鍵。
- 操作判斷（點擊、懸停、無障礙）應共用同一規則，避免分歧。
- 會影響啟動的功能需要真正啟動程式的閘門，而不只是單元測試。
- 量測必須固定條件，分開記錄工具本身的成本。

**僅適用 Windows 的實作**

- `UpdateLayeredWindow`、`WM_GETOBJECT` 與 `LresultFromObject`、`NotifyWinEvent`、WASAPI 格式轉換、Windows 認證管理員、Inno Setup 安裝程式、PowerShell 腳本。

## 5. 量測結果

### 5.1 量測條件

- 工具：`D:\DevCache\perf-harness`（不在產品專案內，未納入 repo）。
- 方式：隱藏的量測視窗（不含桌面合成器成本）、100% DPI、合成資料、無真實錄音與 API。
- 每個情境暖機 5 秒，之後重複 3 次、每次 30 秒。
- CPU 百分比：程序 `TotalProcessorTime` 增量 ÷ 牆鐘時間 × 100%，以單一邏輯核心為基準（包含該程序所有執行緒）。

### 5.2 結果

| 情境 | 版本 | CPU | 每幀耗時 | Present 平均／p95 | 配置 | GDI 增長 |
|---|---|---|---|---|---|---|
| 錄音浮窗 | 快取前 | 33.4–35.2% | 17.2–17.6 ms | 0.48–0.50／0.88–0.90 ms | 5.7 MB/s | 0 |
| 錄音浮窗 | 快取後 | 3.1–3.4% | 1.3–1.5 ms | 0.62–0.72／1.24–1.52 ms | 5.7 MB/s | 0 |
| 桌寵散步 | 快取前 | 1.66–1.82% | 1.05–1.32 ms | 0.35–0.39／0.72–0.81 ms | 0.8 MB/s | 0 |
| 桌寵散步 | 快取後 | 1.25–1.72% | 0.99–1.12 ms | 0.33–0.38／0.66–0.81 ms | 0.8 MB/s | 0 |

### 5.3 限制

- headless 結果不等同真實桌面表現；不含桌面合成器、音訊回呼與真實錄音。
- 快取後的 `Present` 平均由 0.48–0.50 ms 升到 0.62–0.72 ms，原因未確認（程式碼未改動）。
- 每幀仍配置一張整張位圖（5.7 MB/s）。大物件造成頻繁 gen2 GC 的說法**只是推測**，尚未有分代暫停時間的量測證據。
- 桌寵改動前後的差異屬於統計誤差。

## 6. 測試、CI 與人工驗收

### 6.1 自動測試與 CI（已通過）

- 離線測試：**563 項通過、0 失敗**，建置無警告（`build.ps1` 的建置與離線測試步驟）。
- 本機完整 `build.ps1`（含 smoke）：通過，執行前後沒有殘留的 smoke 暫存資料夾或程序。
- Windows CI（GitHub Actions）：
  - `e086ce0`：run `39821696366`，成功。
  - `6796e3a`：run `39825929466`，成功（包含 `build.ps1` 與 smoke 閘門）。

### 6.2 可見 HUD 的 MSAA 整合驗證（通過）

工具位於 `tools/a11y-verification/`，不納入安裝包，也不被 `build.ps1` 執行。以合成資料從外部程序驗證：

- 子元素數 2、角色 PushButton（43）、七狀態名稱與啟用狀態。
- 預設操作觸發的事件種類與次數：Idle 右眼 Start、Recording 左眼 Cancel 與右眼 Finish、RetainedAudio 右眼 Retry。
- `Delivering` 與隱藏時不觸發。
- 移動後視窗位置正確，眼睛位置與視窗位移一致。
- LocationChange（子 ID 1、2）與 NameChange 通知送達客戶端。
- 焦點前提成立，操作前後輸入框保持前景與焦點。
- 關閉後 HUD 視窗已銷毀（`IsWindow` 為 false）。

「關閉後沒有事件」只是弱檢查；釋放後的行為由單元測試保護。

### 6.3 人工驗收（使用者回報）

- 新版試用正常（貓圖快取版，SHA-256 `07dadb9e…`）：桌寵散步與拖曳、透明與焦點、錄音浮窗、聽寫與貼上，未發現阻礙使用的問題。範圍僅限使用者實際試用的情境。
- 早期的人工驗收對應 Ticket D 之前的版本（`9790e25`），不能當作重構後的驗收。

### 6.4 不支援與尚未驗收

- **UIA Invoke：不支援**。橋接沒有暴露任何按鈕元素，也沒有 InvokePattern。`WM_GETOBJECT` 目前只處理 `OBJID_CLIENT`，沒有處理 `UiaRootObjectId`。此項記錄為不支援，不算通過。
- **Narrator 與 NVDA 朗讀**：未驗收。
- **多螢幕與 DPI 下的座標與通知**：未驗收。
- **焦點被其他應用程式搶走時的行為**：未驗收。
- **Windows 10 完整安裝、升級與解除安裝（真實舊版資料）**：只有合成資料的安裝測試。
- **ARM64**：未驗證。
- **多人會議、iPhone 接力、跨裝置詞典同步、本機 AI 模型**：不在本版範圍。

## 7. 已知問題與延後項目

- 錄音浮窗每幀配置整張位圖（5.7 MB/s）。重用整張位圖尚未實作，列為延後項目（Ticket C 的後續）。
- UIA 支援需要另外設計（處理 `UiaRootObjectId` 或提供 `IRawElementProviderSimple`），本批不做。
- 桌寵的散步速度與 macOS 版不同（固定 55 px/s、最多 7 秒，macOS 版約 2–9 秒走完、使用平滑曲線）。
- 桌寵沒有點擊、舔掌、抓起動畫與資料夾停靠。
- 錄音浮窗的眼睛沒有停用的視覺樣式，停用狀態只存在於無障礙的 `State`。
- **左右眼無障礙回歸（開發期間引入，已修復）**：分層視窗改動把原本的子控制項（`CatActionArea`，具有 PushButton 角色與名稱）移除，導致輔助工具看不到左右眼。已以 `WM_GETOBJECT` 重新提供 MSAA 子元素，此回歸存在於本分支的中間版本，不是上游的問題。
- `LocationChange` 通知已送出，但 Narrator 與 UIA 客戶端是否正確更新，尚未驗證。
- `release.yml` 在本分支已改為僅手動觸發（`workflow_dispatch`）。fork 的 `main` 仍保留推送標籤觸發的既有流程。

## 8. 上游差異與合併注意事項

- **上游基準**：`yuipan0611/whats-up-my-neighbor-ai-input-windows` 的 `main` 為 `9e767ea`，與本 fork 的基準相同（已確認）。
- **差異範圍**：本分支相對上游的改動來自本 fork 的 commit，數量與檔案清單以 PR 頁面為準。
- **交付分支已處理的設定**（只在 `handoff/windows-beta2-improvements` 上，fork 的 `main` 不變）：
  - 安裝程式的發行者、支援與更新網址（`packaging/neighbor.iss`）指向上游 repo。
  - 下載、支援與更新連結（README、CONTRIBUTING、RELEASE_NOTES、docs/UPDATES、docs/DEVELOPMENT、docs/WEBSITE、使用說明.txt）指向上游 repo。
  - 來源與維護說明（README 第 9 行、RELEASE_NOTES 第 5 行、docs/WEBSITE 維護者列）保留 fork 連結，並標明它是貢獻來源。
  - `docs/WEBSITE.md` 的直接下載網址仍標示為**預計網址**，上游尚未建立此 Release，不能當作可用連結。
- **發布設定**：`release.yml` 在本分支只保留 `workflow_dispatch`，由原作者決定何時發布。
- **上游 PR #2**（`codex/official-website-links`，作者 yuipan0611，修改 `README.md`）：本分支把 PR #2 新增的官網連結原文保留在 README 標題下方。其餘 README 內容是本分支的 Windows 版說明。若 PR #2 先合併，合併時應以本分支的 README 為準，但保留官網連結那一行。
- **建議處理**：先由原作者審查程式與測試，再決定 fork 的連結與發布設定是否要保留。

## 9. 交付文件與驗證工具的位置

- 本文件：`docs/WINDOWS-IMPROVEMENTS.md`
- 驗證紀錄：`docs/TESTING.md`
- 桌寵：`docs/DESKTOP-PET.md`
- 驗證工具：`tools/a11y-verification/`（不納入安裝包）
- smoke 閘門：`scripts/smoke-gate.ps1`
