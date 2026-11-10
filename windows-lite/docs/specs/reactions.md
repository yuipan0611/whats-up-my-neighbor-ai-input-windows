# 課堂反應貓咪圖（D-32；第 1 批）
現場教室、雙實機、多螢幕／投影與實體 DPI 驗收仍未完成。原生 runner 測試是隔離合成資料證據。

| 編號 | Given / When / Then |
|---|---|
| RX-01 | Given 新安裝且未操作圖片開關，When 收到符合既有角色限制的課堂反應，Then 預設呼叫 NotifyReaction 並顯示內嵌貓咪圖。 |
| RX-02 | **D-37 修訂**：Given 設定頁，Then 不顯示「課堂反應」區（無老師電腦勾選、無跳圖開關、無「提醒螢幕」）；貓咪圖固定跟隨貓咪所在螢幕（WHY NOT 見 CODEX-VERIFICATION D-37）。舊設定若 explicit OFF，老師電腦只顯示原本的姓名＋新訊息文字提醒，不顯示反應正文或圖片；非老師的 TeacherOnly 依 RX-08 完全不提醒。老師 IP／記住老師維持隱藏，老師由聯絡人選單設定。 |
| RX-03 | Given 舊設定缺少 ReactionPreviewExplicitChoice，或其值為 false，When 載入，Then ShowReactionPreviews 不論原值為 missing／false／true，一律正規化為 true；舊開關隱藏期間的 false 視為從未選擇。載入本身不重寫原檔；下次正常成功儲存會保存正規化值，儲存失敗依現有提示處理，下次仍可重試。其餘設定不動。 |
| RX-04 | **D-37 修訂：設定頁已無可見開關**；Core 策略不變，舊設定檔的 ReactionPreviewExplicitChoice=true 及所選值照舊儲存。此後載入、一般儲存與升級皆尊重 explicit OFF，不再打開。程式載入或設定螢幕不視為使用者圖片選擇；無效／不可讀設定保持原有唯讀保護，不覆寫原檔。 |
| RX-05 | Given 反應圖片開啟，When 收到四款 TeacherOnly 反應，Then 僅老師端跳圖；學生端不跳圖也不跳文字提醒（D-33／RX-08）。「你再給我滑！」保持可給同學。既有 2 秒顯示、相同反應合併計數、不延長截止時間、螢幕選擇、視窗不搶焦點及結束復原不變；未知反應仍是普通文字。 |
| RX-06 | Given 本機封鎖來源 IP，When 收到任何課堂反應（legacy／classroom），Then D-30 的正常 ACK 保留，但不存正文、不建 auto 聯絡人、不發事件、不跳圖、不發文字提醒。儲存層同交易判斷＋通知前 Worker 封鎖複查維持不變；無法判斷時 fail-closed。封鎖不是防偽，換 IP 可繞過，沿用 CM-06。 |

驗證：Console `--reactions` 含 RX policy、實際 settings.json 遷移與五款反應在兩個接收路徑的封鎖負控；既有 ReactionChecks 繼續釘選角色／2 秒／合併／真 TCP。
原生 `--contacts-ui-smoke-test` 內另建 fresh reaction profile，使用真 loopback 接收及真 WPF PetWindow，擷取預設貓咪圖／可見開關／關閉文字提醒／同學貼圖／封鎖無提醒；既有雙程序 messaging smoke 另證明未預先 opt-in 的老師圖片及 explicit OFF。macOS 交叉建置不等於已執行上述原生斷言。

**RX-07 老師電腦辨識與升級（D-33）**
- Given Settings.ThisDeviceIsTeacher 是 nullable，舊設定為 null；名單老師（SQLite 與 TeacherIp 投影）與本機角色分開。
- When 每次 AttachRuntime 啟動，在 TC-03 投影覆寫 TeacherIp **之前**，先以本機所有 IP＋ClassroomFiles.IsTeacherNode 解決本機角色。首次 null：舊 TeacherIp 是本機任一 IP、teacher node 或本機包含 DefaultTeacherIp（192.168.24.240）則 true，否則 false。
- Then 將解決結果儲存，不會因為名單沒有自己、投影變成空字串而失去老師角色。未明確選擇時，每次啟動重新檢查 DefaultTeacherIp／teacher node；即使上次為 false 也會提升為 true。已遷移的 true 在換網卡／DHCP 後保留；普通學生 IP 不匹配且沒有其他依據時為 false。
- **D-37 修訂：設定頁已移除「這台是老師的電腦」勾選，僅自動辨識**；舊設定檔中的 ThisDeviceTeacherExplicitChoice=true 仍被尊重。明確 true／false 永遠優先，**包含優先於 teacher-node 提示**，明確 false 在固定 .240 上重開仍為 false。這是本機顯示角色，不授予課堂檔案撤下或網路權限。
- IsReactionTeacher 不再讀 TeacherIp 或給 127.0.0.1 特例。原生測試只在精確隔離 profile/runtime 的既有 hook 注入本機 IP 清單，不修改網卡，也不讓一般 profile 使用該 hook。遷移儲存失敗沿用設定保存失敗提示、保留記憶體結果並可於下次啟動重試，不覆寫無效原檔。

**RX-08 非老師不提醒、保留標示紀錄（D-33）**
- Given 非老師本機，When 收到帶已知 TeacherOnly reaction id 的前四款，Then 不呼叫 NotifyReaction 或 NotifyChat（圖片開關不影響此條）；正文、reaction id、歷史與本機未讀仍依原接收規則保存。
- 該來訊在聊天紀錄用 11pt 小字顯示「課堂反應：講太快了（只有老師電腦會跳出貓咪圖）」等對應名稱，取代一般正文顯示；RawText 不改寫，複製與本機刪除照舊。切換本機角色時重新投影標示。只影響 incoming，不標示 outgoing。
- Given 手動打字「講太快了」但沒有 reaction id，Then 是一般文字通知與一般歷史，不反推 reaction id；legacy 和 classroom 都保留實際 wire metadata。未知 id 照一般文字。「你再給我滑！」不受影響。D-30 封鎖優先，仍不儲存、不發任何提醒。
