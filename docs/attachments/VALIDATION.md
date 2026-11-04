# 附件修復驗證

基準：ecbed3dd9b679b8569b44fda80243d8f352d8b1e。獨立分支 codex/nekochat-attachments-20261104。產品與測試驗證來源：54a8e2b80034e24fa22d9162dfb042fb89eecd3c。後續交付文件／CI commit 不改這份產品碼。

## 結果

| 檢查 | 結果 | log SHA-256 |
|---|---|---|
| final-core | exit 0 | 7b5c6b868b668753c187c92d779823ce6490a16ef2a85d0c537dd9427e7a3fc2 |
| final-classroom | exit 0 | d2356eecca9bf1a2883f8b113b23566d05807f87e8ef9f18e01be5f719ad0b6f |
| final-kill | exit 0 | 522d270ec0ba83d633d41ac3a2c2839eed16fb0755f8dafe81855914b66d9975 |
| final-animation | exit 0 | 970d3d0115659559c348be2d17f08253e776d73504b42814daa1f11c6e8ad749 |
| final-restore | exit 0 | 5df39ddee2f0867e790bba6e762f4181bcbc47c89cac2a9917737665c6451924 |
| final-windows | exit 0 | 528b513514380480718c9a39112e31dea69326d3b890ec5c0e4638ef373a921e |
| final-packaging-tests | exit 0 | 34a82a30511a2175a5286cff0751874e9220ce5fb08cab87b09cc04e220f6b31 |

Core 6,363 項；教室15組全過，其中附件69項；教室強殺202項／32次；動畫4,361項；Windows win-x64交叉建置0警告、0錯誤；打包工具11項單元測試。

原始log與逐項指令、真實load在 build/attachment-evidence/final-validation.json。早期失敗log保留，包括新增migration後舊10／26數量斷言和取消分類錯誤；均修正後重驗，沒有刪除或放寬行為斷言。schema斷言明確更新為11個migration／28個STRICT表，備份v1～v11回歸保留。

## 證據範圍

- 真TCP兩節點＋真SQLite＋實際位元組和SHA-256：原檔修改後仍傳快照、中文caption、純附件、名單自動分流、6MiB分塊、取消與手動重試、錯誤權杖／區塊範圍、毀損內容、空檔、空間不足、舊版拒絕、清除世代與去重。
- 附件另外強殺3個真子程序，分別在.part、原子更名後、完成SQL提交後停止，再重開確認真實資料。這些包含在附件69項裡。
- 名單內自動接收使用明示的合成loopback名單fixture；不放寬產品的名單IP驗證。
- 新增庫容量預留與512MiB上限；額滿測試以小容量注入重現同一產品路徑，25MiB精確邊界另驗。
- 一般／窄視窗HTML預覽已由瀏覽器檢視；它是設計參考，不是WPF或Windows互動證據。

## 未執行

Windows原生Ctrl+V、右鍵貼上、拖曳、選檔、WPF渲染、IME、DPI，以及兩台實際Windows和教室網路均未執行。操作單見 WINDOWS-ACCEPTANCE.md。尚未安裝、push、PR、合併、正式發布或部署。CI設定已補classroom-all，但未觸發遠端CI。

## 候選與回退

測試ZIP及來源／檔案雜湊放在 build/attachment-candidate-20261104/，以該目錄candidate-manifest.json為準。這是self-contained win-x64資料夾ZIP，不宣稱三種包裝或安裝器已驗收。只用 --ui-test-data 合成資料啟動；migration11會建立升級前備份，舊程式不能直接打開新版資料庫。現有SQLite備份不含附件bytes，缺少副本會標示不可用。

待完成：執行Windows操作單、修正任何原生／雙機發現，再由使用者決定發布。
