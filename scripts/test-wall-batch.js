// Parametric-wall graph built as ONE batch op (1 socket round-trip, 1 re-eval).
// Compare timing vs the old 10-round-trip approach.
//   node scripts/test-wall-batch.js
const { call } = require("./dyn.js");
const P = (l, o) => { console.log("\n=== " + l + " ==="); console.log(JSON.stringify(o, null, 2)); };

(async () => {
  const t0 = Date.now();
  const res = await call("dynamo_op", {
    op: "batch",
    manualDuringBatch: true,
    run: true,
    ops: [
      { op: "new" },
      {
        op: "build_graph",
        nodes: [
          { id: "len",  kind: "slider", integer: true, min: 1000, max: 12000, value: 6000, x: -450, y: -220 },
          { id: "hgt",  kind: "slider", integer: true, min: 1000, max: 6000,  value: 3000, x: -450, y: -40 },
          { id: "xpos", kind: "slider", integer: true, min: -6000, max: 6000, value: 0,    x: -450, y: 140 },
          { id: "crv",  kind: "codeblock", x: 0, y: 0,
            code: "line = Line.ByStartPointEndPoint(Point.ByCoordinates(x,0,0), Point.ByCoordinates(x+len,0,0));" },
          { id: "lvl",  kind: "library", name: "DSRevitNodesUI.Levels",    x: 0, y: 220 },
          { id: "wt",   kind: "library", name: "DSRevitNodesUI.WallTypes", x: 0, y: 370 },
          { id: "wall", kind: "codeblock", x: 460, y: 60,
            code: "w = Revit.Elements.Wall.ByCurveAndHeight(line, height, level, walltype);" },
        ],
        edges: [
          { from: "xpos", fromPort: 0, to: "crv",  toPort: 0 },
          { from: "len",  fromPort: 0, to: "crv",  toPort: 1 },
          { from: "crv",  fromPort: 0, to: "wall", toPort: 0 },
          { from: "hgt",  fromPort: 0, to: "wall", toPort: 1 },
          { from: "lvl",  fromPort: 0, to: "wall", toPort: 2 },
          { from: "wt",   fromPort: 0, to: "wall", toPort: 3 },
        ],
      },
    ],
  });
  P("batch result", res);
  console.log("\nbatch build took " + (Date.now() - t0) + " ms (1 round-trip)");

  // dropdowns need the nodes to exist first; do them + a final run as a 2nd tiny batch
  const bg = res.results.find(r => Array.isArray(r.nodes));
  const g = {}; for (const n of (bg ? bg.nodes : [])) g[n.id] = n.guid;

  const t1 = Date.now();
  const res2 = await call("dynamo_op", {
    op: "batch", run: true,
    ops: [
      { op: "set_dropdown", nodeId: g.lvl, index: 0 },
      { op: "set_dropdown", nodeId: g.wt, index: 0 },
    ],
  });
  P("dropdown batch", res2);
  console.log("dropdown batch took " + (Date.now() - t1) + " ms");

  const wall = await call("dynamo_op", { op: "get_node_value", nodeId: g.wall });
  P("wall value", wall);
  console.log("\nTOTAL " + (Date.now() - t0) + " ms");
})().catch(e => { console.error("FAILED:", e.message); process.exit(1); });
