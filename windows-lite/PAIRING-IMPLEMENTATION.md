# P1 條件式實作與設計審查

本分支尚未替使用者選定 P1／P2／P3 或產品訊息上限。以下是「若採 P1」的工程實作，不能當作已啟用配對、人類完成核對或完整 N002B 通過。

## N002P-v2 審查

來源是獨立文件 `docs/team/design/N002P-pairing-v2.md` 與 `N002P-measure-v2/pair_model.py`；保持原件不動。Codex 在本機隔離副本重新執行，reference state model24/24、Python/OpenSSL公式與 wire41/41 皆通過。但以下獨立反例說明 v2 仍不能直接照寫並宣告通過：

1. **候選建立的撤銷競態。**`on_request` 先查 state，再 register、擷取 snapshot。若中間改成 revoked，snapshot 會把 revoked 當成配對基準，最後 CAS 反而可成功改為 paired。獨立模型探針得到 `commit → reveal → pending → paired`，最後是 paired；預期應保留 revoked。必須把「原始狀態仍允許」與候選建立／pin／snapshot 放進同一交易，比對開場前擷取的 security revision，不能只在 register 之後換成最新 snapshot。
2. **取消會降級既有信任。**v2 對既有 paired Peer 的 register 保留原狀，但 abort 一律條件寫 unpaired。公開公鑰可被複製到未驗證 request；視窗開啟期間，`request → cancel` 即可把原有 paired 降成 unpaired。模型獨立探針已重現。取消／失敗只清理自己留下的 pending，既有 paired 必須保留；當中的 revocation／suspension 或其他 revision 變更亦不可覆寫。
3. **參考模型與產品 CAS 的冪等結果不同。**產品 `SetPairingStateIfCurrent` 對已是 paired 的相同 snapshot 回 `Changed=false, Reason=unchanged`，模型則仍計為 changed。已可信的同 key 重配對，必須在雙方都核對且最終 guard 有效時，把這個 unchanged 視為冪等完成；不能視為失敗後再把它降成 unpaired。
4. **輪詢與現有 admission quota。**listener 目前在 accept 階段以來源 IP 限20次／分鐘。若採每秒一次 confirm，兩個開場請求後很快耗盡，不能保證等待完整120秒。條件式 client 將採每5秒一次（仍符合提案的至少1秒間隔），不調高現有 listener quota，也不自動重開 session。其他流量仍可能造成限速；UI 必須顯示結果未確認，不能聲稱未配對。

證據位於 worktree 的 `build/pair-review-v2/`：原模型副本、原向量副本、reference logs 與 `independent-probes.json`。前兩項是阻擋；第三、四項必須在產品 actor/client 中收斂。原參考模型綠燈不等於整體提案已通過，也不作為人類確認證據。

## 目前產品程式

- `PairingCrypto` 按凍結公式實作公鑰與 nonce 綁定的 commitment、transcript、六位 SAS、分角色確認簽章輸入與 P256/P1363 驗證；commitment 以固定長度常數時間比較。輸入角色、長度與 named P-256 canonical SPKI 有檢查。
- `PairingWire` 只接受最多1KiB、exact key set、整數字面值2、正規16-byte base64url與32-byte commitment、正規 SPKI／64-byte signature。沿用嚴格 JSON reader 拒絕重複鍵、非法 UTF-8／surrogate、深度與多餘資料。解析結果不可變，但**不是簽章／人類同意／信任證明**。
- `BeginPairingCandidateIfCurrent` 在 immediate SQLite transaction 比對原始 local／peer revision與狀態，拒絕 revoked／suspended／未知狀態、自己與缺失／不符的 local public metadata，再建立或 first-pin 候選並擷取 snapshot。取消與儲存失敗會整體回滾。候選未知 Peer 是 pending；已存在的配對狀態保持原樣，永不在這個 API 升為 paired。
- 固定41向量被複製到測試 fixtures，只含公用測試資料；另有嚴格 JSON、原文邊界與真 P256 簽章、SQLite ABA／撤銷／取消／兩 handle 競爭等測試。沒有新套件或 schema 變更。

## 雙方確認 actors（已實作，未接 TCP）

`PairingIdentity` 借用資料庫、持有呼叫端交付的現有私鑰，只接受與當下 SQLite 身分／public metadata／revision 相符且可真實簽驗的 P256 key；不建立或儲存身分。兩 actor 停止並 drain 後才能 dispose 身分與資料庫。

