// 配置先: server/src/tools/dynamo_graph_control.ts
// register.ts が自動でこのファイルを検出するので手動登録は不要。

import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { withRevitConnection } from "../utils/ConnectionManager.js";

export function registerDynamoGraphControlTool(server: McpServer) {
  server.tool(
    "dynamo_graph_control",
    "Inspect and edit the currently open Dynamo (Revit) Home graph. Operations: " +
      "'run' forces a recompute (add wait:true to block until evaluation finishes, so a following get_node_value is fresh); " +
      "'get_graph' returns every node (guid, type, ports, state, messages) and every connector; " +
      "'get_node_value' returns the evaluated output of one node (by guid) or all, as structured JSON, plus state/messages; " +
      "'clear' deletes every node; 'new' clears to an empty Home graph; 'open' opens a .dyn (path); " +
      "'set_run_mode' switches the graph between Manual/Automatic/Periodic (mode); " +
      "'layout' runs Dynamo's auto graph layout (topological columns, removes node overlap); " +
      "'search' finds node names by fuzzy query (query, max) — returns creationName usable as a library node name; " +
      "'node_info' reverse-looks-up a placed node (nodeId) — returns its library category, creationName, description; " +
      "'predict_ports' returns a Code Block's in/out port names for a given 'code' WITHOUT creating the node; " +
      "'delete' deletes specific nodes (nodeIds); 'move' repositions a node (nodeId, x, y); " +
      "'connect' wires an existing node's output to another's input (from, fromPort, to, toPort); 'disconnect' removes that wire; " +
      "'set_lacing' sets a node's lacing (nodeId, strategy = Auto|Longest|Shortest|CrossProduct|First|Disabled); " +
      "'set_dropdown' populates and selects a dropdown node (nodeId, and index or item); " +
      "'save' writes the graph to a .dyn path. " +
      "Node GUIDs come from dynamo_build_graph's response or from get_graph.",
    {
      op: z
        .enum([
          "run",
          "get_graph",
          "get_node_value",
          "clear",
          "new",
          "open",
          "delete",
          "move",
          "connect",
          "disconnect",
          "set_lacing",
          "set_dropdown",
          "set_run_mode",
          "layout",
          "search",
          "node_info",
          "predict_ports",
          "save",
        ])
        .describe("Which operation to perform."),
      nodeId: z
        .string()
        .optional()
        .describe("get_node_value / move / set_lacing / set_dropdown: the target node GUID."),
      nodeIds: z.array(z.string()).optional().describe("delete: node GUIDs to delete."),
      x: z.number().optional().describe("move: new X position."),
      y: z.number().optional().describe("move: new Y position."),
      from: z.string().optional().describe("connect/disconnect: source node GUID."),
      fromPort: z.number().int().optional().describe("connect/disconnect: output port index."),
      to: z.string().optional().describe("connect/disconnect: target node GUID."),
      toPort: z.number().int().optional().describe("connect/disconnect: input port index."),
      strategy: z
        .enum(["Auto", "Longest", "Shortest", "CrossProduct", "First", "Disabled"])
        .optional()
        .describe("set_lacing: lacing strategy."),
      index: z.number().int().optional().describe("set_dropdown: item index to select."),
      item: z.string().optional().describe("set_dropdown: item display name to select (alternative to index)."),
      path: z.string().optional().describe("open / save: absolute .dyn file path."),
      query: z.string().optional().describe("search: fuzzy node-name query, e.g. 'wall', 'point by'."),
      code: z.string().optional().describe("predict_ports: DesignScript to analyze."),
      max: z.number().int().optional().describe("search: max results (default 20)."),
      wait: z
        .boolean()
        .optional()
        .describe("run: block until the graph finishes evaluating (RefreshCompleted)."),
      mode: z
        .enum(["Manual", "Automatic", "Periodic"])
        .optional()
        .describe("set_run_mode: graph run mode."),
    },
    async (args, extra) => {
      try {
        const response = await withRevitConnection(async (revitClient) => {
          return await revitClient.sendCommand("dynamo_op", { ...args });
        });
        return {
          content: [{ type: "text", text: JSON.stringify(response, null, 2) }],
        };
      } catch (error) {
        return {
          content: [
            {
              type: "text",
              text: `dynamo_graph_control failed: ${
                error instanceof Error ? error.message : String(error)
              }`,
            },
          ],
        };
      }
    }
  );
}
