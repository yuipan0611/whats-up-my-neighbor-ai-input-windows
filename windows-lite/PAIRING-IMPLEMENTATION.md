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

## 雙方確認 actors（已實作）

`PairingIdentity` 借用資料庫、持有呼叫端交付的現有私鑰，只接受與當下 SQLite 身分／public metadata／revision 相符且可真實簽驗的 P256 key；不建立或儲存身分。兩 actor 停止並 drain 後才能 dispose 身分與資料庫。

`PairingResponder` 初始為 closed；只有本機明確開窗才接 request。每窗最多3次新 reveal、120秒（從本機開窗動作起算，含等待舊工作）、committed idle20秒，session不延長原視窗。結構已驗證的request透過原子candidate API擷取原始 allowed-state snapshot；Na到達後才顯示SAS及一次性本機ticket。只有實際驗證的 initiator角色簽章，加上本機同一ticket確認，才能最終CAS paired。既有paired的unchanged視為冪等成功，取消只清理原pending；撤銷、suspension、ABA、local身分改變與舊ticket均不能重新信任。

`PairingInitiator` 在收到commitment後才產生Na，驗揭露的公鑰／expected PeerId與commitment後才建立候選、顯示SAS。沒有本機ticket確認，即使有效responder簽章也不接受。使用者確認後只簽一次，poll至少5秒重送相同位元組，不補做新session；只有驗到同一TH／SID／responder角色的簽章，才CAS本機paired。明確新begin可附前一session的best-effort `superseded` cancel供transport先送。取消、逾時或儲存結果不確定不會斷言遠端未配對；paired也只代表本機已完成，不是跨兩台資料庫的原子提交。

每actor同時最多1個physical protocol/storage工作；網路呼叫忙碌時立即拒絕，使用者確認與清理等待均有界，快照意圖在等SQL/state lock前即失效。完成／中止cache最多最近64筆、10分鐘，成功confirm只回原簽章副本，不寫DB；呼叫端改動回傳byte陣列不會污染cache。matching-session處理上限200、confirm最短1秒。身分檢查可能延遲；讀取完成後會以同一短lifecycle邊界重核取消，再發布ready／request。以內部fault hook在真SQLite查詢完成、結果返回前延遲，兩個negative control已先紅，恢復guard後兩端87項皆通過。最初以WAL寫鎖阻擋讀取的fixture假設不成立，該批紀錄保留但不作取消證據；已移除觀察counter，沒有把它當成安全權威。native host仍必須把關窗、resume、rebind與trust事件傳入invalidate，並在dispose資料前drain。

87個新增檢查是本機兩份真SQLite、真P256、實際產品封包驅動兩actor；有雙向consent順序、真並行確認、SQL boundary阻塞時取消／新開窗、commit前後IO fault、錯role／key／session、有效遠端簽章不能取代本機動作、確認遺失／exact deadline／五秒poll與既有quota、cache／reveal／request上限、timer與資料保留。測試呼叫可信的本機確認入口，**不是人類實際比對或WPF驗收，也不是TCP傳輸驗收**。在網路admission之外被busy拒絕的呼叫不佔新的physical工作；transport另有pair類別1槽與2秒wire回覆，不能把actor serial鎖當成全部listener的隔離。

## 條件式 TCP 傳輸（已實作）

`IPairingEndpoint` 是 listener 借用的明確 opt-in endpoint；原建構式沒有 endpoint，`/nekochat/v2/pair` 仍回 unsupported_version，不會暗中啟用 P1。設定 endpoint 時，不需要先配置 ordinary-message receiver 或訊息 L，即可單獨開啟配對控制路由；它不代替使用者決定身分方案或建立私鑰。`DirectLanListener` 有 additive 四參數入口，舊建構式與 v1/v2 消費者保持原契約。

