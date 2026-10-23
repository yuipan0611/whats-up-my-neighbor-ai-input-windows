# 開發環境與本機建置

這份文件說明如何在 Windows 上取得原始碼、建置、測試並打包安裝檔。

對照版本：0.1.0-beta.2（見 [VERSION](../VERSION)）。

## 需要準備

| 項目 | 說明 |
|---|---|
| Windows 11 x64 或 Windows 10 22H2 x64 | 建置與執行環境。UI 整合測試需要可用的桌面工作階段。 |
| .NET SDK 8.0 | 建置 `net48` 專案。`dotnet` 需可從 PATH 執行。 |
| .NET Framework 4.8 targeting pack | 可由 `Microsoft.NETFramework.ReferenceAssemblies` 套件提供（已在專案中鎖定）。 |
| Inno Setup 6 | 產生安裝程式。安裝後 `scripts/package.ps1` 會自動找到 Program Files 或使用者資料夾中的 `ISCC.exe`。 |
| PowerShell 7（建議） | CI 使用 PowerShell 7。Windows PowerShell 5.1 也可以執行腳本，但 `test-install.ps1` 使用 .NET 5 以上的 API，只能在 PowerShell 7 完整執行。 |
| 網路 | 第一次還原 NuGet 套件時需要。 |
| Groq API Key（選用） | 只有實際錄音辨識時需要。建置與測試都不需要。 |

確認工具：

```powershell
dotnet --list-sdks
ISCC.exe /?      # 若不在 PATH，請使用完整路徑
pwsh --version
```

Inno Setup 可用 winget 安裝（會安裝到使用者資料夾，打包腳本可直接找到）：

```powershell
winget install --id JRSoftware.InnoSetup -e
```

## 取得原始碼

```powershell
git clone https://github.com/yuipan0611/whats-up-my-neighbor-ai-input-windows.git
cd whats-up-my-neighbor-ai-input-windows
```

## 建置與離線測試

```powershell
./scripts/build.ps1
```

這會還原套件（`--locked-mode`）、建置測試專案，並執行離線測試。全部通過時最後一行是 `RESULT: <數量> passed; 0 failed.`，測試報告寫在 `Neighbor.Tests/bin/Release/net48/test-results/offline-tests.txt`。

離線測試不開啟視窗、不讀取正式金鑰、不錄真實麥克風、不呼叫 Groq。

## UI 整合測試

```powershell
./Neighbor.Tests/bin/Release/net48/Neighbor.Tests.exe --interactive
```

這個模式會開啟隔離的文字視窗並使用剪貼簿與鍵盤注入。**請只在可以接受的測試時段執行，不要在正在工作的桌面上跑。** 報告寫在 `test-results/ui-tests.txt`。

## 打包安裝檔

```powershell
./scripts/package.ps1
```

腳本會：

1. 讀取 `VERSION`，清空 `dist/`。
2. 依固定清單複製執行檔與相依 DLL 到 `dist/app/`，並加入使用說明、第三方聲明與授權。
3. 檢查 `Neighbor.exe` 內的版本與 `VERSION` 一致，並確認使用說明存在。
4. 以 Inno Setup 產生 `dist/Neighbor-Setup-<版本>-x64.exe`，檢查大小小於 20 MB。
5. 產生 `.sha256` 與 `build-manifest.json`。

## 安裝、升級與解除安裝測試

```powershell
./scripts/test-install.ps1
```

這會在目前使用者下安裝合成的基準版本、升級到目前版本、執行冒煙啟動、再解除安裝，並核對登錄資訊、檔案雜湊與隔離的測試資料。**它會實際寫入目前使用者的 `%LOCALAPPDATA%\Programs\WhatsUpMyNeighbor-CI` 與 HKCU 登錄，測試結束會清理。** 最好在一次性的 Windows 測試帳號執行。

## 更改版本

只改 `VERSION` 一個檔案。格式為 `MAJOR.MINOR.PATCH` 加上可選的預發布後綴，例如 `0.1.0-beta.3`。

## 專案結構

```text
Neighbor.Core/           與 Windows 介面無關的核心邏輯
Neighbor.Windows/        系統匣 App、錄音、貼上、快捷鍵、認證管理員與 UI
Neighbor.Tests/          離線測試與受控桌面 UI 測試
packaging/               Inno Setup 安裝檔設定
scripts/                 建置、打包與安裝測試腳本
assets/                  圖示與黑貓圖片
docs/                    開發者文件
.github/workflows/       CI（windows.yml）與正式發布（release.yml）
VERSION                  版本號唯一來源
RELEASE_NOTES.md         發布說明範本
```

模組分工見 [ARCHITECTURE.md](ARCHITECTURE.md)。

## 常見問題

**`dotnet` 找不到 SDK**：安裝 .NET SDK 8.0，並重新開啟終端機讓 PATH 生效。只有執行環境（runtime）不夠。

**`ISCC.exe` 找不到**：用 `winget install --id JRSoftware.InnoSetup -e` 安裝，或把 `ISCC.exe` 所在資料夾加入 PATH。腳本不會修改全域 PATH。

**`Missing runtime payload`**：先執行 `./scripts/build.ps1`，它會建置 `Neighbor.Windows` 的 Release 輸出。

**`test-install.ps1` 報錯 `GetRelativePath`**：改用 PowerShell 7 執行，與 CI 相同。

**`Existing Neighbor installation detected`**：測試安裝腳本拒絕覆蓋既有安裝。請在一次性的測試帳號執行，或先解除安裝既有版本。
