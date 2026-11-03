# 可重做的本機交叉包裝

工具只建置、測試與讀取產物；不安裝、簽章、執行 Windows App／Setup、上傳或發布。使用 Python 3.11+、Git 與 .NET 10 SDK；Windows 建議採 CI 相同的 Python 3.12。NuGet 以既有 lockfiles 還原。來源先 commit，工作樹需乾淨，輸出用 checkout 下全新的 `build/` 子目錄，既有包裝不覆寫。

```sh
python3 windows-lite/tools/build_delivery.py --dotnet /完整路徑/dotnet --output build/delivery-new
```

在本工作區把 `NUGET_PACKAGES` 指向桌面 dependencies/nuget-nekochat-windows，SDK 為 dependencies/dotnet-10.0.403/dotnet。換機可用 PATH 上的 `dotnet`，manifest 會記錄實際 SDK／來源 SHA，不宣稱不同 SDK 位元組相同。腳本先執行 Core／動畫 Release suites，再 publish完整包／真正單檔／Setup，核對 payload、bundle 與 SHA。測試失敗不產生成功 manifest。

主要產物是完整 ZIP、`NekoChat-Lite.exe`、Setup EXE；附 `.sha256`、`delivery-manifest.json`、`portable-bundle.json`、DELIVERY 及完整 logs。full／portable／setup 子目錄是可追溯的中間產物。App 的單檔 profile 明確包含全部內容，runtime 以 extraction base 載入既有 Assets／Coats；普通 App 的身分方案／L 不因此設定。

## 可分別重跑的驗證

```sh
python3 -m unittest discover -s windows-lite/tools -p 'test_*.py'
python3 windows-lite/tools/verify_bundle.py build/產物/NekoChat-Lite.exe --repo . --output build/產物/portable-bundle.json
python3 windows-lite/tools/prove_single_file.py --repo . --dotnet /完整路徑/dotnet --output build/新的-mac-proof
```

最後一條只在 Mac 執行隔離 console probe：兩次都只複製執行檔到空目錄；舊 native-only mode 預期因缺檔失敗，all-content mode 預期成功讀到同 bytes 的原圖／花色，且 AppContext 指向工作區內 cache。它不是 Windows／WPF 證據，也不會讀取日常聊天／金鑰或安裝 App。

`verify_bundle.py` 依官方 .NET10.0.14 v6 格式解析 bounded offset／size／字串／路徑／重複與重疊，核對 compat flag、managed runtime inputs 與所有已追蹤的內建素材／花色。來源內容若混入未追蹤或 linked inputs 就拒絕，避免 ignored 資料跟進 publish glob。它不載入 assembly，不將 metadata 通過解讀成 Windows 字型／DPI／權限或 Authenticode 驗證。`PackageVerifier` 只用 PE metadata 驗 Setup 嵌入 ZIP/checksum，並檢查 bundle 內的精確 assembly bytes。

官方格式與行為來源：

- [.NET 單檔部署](https://learn.microsoft.com/en-us/dotnet/core/deploying/single-file/overview)
- [10.0.14 header／compatibility base](https://github.com/dotnet/runtime/blob/v10.0.14/src/native/corehost/bundle/header.h)
- [marker 與 offset](https://github.com/dotnet/runtime/blob/v10.0.14/src/native/corehost/apphost/bundle_marker.cpp)
- [FileEntry binary write](https://github.com/dotnet/runtime/blob/v10.0.14/src/installer/managed/Microsoft.NET.HostModel/Bundle/FileEntry.cs)

Windows CI 原有 EXE-alone 隔離原生檢查保留，新增靜態 bundle proof 及合成 Cancel controls gate；這些 workflow 目前只更新來源，未遠端執行。Windows 原生操作單另見 PAIRING-VERIFICATION／SEND-CANCELLATION／IDENTITY-VERIFICATION，不把同機／Mac／routed 自動操作充當人類比碼或雙機教室驗收。