listener 在完整配對 request line 到達時先保留單一 class slot，含慢 headers／body 的 wire budget 最多2秒，body最多1KiB。尚無完整 request line 的連線沿用原一般 connection budget；所有路由仍共用原8條連線及20/source/min admission，沒有提高限制。wire逾時後會關閉 socket，但 detached protocol／SQLite job 真正完成前，class slot不釋出；第二個pair請求被拒絕，其他路由仍可使用。停止會先撤銷 endpoint 意圖、取消 connection token，再 drain 一般 workers、真正的pair job及其lease continuation，最後才讓host釋放借用的store/key。endpoint實作者必須在自己的最終trust transaction核對token。

`PairingLanClient` 借用 initiator，持有自己的 HTTP／session lifetime；只接受 canonical private IPv4 literal，拒絕DNS／publicIP／縮寫IPv4／IPv6，不走proxy、不follow redirect。connect最多2秒、每個request最多4秒；headers最多8KiB，回覆body最多1KiB（包括無Content-Length的chunked），只接受未編碼的UTF-8 JSON。非200必須exact error envelope及status/code對應，200仍須actor驗packet／commitment／SID／role／key。只在明確Begin送request、nonce；SAS尚未本機確認時不送confirm。人類ticket確認後，暫時斷線／pending／pair_unavailable暫忙等只會每5秒重送相同confirm bytes，絕不自動新建session或送私訊。一次physical outbound操作、busy不排隊；Cancel可立即撤銷generation與HTTP，然後drain及best-effort forward cancel。

route generation、caller cancellation、120秒lifetime及外部fast scope predicate共同約束actor。predicate亦進入candidate與final SQLite commit guard；它必須thread-safe、無I/O，不可在guard內重新取得資料庫鎖。取消／dispose的同步token callbacks不在client lifecycle鎖內執行；user cancel與background failure發佈採同一短邊界，避免背景取消蓋掉明確user-cancel原因。這是volatile scope守衛，**還不是名單row的持久revision fence**。

65項實際 TCP／P256／兩份SQLite checks，包括正常雙方consent後的真正signed hello/message/storage ACK、default receipt OFF、lost socket ACK的相同confirm五秒重試、wrong role/SID/key、假HTTP200與錯kind、揭露換鑰、expected-peer不符、重複JSON、oversize/chunked/header/charset/encoding/redirect/status錯配、配對尚關閉／pair-only／預設disabled、cancel與dispose競態、真SQL read completion中取消、不允許outbound排隊，以及真BeforeCommit卡住後wire先關、class仍佔用、v1仍可使用、stop等待storage／回滾。此批是可信local action API，不代表人類真的比較或WPF核准。

最後local COMMIT的scope test另做negative control：只暫時移除 initiator Live 的scope predicate，實際TCP收到有效R簽章後在BeforeCommit翻轉scope，測試精確失敗；還原guard後納入完整回歸。原始log與source hashes見 `build/wire-evidence/pairing-tcp-delivery.json`。來源未改schema、固定269／41vectors或核准素材，無新套件。

## 同時發起的角色協調（已實作）

可選的 `PairingCoordinator` 持有 initiator／responder／client，借用既有PairingIdentity與store。初始沒有使用者意圖、兩actor不接受新配對。明確Begin或Open建立一個有generation、scope／caller token和原始monotonic start的120秒意圖；新操作先排空舊工作。Begin／Open一個physical action、不排隊；incoming協調一個physical工作。cancel／invalidate／stop先讓scope失效，再排空工作。listener明確配置coordinator才會使用，正常App未配置它。

若兩個明確outbound意圖的request交叉，收到的來源須符合captured目標IP，公鑰導出的PeerId須符合expected／已揭露PeerId（未知對象時只作來源及公開claim的角色提示）。**依ASCII Ordinal byte順序，較小者保持I並回403，較大者放棄舊I、取消其HTTP並drain實際工作，轉為R。**這不是大小寫折疊／顯示名稱排序；公開claim只可能干擾角色，不能取得配對／老師權限。新操作還在等舊工作時不參與cross arbitration，避免前一個對象狀態滲入新意圖。

