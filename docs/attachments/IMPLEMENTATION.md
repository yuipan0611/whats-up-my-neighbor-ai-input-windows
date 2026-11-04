# 真實附件實作

基準 ecbed3dd9b679b8569b44fda80243d8f352d8b1e。高風險邊界是 additive SQLite migration、LAN 協定與取消／清除競態。保護既有文字、P1 封存、來源 IP 歸屬、草稿 epoch、核准素材和其他工作樹。

入口 → 受控檔案快照 → 附件草稿 → 能力探測／offer → 接收端分塊 pull → size/SHA-256 驗證／原子移動 → complete ACK → 已送達。

單檔25 MiB，名單內5 MiB以下自動接收；其餘點下載；每則一個附件，無資料夾／續傳。附件快取512 MiB，不自動清理完成檔。基線 classroom-framing exit0。

驗證：附件真TCP／SQLite focused、classroom-all、共用回歸、Windows cross-build與候選publish。原生Windows、雙機和DPI必須另列。
