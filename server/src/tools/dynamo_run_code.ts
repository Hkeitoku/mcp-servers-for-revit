// 配置先: server/src/tools/dynamo_run_code.ts
// register.ts が自動でこのファイルを検出するので、手動登録は不要。

import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { withRevitConnection } from "../utils/ConnectionManager.js";

export function registerDynamoRunCodeTool(server: McpServer) {
  server.tool(
    "dynamo_run_code",
    "Add a Code Block node containing DesignScript code to the currently open Dynamo graph in Revit. " +
      "The change appears immediately in the open Dynamo window. " +
      "Example code: 'Point.ByCoordinates(10, 20, 0);'",
    {
      code: z.string().describe("DesignScript code text to place in a new Code Block node."),
      x: z.number().optional().describe("X position on the Dynamo canvas. Defaults to 0."),
      y: z.number().optional().describe("Y position on the Dynamo canvas. Defaults to 0."),
    },
    async (args, extra) => {
      try {
        const response = await withRevitConnection(async (revitClient) => {
          return await revitClient.sendCommand("dynamo_run_code", args);
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
              text: `dynamo_run_code failed: ${
                error instanceof Error ? error.message : String(error)
              }`,
            },
          ],
        };
      }
    }
  );
}