較大者沿用同一個原始120秒意圖來開R window，scope進入readiness、request candidate與最終transaction guard；角色轉換不增加期限，不自動產生新的outbound session。舊I取消只清自己unchanged pending，既有paired／revoked等不降級。其best-effort舊sid cancel使用client的唯一outbound slot，可以在背景等待最多4秒；R不等待這個通知回覆才接受新request。slot仍有owner並會在cancel／dispose drain，沒有unbounded fire-and-forget。新R的nonce／transcript／SAS／ticket是新session，舊I的ticket、確認旗標、cancel sid都不能沿用；兩邊仍須各自核對並確認。

較大者先收到較小者403、稍後交叉request才到時，也可沿用原明確意圖接收；在原期限內顯示awaiting_cross_request，不自動重送request。這不保證先前request已在對方尚未有任何本機意圖時被拒絕的兩次循序操作自動收斂，該情況仍需明確新動作。confirm階段的generic pair_unavailable可能只是R local-action／physical slot暫忙，現在與pending等同樣每5秒重送已核准的同一confirm，直到有效簽章、409、期限或取消；Begin的403依舊不自動重開session。

原66項checks加上準備中／失敗後的新意圖顯示守衛，共68項真TCP／P256／兩份SQLite檢查。合成私鑰只在RAM產生，特選PeerId '_'與小寫字首，構成Ordinal與case-folding順序相反的實際例子；以兩個受控real-HTTP request arrival gates驗reject-first／yield-first／同時放行／unknown expected的角色收斂、單一存活SAS、舊sid cancel、雙方ticket、配對後真signed message/storage ACK。另驗舊cancel回覆卡住仍能新R握手、cancel擁有並排空notification、119秒轉R只剩1秒／exact120拒確認、cancel/scope/dispose後不復活、無關key／logical-source注入不觸發轉換、舊SAS不能確認或拒絕新session，以及current mismatch rejection。

真並行確認測試以AfterRead hook在**真正GetPeerSecuritySnapshot完成後**暫停R local action，確保network confirm實際得到403；釋放後原confirm五秒retry完成，R PeerRevision恰加1。這抓到先前client把暫忙403當作terminal remote_ended的真問題，沒有降低assert或改成手動fixture重試。另有R final BeforeCommit翻scope／revocation、scope在ready read完成時失效。四個negative controls分別反轉角色比較、移除R final scope、移除403 retry、移除R ready scope，各在精確對應assert失敗；finally還原production原文再完整回歸。詳見 `build/wire-evidence/pairing-cross-delivery.json`。

## 崩潰／重啟及不確定結果（已驗證）

`PairingCrashChecks` 使用真正子程序、TCP、P256及SQLite，不以RegisterPeer直接設paired。I與R各在 `security-change` 的BeforeCommit／AfterCommit精確停點被強制終止，共4次kill及4次正常重啟。父程序在產品重新開啟資料庫**之前**以read-only SQLite讀取：before仍pending，after已paired，integrity_check正常，獨立草稿保留，沒有pairing製造的Message／ManualSendAttempt。after不代表兩台資料庫原子完成：R可能已paired而I未確認；I在其CAS前被kill時，R也可能早已paired。

重啟沿用同一測試身分／public metadata／revision，只保留已提交的local trust；coordinator仍closed／RoleNone、沒有SID／SAS／consent，舊confirm重播409、新request在未明確開窗時403，不能自動恢復人類核對。before的實際SEND因peer-not-paired被拒；Rafter但另一邊unpaired的實際signed SEND也被拒且保留draft。原Iafter、兩端都已提交時可正常signed storage ACK。需要修復的case必須明確重新開窗／新Begin／新SID與SAS／兩次新確認，之後實際訊息收發、ACK與清草稿才成功。正常App未因本測試啟用P1或provision。

