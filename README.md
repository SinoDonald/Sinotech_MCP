# Sinotech_MCP

讓 Claude 等 AI 助手透過 Model Context Protocol (MCP) 操作 Autodesk Revit（Revit 2020–2026）。

```
MCP Client ⇄(stdio)⇄ server/ (TypeScript) ⇄(TCP 127.0.0.1＋權杖)⇄ src/Sinotech.Revit.Addin → src/Sinotech.Revit.Commands → Revit API
```

| 目錄 | 說明 |
| --- | --- |
| `server/` | MCP Server（Node.js 20+），將工具呼叫轉為 JSON-RPC，經本機 TCP 送到 Revit |
| `Sinotech.MCP.sln` | Visual Studio 解決方案（Add-in、Commands、Setup） |
| `src/Sinotech.Revit.Addin/` | Revit Add-in（`Sinotech_MCP.addin`），功能區「中興MCP」含 AI助手、開關與設定 |
| `src/Sinotech.Revit.Commands/` | 實際執行 Revit API 的命令集，`command.json` 為命令清單 |
| `src/Sinotech.Revit.Extensions/` | **中興自行開發命令集**（依 CAD 自動建柱、樑、板、牆），Copyright © 2026 Donald |
| `Sinotech_MCP_Setup/` | Revit 2024–2026 MSI 安裝檔產生流程，見 [Setup 說明](Sinotech_MCP_Setup/README.md) |

## 建置與安裝

```powershell
.\scripts\build.ps1 -Revit 2026 -Install   # 建置並安裝到 %AppData%\Autodesk\Revit\Addins\2026
cd server; npm install; npm run build        # 建置 MCP Server
.\Sinotech_MCP_Setup\Build-Msi.ps1             # 產生 Revit 2024–2026 MSI 安裝檔
```

啟動 Revit 會自動載入所有已安裝的命令。功能區「中興MCP」有三個按鈕：

| 按鈕 | 用途 |
| --- | --- |
| AI助手 | 在 Revit 內與 Codex／Claude／Gemini 對話。面板開啟時啟動 AI助手專用的連線，關閉面板即停止 |
| 外部AI服務 | 開關供外部 Codex、Claude Code、Claude Desktop 使用的連線，與 AI助手互相獨立；每次啟動 Revit 預設「關」，右下角圓點綠＝開、灰＝關 |
| 外部設定 | 一鍵把 Sinotech MCP 登錄到本機的 Codex（`~/.codex/config.toml`）、Claude Code（`~/.claude.json`）、Claude Desktop（`claude_desktop_config.json`），也可移除或測試連線 |

### 連線埠號與權杖（1.3.0 起）

兩種連線各自獨立、只接受本機（127.0.0.1）連線，且每個要求都必須帶正確的權杖：

| 連線 | 埠號 | 權杖與設定檔 |
| --- | --- | --- |
| AI助手 | 每次啟動 Revit 由 Windows 指定空閒埠號 | 每次啟動 Revit 重新產生，寫在 `%LOCALAPPDATA%\Sinotech\McpInternal\revit-<PID>.json`，Revit 關閉時刪除；只交給 AI助手自己的背景程式 |
| 外部AI服務 | **20800＋年份**：2024＝20824、2025＝20825、2026＝20826 | 保存在 `%LOCALAPPDATA%\Sinotech\McpExternal\<年份>\external.json`（只屬於目前 Windows 帳號）；可修改其中的 `port` |

- 2024、2025、2026 可同時開啟，各自使用 AI助手或外部AI服務都不會衝突；同一年份開兩個 Revit 時，只有一個能開啟外部AI服務。
- 外部AI服務為「關」時，外部 AI 一定連不進來；關閉 AI助手不影響外部AI服務，反之亦然。
- 避開 TYBIM_MCP 使用的 17824～17826、21824～21826。關閉 Revit 時，所有埠號由 Windows 自動收回。
- 1.2.x 以前共用 8080、沒有權杖；升級後請在「外部設定」重新按一次「套用設定」，舊的 `sinotech_revit` 項目會自動移除。

### 使用外部 Claude、Codex

1. 先完全結束要設定的 AI 程式（Claude Desktop 也要從系統匣結束）。
2. Revit 中按「外部AI服務」使其顯示「服務：開」；第一次使用且尚未設定任何 AI 程式時，會自動開啟「外部設定」。
3. 在「外部設定」勾選 Codex／Claude Code／Claude Desktop，按「套用設定」。每個 Revit 年份各一個項目（例如 `sinotech_revit_2025`），其他設定保留，原檔先備份為 `*.before-sinotech-<時間>.bak`（每個設定檔只保留最近一次的備份）。要同時使用多個年份，請在各年份的 Revit 各套用一次。
4. 按「測試 Revit 連線」，再重新啟動 AI 程式：Codex 在「設定 → MCP」、Claude Code 輸入 `/mcp`、Claude Desktop 在「Settings → Developer」確認 `sinotech_revit_<年份>`。
5. 先輸入唯讀測試：「請用 sinotech_revit_2025 讀取目前 Revit 視圖資訊，先不要修改模型。」

設定內容是安裝目錄內附的 `runtime\node\node.exe`、`runtime\build\index.js`，以及環境變數 `SINOTECH_MCP_CONFIG`（指向上表的 `external.json`）；AI 用戶端設定檔不含權杖、API Key 或登入資料。AI 程式仍需各自登入並核准工具使用。外部 AI 呼叫修改模型的工具時，由 AI 程式自己的授權提示確認（不會出現 AI助手的確認卡），模型修改可用 Revit「復原」還原。開發時也可手動註冊（路徑請依實際調整）：

```bash
claude mcp add --scope user sinotech_revit_2025 -e SINOTECH_MCP_CONFIG=%LOCALAPPDATA%\Sinotech\McpExternal\2025\external.json -- node C:\Prj\Revit\Sinotech_MCP\server\build\index.js
```

## 自行開發命令集（SinotechExtensions）

> 使用須經開發者授權，詳見 [`src/Sinotech.Revit.Extensions/LICENSE.md`](src/Sinotech.Revit.Extensions/LICENSE.md)。

中興自行開發的 Revit 功能獨立放在下列位置，和衍生自開源專案的原始命令集分開：

| 位置 | 內容 |
| --- | --- |
| `src/Sinotech.Revit.Extensions/` | C# 命令集，編譯為 `SinotechExtensions.dll`，部署到 `Commands\SinotechExtensions\` |
| `server/src/tools/extensions/` | 對應的 MCP 工具 |

目前功能：依連結 CAD **自動建柱、樑、板、牆**，四個功能各自獨立；建牆預設只建牆，門、窗、柱、板須明確要求。未指定牆類型時依 CAD 牆厚選型（同厚度優先，沒有就複製單層牆類型）；建牆後自動逐組接合（失敗只復原該組）；可指定柱類型；另有 `auto_join_elements` 接合／取消接合既有的柱、梁、牆、板。在平面視圖連結 DWG 後，於 AI助手中輸入例如「用連結的 CAD 自動建牆」「依 CAD 建柱」「依結構平面圖建梁」「牆柱建好後建板」，AI 會自行判定圖層並直接執行（不跳確認，可用 Revit「復原」還原）。詳見 [`src/Sinotech.Revit.Extensions/README.md`](src/Sinotech.Revit.Extensions/README.md)。

## 授權

衍生自 MIT 授權專案，詳見 `LICENSE` 與 `NOTICE.md`。`src/Sinotech.Revit.Extensions/` 與 `server/src/tools/extensions/` 為自行開發部分，不適用 MIT 授權。
