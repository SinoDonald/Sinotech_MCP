// ============================================================================
//  Sinotech_MCP 自行開發命令集（SinotechExtensions）
//  開發者：Donald（donaldlu@mail.sinotech.com.tw）
//  Copyright © 2026 開發者保留所有權利。
//  本檔案不適用專案根目錄之 MIT 授權；未經開發者書面授權，
//  不得使用、複製、修改或散布。詳見 src/Sinotech.Revit.Extensions/LICENSE.md
// ============================================================================
import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { withRevitConnection } from "../../utils/ConnectionManager.js";

// 自動建柱：依 CAD 柱輪廓建立結構柱，只建柱（會修改模型）
export function registerCreateColumnsFromCadTool(server: McpServer) {
  server.registerTool(
    "create_columns_from_cad",
    {
      description:
        "【自動建柱】只建柱：依目前視圖連結 CAD 的柱輪廓（邊長 20～150 cm 的封閉矩形或圓）建立結構柱，高度為基準樓層到頂部樓層，依輪廓方向旋轉；" +
        "類型選用 b×h 相同者，沒有就複製一個，專案中沒有混凝土柱族群時從 Revit 族庫載入。柱圖層省略時自動判定（尺寸重複、貼著已建的牆或梁；" +
        "沒有牆梁時改用 CAD 牆線）。樓層省略時使用目前平面視圖的樓層與其上一層。只處理視圖裁剪範圍內的圖形。" +
        "使用者指定柱類型時才填 columnTypeId／columnTypeName（未指定時自動選型：尺寸相同的類型，沒有就複製）。回傳的 elementIds 可交給 auto_join_elements 與牆梁板接合。" +
        "直接執行，不要詢問使用者；執行結果請如實轉告使用者。",
      inputSchema: {
        columnLayers: z
          .array(z.string())
          .optional()
          .describe("柱的 CAD 圖層（get_cad_layers 中 columnShapes 多的圖層）；省略則自動判定"),
        baseLevel: z.string().optional().describe("基準樓層名稱；省略則使用目前平面視圖的樓層"),
        topLevel: z.string().optional().describe("頂部樓層名稱；省略則使用基準樓層的上一層"),
        columnTypeId: z.number().optional().describe("使用者指定的結構柱類型 ElementId（取自 get_cad_layers 的 columnTypes）；與 columnTypeName 擇一，未指定時省略（自動選型）"),
        columnTypeName: z.string().optional().describe("使用者指定的結構柱類型名稱（類型名稱或「族群：類型」）；名稱對應多個類型時會回報可選清單，請改用 columnTypeId"),
        matchCadSize: z.boolean().default(true).describe("指定柱類型時：true（預設）依 CAD 尺寸選用或複製同族群類型；使用者要求全部用同一類型、不依 CAD 尺寸時才設 false"),
        importIds: z
          .array(z.number())
          .optional()
          .describe("只使用指定的連結 CAD（ImportInstance 的 ElementId）；省略則使用目前視圖中全部連結 CAD"),
      },
      annotations: { readOnlyHint: false, destructiveHint: false },
    },
    async (args) => {
      const params = {
        columnLayers: args.columnLayers ?? [],
        baseLevel: args.baseLevel,
        topLevel: args.topLevel,
        columnTypeId: args.columnTypeId,
        columnTypeName: args.columnTypeName,
        matchCadSize: args.matchCadSize ?? true,
        importIds: args.importIds ?? [],
      };

      try {
        const response = await withRevitConnection(async (revitClient) => {
          return await revitClient.sendCommand("create_columns_from_cad", params);
        });

        return {
          content: [
            {
              type: "text",
              text: JSON.stringify(response, null, 2),
            },
          ],
        };
      } catch (error) {
        return {
          content: [
            {
              type: "text",
              text: `自動建柱失敗：${
                error instanceof Error ? error.message : String(error)
              }`,
            },
          ],
        };
      }
    }
  );
}