最終92個checks也驗證restart不會繞過仍活著的R來源2秒新request間隔：先在父端注入clock的原時間實際收403，再明確新動作、advance該monotonic clock2秒後才成功；沒有清掉或放寬production limiter，也沒有自動重開session。其他socket deadline仍真實wall-clock。最初fixture在正常wall-clock下太快嘗試repair而被原anti-grinding規則正確拒絕，已修fixture並追加上述真TCP反例，原失敗log保留。

合成私鑰只在RAM產生，子程序透過匿名stdin收取，不放argv／env／檔案／stdout；DER暫存array清零。stdout只有bounded public狀態、代碼及boundary，stderr應為空；檔案掃描確認兩邊fixture都沒有raw PKCS8或其base64。測試只能證明這個隔離fixture的檔案與輸出，沒有冒稱Windows DPAPI／CNG／實際使用者profile／整機記憶體驗證。

兩類新的**產品問題**有三個原碼失敗反例，修正後納入回歸：(1) I確認後結果未明，原coordinator關閉會抹掉MayBeAsymmetric；現在terminal保留不確定結果，轉角色亦保留已發confirm的不確定性，不能把本機close說成遠端已取消。(2) 同actor的新意圖還在真identity-read completion等待時，原View可露出舊SID／SAS／paired狀態；現在每個intent有自己的DisplayReady，準備期間不公開舊session／ticket，過期／scope失效會顯示ended。新意圖因durable identity版本變更而啟動失敗時，terminal亦不複製先前actor的SID／SAS／結果，只回本輪失敗代碼與captured expected hint。readiness發佈與角色切換共用短lifecycle邊界，沒有把SQL放進該鎖。第二項測試在真SQL讀完、回傳前阻塞，不使用WAL writer可擋reader的錯誤假設。

## 實際換鑰中間人案例（已驗證）

11項check使用A、B及惡意程式控制的兩個合成身分M-R／M-I，共四個真TCP／P256／SQLite節點；M向A用自己的R key，向B用另一個I key。兩條各自合法的commit／reveal都成功，A與B（未知expected身份的首次配對）各顯示對應M key的SAS，沒有自動把公鑰當成已配對。即使M在兩腿按自己的確認並送有效I簽章，A／B仍需自己比較。把另一腿的confirm直接搬過來因SID不符409，改成當前SID仍因key／transcript不符403。

當A與B看到不同SAS並明確reject，兩腿都回unpaired，A/B彼此亦未建立Peer，沒有Message或send attempt。使用的是可信local rejection API，**不是人類實際比較／Windows畫面驗收**。六位SAS可能碰撞；fixture最多3個獨立場景以取得一個不同SAS實例，沒有把該案例當成碰撞率估計、窮舉安全證明或保證所有換鑰都被密碼學自動拒絕。公式固定向量及風險範圍仍見前述章節。

本批原始baseline failures、process exit／命令／source及log hash見 `build/wire-evidence/pairing-crash-mitm-delivery.json`。首次proxy fixture沒有把被kill的socket EOF轉成沒有ACK，dispose時掩蓋了原本assert；已修為真斷線且無response，不是假200，該log保留但不當作產品因果證據。真正三個產品baseline分別精確在close uncertainty／preparing-intent display／failed-intent display assert失敗，修正後通過。

## 選定名單的配對與綁定交易（已實作）

`CapturePairingRosterTarget(rowId)` 以單一SELECT擷取選定列全部顯示／來源欄位、binding／endpoint durable revision、local identity/revision，建立opaque immutable、限本store runtime的target。它不是consent或權限。列的姓名／學號／老師標籤保留原值供本機票券與UI顯示，`ToString`不帶個資，不能把target／整份view序列化進wire或diagnostics。文字欄位以當前完整值比對；未新增文字變更歷史。binding／endpoint及delete-recreate ABA由既有RosterSecurityRevision拒絕，local／peer ABA沿用既有版本守衛；無schema變更。

