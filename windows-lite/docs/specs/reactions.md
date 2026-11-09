# 課堂反應貓咪圖（D-32；第 1 批）
現場教室、雙實機、多螢幕／投影與實體 DPI 驗收仍未完成。原生 runner 測試是隔離合成資料證據。

| 編號 | Given / When / Then |
|---|---|
| RX-01 | Given 新安裝且未操作圖片開關，When 收到符合既有角色限制的課堂反應，Then 預設呼叫 NotifyReaction 並顯示內嵌貓咪圖。 |
| RX-02 | Given 設定頁，When 使用者查看「課堂反應」，Then 可見「收到課堂反應時跳出貓咪圖（投影時可關閉）」及「提醒螢幕」；關閉後只顯示原本的姓名＋新訊息文字提醒，不顯示反應正文或圖片。老師 IP／記住老師維持隱藏，老師由聯絡人選單設定。 |
| RX-03 | Given 舊設定缺少 ReactionPreviewExplicitChoice，或其值為 false，When 載入，Then ShowReactionPreviews 不論原值為 missing／false／true，一律正規化為 true；舊開關隱藏期間的 false 視為從未選擇。載入本身不重寫原檔；下次正常成功儲存會保存正規化值，儲存失敗依現有提示處理，下次仍可重試。其餘設定不動。 |
| RX-04 | Given 本版可見開關，When 使用者開／關，Then 同時儲存 ReactionPreviewExplicitChoice=true 及所選值。此後載入、一般儲存與升級皆尊重 explicit OFF，不再打開。程式載入或設定螢幕不視為使用者圖片選擇；無效／不可讀設定保持原有唯讀保護，不覆寫原檔。 |
| RX-05 | Given 反應圖片開啟，When 收到四款 TeacherOnly 反應，Then 僅老師端跳圖；學生端沿用文字提醒。「你再給我滑！」保持可給同學。既有 2 秒顯示、相同反應合併計數、不延長截止時間、螢幕選擇、視窗不搶焦點及結束復原不變；未知反應仍是普通文字。 |
| RX-06 | Given 本機封鎖來源 IP，When 收到任何課堂反應（legacy／classroom），Then D-30 的正常 ACK 保留，但不存正文、不建 auto 聯絡人、不發事件、不跳圖、不發文字提醒。儲存層同交易判斷＋通知前 Worker 封鎖複查維持不變；無法判斷時 fail-closed。封鎖不是防偽，換 IP 可繞過，沿用 CM-06。 |

驗證：Console `--reactions` 含 RX policy、實際 settings.json 遷移與五款反應在兩個接收路徑的封鎖負控；既有 ReactionChecks 繼續釘選角色／2 秒／合併／真 TCP。
原生 `--contacts-ui-smoke-test` 內另建 fresh reaction profile，使用真 loopback 接收及真 WPF PetWindow，擷取預設貓咪圖／可見開關／關閉文字提醒／同學貼圖／封鎖無提醒；既有雙程序 messaging smoke 另證明未預先 opt-in 的老師圖片及 explicit OFF。macOS 交叉建置不等於已執行上述原生斷言。
