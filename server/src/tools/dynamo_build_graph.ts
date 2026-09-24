// 配置先: server/src/tools/dynamo_build_graph.ts
// register.ts が自動でこのファイルを検出するので手動登録は不要。

import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { withRevitConnection } from "../utils/ConnectionManager.js";

const nodeSchema = z.object({
  id: z
    .string()
    .describe("Caller-defined node id, referenced by edges. Any unique string."),
  kind: z
    .enum(["codeblock", "library", "slider", "python"])
    .default("codeblock")
    .describe(
      "codeblock = a Code Block node running DesignScript (default); " +
        "library = a Dynamo library node placed by search name; " +
        "slider = an Integer/Number Slider input node; " +
        "python = a Python Script node."
    ),
  code: z
    .string()
    .optional()
    .describe(
      "kind=codeblock: DesignScript text, e.g. 'x * x;'. " +
        "kind=python: the Python script body (IN[0..], OUT)."
    ),
  engine: z
    .string()
    .optional()
    .describe("kind=python: engine name, default 'CPython3'."),
  name: z
    .string()
    .optional()
    .describe(
      "kind=library: exact Dynamo search name, e.g. 'Point.ByCoordinates', 'List.Flatten', 'Math.Sin'."
    ),
  min: z.number().optional().describe("kind=slider: minimum value."),
  max: z.number().optional().describe("kind=slider: maximum value."),
  value: z.number().optional().describe("kind=slider: current value (clamped to min..max)."),
  integer: z
    .boolean()
    .optional()
    .describe("kind=slider: true = Integer Slider, false/omitted = Number Slider."),
  x: z.number().optional().describe("X position on the Dynamo canvas. Default 0."),
  y: z.number().optional().describe("Y position on the Dynamo canvas. Default 0."),
});

const edgeSchema = z.object({
  from: z.string().describe("Source node id (its output port feeds the wire)."),
  fromPort: z
    .union([z.number().int(), z.string()])
    .describe("Output port: 0-based index, or the port name (e.g. 'line')."),
  to: z.string().describe("Target node id."),
  toPort: z
    .union([z.number().int(), z.string()])
    .describe("Input port: 0-based index, or the port name (e.g. 'height'). Names are safer than indices for Code Blocks."),
});

export function registerDynamoBuildGraphTool(server: McpServer) {
  server.tool(
    "dynamo_build_graph",
    "Build a multi-node graph in the currently open Dynamo (Revit) Home workspace: place several " +
      "nodes and wire them together in one call. Nodes can be Code Block nodes (DesignScript), " +
      "library nodes placed by search name, or slider input nodes. The response lists every created " +
      "node with its real GUID and its actual input/output port names+indices — use those indices " +
      "for follow-up wiring. Code Block port indices are only known after creation, so wire in the " +
      "same call via 'edges' or inspect the response first. Requires Dynamo open on a Home graph.",
    {
      nodes: z.array(nodeSchema).describe("Nodes to create."),
      edges: z
        .array(edgeSchema)
        .optional()
        .describe("Wires to draw after all nodes exist. Omit for an unwired set of nodes."),
      layout: z
        .boolean()
        .optional()
        .describe("Run Dynamo's auto graph layout after building so nodes don't overlap. Default false."),
    },
    async (args, extra) => {
      try {
        const response = await withRevitConnection(async (revitClient) => {
          return await revitClient.sendCommand("dynamo_op", {
            op: "build_graph",
            nodes: args.nodes,
            edges: args.edges ?? [],
            layout: args.layout ?? false,
          });
        });
        return {
          content: [{ type: "text", text: JSON.stringify(response, null, 2) }],
        };
      } catch (error) {
        return {
          content: [
            {
              type: "text",
              text: `dynamo_build_graph failed: ${
                error instanceof Error ? error.message : String(error)
              }`,
            },
          ],
        };
      }
    }
  );
}