`BeginRosterPairingCandidateIfCurrent` 在原candidate交易首尾核對同一target，不建立名單列、不改peerId。`CompleteRosterPairingIfCurrent` 是trusted local consent flow的最後一步：同一immediate transaction核對原peer/local、原選定列及其版本，才將Peer改paired並綁定該RosterEntry。最後guard使用**由原snapshot加上本次唯一授權變動推導**的peer／row新revision及新binding，不把寫完後重新讀到的row當作新基準。取消、IO、版本耗盡、意外DB mutation都不能留下半個trust／binding；already paired/bound可guarded unchanged成功。兩個DB仍各自提交，不保證跨機原子。

`PairingIdentity`／I／R／client／coordinator一直攜帶同一immutable target；one-use consent及view保留當時選取的列，即使cross-role switch也不刷新成最新名單。fast lifecycle predicate仍無SQL，durable檢查在store transaction或background readiness／expiry完成。原unscoped APIs保持契約，正常App未配置P1。新增 `BeginRosterAsync`／`OpenRosterWindowAsync` 是明確「配對並綁定這一列」入口，host仍要把這個含義與原姓名／學號顯示給使用者後才能使用確認票券。

已有不同Peer的列不自動換綁；要先明確解除綁定並重新選取。宣告IP存在時，outgoing target／incoming source必須符合captured IP。無IP列可用明確輸入的private target或listen/SAS流程綁定，但本交易不偷偷補寫IP／lastSeen／安裝證據；後續路由須由原本明確名單編輯或新簽章活動建立。CSV／探測／名單老師標籤永不供應配對同意或老師權限。

57項選定列checks包含真正SQLite與TCP：candidate不綁定、最後peer+row同commit、idempotent不增加revision，姓名／學號／teacher／IP改動、route／binding ABA、delete-recreate／local／peer ABA與revocation，foreign runtime／同DB重開target拒用、unrelated rows可變但不受綁定、不同既有Peer拒換綁、取消與COMMIT前後IO、revision耗盡、兩SQLite handle恰1成功。live DB trigger作受控fault注入，驗候選回滾pin/conversation/意外row變更、最後post-state不能採用意外新姓名或額外peer revision；不是在宣稱本機攻陷防護，也沒有允許新trigger進schema。

真TCP包含兩側選定列的SAS票券、同時發起轉角色仍帶各自原列、post-SAS改列／route ABA不能綁定、成功後真正signed message/storage ACK、IP不符拒絕。wire JSON解碼後檢查兩端本機姓名均未出現，處理escaped JSON，未以UTF8原字串搜尋充當完整個資驗證。三個negative controls分別拿掉final row guard、改成採用post-write最新row、拿掉candidate row guard，各exit134且精確在相對應assert失敗；finally還原source後納入完整回歸。

崩潰suite由原92增為195：共8次真kill／8次正常restart，其中新增4個scoped I/R BeforeCommit／AfterCommit case直接在產品重開前讀Peer與RosterEntry，證明pending+unbound或paired+bound始終同一COMMIT。重啟不補成缺失的一半，明確fresh repair後兩邊selected row都綁定正確簽章對象。fixture私鑰仍僅stdin與RAM，姓名不用於wire。完整hash／命令／controls見 `build/wire-evidence/pairing-roster-delivery.json`。

## Host／設定／SAS 視窗來源整合

`PairingSessionHost` 為 runtime 的單一配對視窗 owner。每次開啟先令舊意圖失效再等待 physical work；新視窗在明確發起／等候前不繼承舊 SAS 或 terminal state。原 roster target、候選金鑰及 one-use consent 沿用同一份。關閉／owner 隱藏／runtime 停止、睡眠恢復與名單／信任操作都先作 RAM 失效，再等待 Core／SQL 結束；當下 callback 不在 dispatcher 作 DB I/O。接收與配對排空後才 dispose 借用的金鑰與 store。

