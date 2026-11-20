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

const category = z.enum(["columns", "beams", "walls", "floors"]);

// 自動接合／取消接合柱、梁、牆、板（會修改模型；每組獨立交易）
export function registerAutoJoinElementsTool(server: McpServer) {
  server.registerTool(
    "auto_join_elements",
    {
      description:
        "【自動接合】接合或取消接合柱、梁、牆、板。範圍二擇一，不可混用，也不預設整個模型：" +
        "(1) elementIds：2～500 個明確元素 ID（例如建牆結果的 wallIds、建柱結果的 elementIds），只處理集合內的配對；" +
        "(2) hostCategory＋targetCategories：目前視圖中主體品類與目標品類之間的配對（columns、beams、walls、floors），相關元素最多 500 個。" +
        "牆與牆先試牆端接合（只接到指定範圍內的牆，鄰近其他牆時保留不接合），失敗再試幾何接合；其他配對用幾何接合。" +
        "action=unjoin 只取消幾何接合，不停用牆端接合。floorPriority 預設 preserve 保持既有切割順序；使用者明確要求時才用 floorCuts（樓板切割其他元素）或 otherCutsFloor。" +
        "候選以包圍盒重疊判斷，不保證有實體交集；每組獨立交易，失敗只復原該組，其他成功的保留，成功操作合併為一筆 Undo，不移動或刪除元素、不自動儲存。" +
        "請如實轉告接合、原本已接合、失敗的組數與問題元素 ID／座標；逾時時先查看模型，不要盲目重試。",
      inputSchema: {
        elementIds: z.array(z.number()).optional().describe("要處理的元素 ID（2～500 個）；與 hostCategory 擇一"),
        hostCategory: category.optional().describe("目前視圖的主體品類；與 elementIds 擇一"),
        targetCategories: z.array(category).optional().describe("目前視圖的目標品類（可多個，可含主體品類本身）"),
        action: z.enum(["join", "unjoin"]).default("join").describe("join 接合（預設）或 unjoin 取消幾何接合"),
        floorPriority: z
          .enum(["preserve", "floorCuts", "otherCutsFloor"])
          .default("preserve")
          .describe("樓板與其他元素的切割順序：preserve（預設，保持既有）、floorCuts、otherCutsFloor"),
        toleranceMm: z.number().min(0).max(50).default(5).describe("候選範圍容差（mm，0～50，預設 5）；不會移動元素或填補空隙"),
        maxPairs: z.number().int().min(1).max(2000).default(1000).describe("候選配對上限（預設 1000，最大 2000）；超過時拒絕、不修改模型"),
      },
      annotations: { readOnlyHint: false, destructiveHint: false },
    },
    async (args) => {
      const params = {
        elementIds: args.elementIds ?? [],
        hostCategory: args.hostCategory,
        targetCategories: args.targetCategories ?? [],
        action: args.action ?? "join",
        floorPriority: args.floorPriority ?? "preserve",
        toleranceMm: args.toleranceMm ?? 5,
        maxPairs: args.maxPairs ?? 1000,
      };

      try {
        const response = await withRevitConnection(async (revitClient) => {
          return await revitClient.sendCommand("auto_join_elements", params);
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
              text: `自動接合失敗：${
                error instanceof Error ? error.message : String(error)
              }`,
            },
          ],
        };
      }
    }
  );
}
