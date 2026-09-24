#!/usr/bin/env node
// make-parametric-wall.js
// スライダー（長さ・高さ・X位置）で駆動するパラメトリック壁の Dynamo グラフを、
// 開いている Revit の Dynamo Home グラフに構築する。
//
//   node scripts/make-parametric-wall.js                 既定値で構築
//   node scripts/make-parametric-wall.js 8000 3000 -1500 長さ/高さ/X位置(mm) 指定
//   node scripts/make-parametric-wall.js --keep          既存グラフを消さずに追記
//
// 前提: Revit + Dynamo Home グラフ + 「Revit MCP Switch」ON。単位は mm 想定。

const { call } = require("./dyn.js");

const args = process.argv.slice(2).filter(a => a !== "--keep");
const keep = process.argv.includes("--keep");
const len = Number(args[0] ?? 5000);
const hgt = Number(args[1] ?? 3000);
const xpos = Number(args[2] ?? 0);

const ops = [];
if (!keep) ops.push({ op: "new" });
ops.push({
  op: "build_graph",
  nodes: [
    { id: "len",  kind: "slider", integer: true, min: 500,   max: 20000, value: len,  x: -460, y: -220 },
    { id: "hgt",  kind: "slider", integer: true, min: 500,   max: 10000, value: hgt,  x: -460, y: -40 },
    { id: "xpos", kind: "slider", integer: true, min: -20000, max: 20000, value: xpos, x: -460, y: 140 },
    { id: "crv",  kind: "codeblock", x: 0, y: 0,
      code: "line = Line.ByStartPointEndPoint(Point.ByCoordinates(x,0,0), Point.ByCoordinates(x+len,0,0));" },
    { id: "lvl",  kind: "library", name: "DSRevitNodesUI.Levels",    x: 0, y: 220 },
    { id: "wt",   kind: "library", name: "DSRevitNodesUI.WallTypes", x: 0, y: 370 },
    { id: "wall", kind: "codeblock", x: 470, y: 60,
      // 'Wall' 単独は AnalyticalAutomation.Geometry.Wall と衝突するため完全修飾
      code: "w = Revit.Elements.Wall.ByCurveAndHeight(line, height, level, walltype);" },
  ],
  // toPort はポート名で指定できる（番号のズレを気にしなくて良い）
  edges: [
    { from: "xpos", fromPort: 0, to: "crv",  toPort: "x" },
    { from: "len",  fromPort: 0, to: "crv",  toPort: "len" },
    { from: "crv",  fromPort: "line", to: "wall", toPort: "line" },
    { from: "hgt",  fromPort: 0, to: "wall", toPort: "height" },
    { from: "lvl",  fromPort: 0, to: "wall", toPort: "level" },
    { from: "wt",   fromPort: 0, to: "wall", toPort: "walltype" },
  ],
});

(async () => {
  const t0 = Date.now();

  // 1) ノード + 配線を 1 バッチ（Manual 固定、最終 Run なし、自動レイアウトで重なり解消）
  const built = await call("dynamo_op", { op: "batch", manualDuringBatch: true, run: false, layout: true, ops });
  const bg = built.results.find(r => Array.isArray(r.nodes));
  if (!bg) { console.error("build_graph failed:", JSON.stringify(built, null, 2)); process.exit(1); }
  const g = {}; for (const n of bg.nodes) g[n.id] = n.guid;

  // 2) ドロップダウン（Level / WallType）を populate して先頭を選択
  await call("dynamo_op", { op: "batch", run: false, ops: [
    { op: "set_dropdown", nodeId: g.lvl, index: 0 },
    { op: "set_dropdown", nodeId: g.wt,  index: 0 },
  ]});

  // 3) 一度だけ評価して完了を待つ
  const run = await call("dynamo_op", { op: "run", wait: true });

  // 4) 結果
  const wall = await call("dynamo_op", { op: "get_node_value", nodeId: g.wall });
  await call("dynamo_op", { op: "set_run_mode", mode: "Automatic" });

  console.log(JSON.stringify({
    ok: run.ok && (wall.values?.[0]?.state === "Active"),
    lengthMm: len, heightMm: hgt, xPosMm: xpos,
    runOutcome: run.outcome,
    wall: wall.values?.[0],
    elapsedMs: Date.now() - t0,
    hint: "以後は Dynamo でスライダーを動かすか set_value でパラメータ変更（同じ壁が更新される）",
  }, null, 2));
})().catch(e => { console.error("FAILED:", e.message || e); process.exit(1); });