`ChatRuntime` 新增獨立於 L 的 explicit `ChatPairingConfiguration`。正常啟動沒有傳入，不自動 provision 金鑰、不選 P1、不啟用配對。只有明確隔離的 Windows fixture 用新 DPAPI profile 啟用 P1；私訊和 L 仍停用。listener 直接借用 host endpoint。配對重選、resume、trust 操作亦推進 outgoing message generation，舊傳送不能用新信任基準完成。

設定頁提供核對／綁定、解除名單綁定、撤銷身分、允許重新核對。`PairingWindow` 顯示開啟時的姓名／學號／本機老師標籤／既有綁定與 IP，明確說明本輪會綁定哪一筆名單；已宣告 IP 不可在視窗內暗改。未宣告 IP 只供當輪顯式輸入，不回填名單。六位 SAS 與時限由 Core RAM projection 呈現，只有 stage、目前 ticket、視窗焦點及未勾選的人工比較 checkbox 全符合時，確認按鈕才可用。沒有 default confirm button；deactivation／ticket 更替清除勾選。短高 body 捲動、固定 close footer；工作區用既有 FitChat 調整。拒絕顯示「本輪已中止」及明確重試指示；可能不對稱的結果優先保留。完成只稱本機完成，對方結果仍以對方畫面為準。

選定列 snapshot 現在同一 SELECT 也凍結原本 bound peer 的 key/state/revision；在 candidate 前被 revoke→paired 改回原值也不能採用新的信任基準。`ChangeSelectedPeerIfCurrent` 在交易首尾核 original row/local/peer epoch 和 live predicate，只能 unlink、revoked、suspended、unpaired，不能直接授予 paired。unlink 保留 peer trust／對話；trust action 只清除該 peer 的未送回條，保留已讀與全域 opt-in。缺金鑰舊綁定須先明確解除綁定。

Mac 的真 TCP／SQLite host checks 含 close、resume、supersede、reject、雙方顯式確認、I/R COMMIT 前被關閉回滾、管理操作與 ABA／trigger／receipt purge。瀏覽器排版 artifact 為 `preview/pairing-dialog.html`，只有合成狀態、無網路與配對；其字型、尺寸與控制項不能作 WPF 還原證據。獨立 finish review 的 rejected-outcome 文案 finding 已補 source regression；Windows native disposition 保持 fix/unexecuted。

## 後續完成條件

Host／設定／SAS 視窗的來源與 cross-build 已完成，不等於 Windows／人類使用驗收。原生檢查入口與操作單見 [PAIRING-VERIFICATION.md](PAIRING-VERIFICATION.md)。仍須在 Windows 驗證 WPF 焦點／鍵盤／輔助工具、DPAPI／CNG／ACL、DPI／工作區、真睡眠恢復／OS／雙實機與教室。它們目前一律未執行。

正常產品啟用仍須使用者的身分方案與訊息上限決定，不以 fixture 或 preview 代答。本機三種交叉包裝及核對結果見 [DELIVERY.md](DELIVERY.md)，實際來源／hash 以同批 manifest 為準；Windows 安裝／執行仍未驗收。教材依使用者指示暫緩。原 N002P-v2 的設計審查缺陷記錄保留；新增工程證據不回寫成原提案已通過。

## 密碼學範圍

這是自訂 SHA-256 commitment／transcript／十進位短碼變體，沒有第三方密碼審查或形式化證明。重新查閱的 [RFC6189 §4.4.1.1](https://www.rfc-editor.org/rfc/rfc6189.html#section-4.4.1.1) 與 [Bluetooth Core5.4 Security Manager §2.3.5.6.2](https://www.bluetooth.com/wp-content/uploads/Files/Specification/HTML/Core-54/out/en/host/security-manager-specification.html) 只支持參考的 commitment／numeric-comparison 模式；Bluetooth 的 g2 採 AES-CMAC，不能把本案宣稱成同一構造或自動繼承其保證。六位數風險與「使用者兩邊確實比較」的前提維持原提案的有限論證，沒有把縮小模擬當作完整產品的攻擊成功率證明。
