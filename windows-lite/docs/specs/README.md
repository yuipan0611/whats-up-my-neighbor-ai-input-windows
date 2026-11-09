# NekoChat Lite 行為規格（BDD）

每條規格以 `Given／When／Then` 描述使用者看得到的行為，編號形式為 `<主題>-<序號>`（例如 `CT-03`）。

- 每條規格都必須對應 NekoChat.Tests 中一個同名 scenario（沿用 `ClassroomKillChecks.cs` 的 `Scenario(Seed/Act/Before/After)` 形式，或 loopback 多節點 fixture）。scenario 名稱以規格編號開頭，方便追查。
- 規格是產品承諾，測試是證據；修改行為時先改規格、再改測試、最後改程式。
- `【WPF】` 標示只能由原生 WPF 測試或人工操作驗證的規格；在原生 CI 未覆蓋前，標「未執行」。
- `【現場】` 標示需要兩台實體 Windows／教室網路才能驗證，一律標「未執行」，不得以 loopback 代替。

| 檔案 | 主題 | 批次 | 決策 |
|---|---|---|---|
| [contacts.md](contacts.md) | 聯絡人：自動加入、合併、新增、移除、備註 | 第 1 批 | D-27、D-25（本機備註部分） |
| [teacher.md](teacher.md) | 老師：單一置頂、設為／更換／移除 | 第 1 批 | D-24 |
| [message-deletion.md](message-deletion.md) | 訊息：本機刪除入口與停用原因 | 第 1 批 | D-26（本機刪除部分） |
| [read-receipts.md](read-receipts.md) | 已讀：預設開啟、回線補送 | 第 1 批 | D-28 |

第 2 批（profile.v1、presence.v1、recall.v1）的規格在第 1 批完成後另寫，協定規格先經 Ben 審查。