`PairingResponder` 初始為 closed；只有本機明確開窗才接 request。每窗最多3次新 reveal、120秒（從本機開窗動作起算，含等待舊工作）、committed idle20秒，session不延長原視窗。結構已驗證的request透過原子candidate API擷取原始 allowed-state snapshot；Na到達後才顯示SAS及一次性本機ticket。只有實際驗證的 initiator角色簽章，加上本機同一ticket確認，才能最終CAS paired。既有paired的unchanged視為冪等成功，取消只清理原pending；撤銷、suspension、ABA、local身分改變與舊ticket均不能重新信任。

`PairingInitiator` 在收到commitment後才產生Na，驗揭露的公鑰／expected PeerId與commitment後才建立候選、顯示SAS。沒有本機ticket確認，即使有效responder簽章也不接受。使用者確認後只簽一次，poll至少5秒重送相同位元組，不補做新session；只有驗到同一TH／SID／responder角色的簽章，才CAS本機paired。明確新begin可附前一session的best-effort `superseded` cancel供transport先送。取消、逾時或儲存結果不確定不會斷言遠端未配對；paired也只代表本機已完成，不是跨兩台資料庫的原子提交。

每actor同時最多1個physical protocol/storage工作；網路呼叫忙碌時立即拒絕，使用者確認與清理等待均有界，快照意圖在等SQL/state lock前即失效。完成／中止cache最多最近64筆、10分鐘，成功confirm只回原簽章副本，不寫DB；呼叫端改動回傳byte陣列不會污染cache。matching-session處理上限200、confirm最短1秒。身分檢查可能延遲；讀取完成後會以同一短lifecycle邊界重核取消，再發布ready／request。以內部fault hook在真SQLite查詢完成、結果返回前延遲，兩個negative control已先紅，恢復guard後兩端87項皆通過。最初以WAL寫鎖阻擋讀取的fixture假設不成立，該批紀錄保留但不作取消證據；已移除觀察counter，沒有把它當成安全權威。native host仍必須把關窗、resume、rebind與trust事件傳入invalidate，並在dispose資料前drain。

87個新增檢查是本機兩份真SQLite、真P256、實際產品封包驅動兩actor；有雙向consent順序、真並行確認、SQL boundary阻塞時取消／新開窗、commit前後IO fault、錯role／key／session、有效遠端簽章不能取代本機動作、確認遺失／exact deadline／五秒poll與既有quota、cache／reveal／request上限、timer與資料保留。測試呼叫可信的本機確認入口，**不是人類實際比對或WPF驗收，也不是TCP傳輸驗收**。在網路admission之外被busy拒絕的呼叫不佔新的physical工作；transport仍須限制pair類別1槽與2秒wire回覆，不能把actor serial鎖當成全部listener的隔離。

## 後續完成條件

仍須完成同時發起的角色協調、one-pair physical job與listener類別隔離、2秒 wire deadline下的 storage job drain、同埠 HTTP route/client、actor五秒poll的真正網路排程，以及host／設定與實際窗口接線。actor內部guard／cache／consent已完成，但host還沒有傳入resume／rebind等事件，不能宣稱整體已接妥。若host把session綁到一筆名單，還須把captured roster revision納入最後交易的scope守衛與綁定，不能只依靠UI事件或把Row重新讀成最新值；目前actors只承諾local／peer revision的原子守衛。

必須以真正的 TCP、P256、SQLite 重做並行雙確認、撤銷／ABA、ACK 遺失、跨身分、換鑰中間人、壞承諾／簽章、重播、deadline／slot及 COMMIT 前後 kill 測試。只有以上全部有證據，才能稱為條件式 P1 完成；正常產品啟用仍須使用者的身分方案與訊息上限決定。Windows CNG／DPAPI／WPF／可用性與雙實機教室另列未執行。

## 密碼學範圍

這是自訂 SHA-256 commitment／transcript／十進位短碼變體，沒有第三方密碼審查或形式化證明。重新查閱的 [RFC6189 §4.4.1.1](https://www.rfc-editor.org/rfc/rfc6189.html#section-4.4.1.1) 與 [Bluetooth Core5.4 Security Manager §2.3.5.6.2](https://www.bluetooth.com/wp-content/uploads/Files/Specification/HTML/Core-54/out/en/host/security-manager-specification.html) 只支持參考的 commitment／numeric-comparison 模式；Bluetooth 的 g2 採 AES-CMAC，不能把本案宣稱成同一構造或自動繼承其保證。六位數風險與「使用者兩邊確實比較」的前提維持原提案的有限論證，沒有把縮小模擬當作完整產品的攻擊成功率證明。
