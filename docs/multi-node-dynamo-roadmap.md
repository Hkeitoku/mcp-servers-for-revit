# Multi-Node Dynamo Graph Generation — Implementation Roadmap

**Status:** all phases implemented & verified in live Revit 2026 / Dynamo 3.6 · **Last updated:** 2026-09-03

## Implementation status (2026-09-03) — VERIFIED

All four phases implemented and tested end-to-end in a running Revit 2026 / Dynamo 3.6.

| Test | Result |
|---|---|
| `build_graph`: 3 code blocks `1..10` → `x*x` → `Math.Sum(s)` + 2 wires | ✅ sum node = **385** |
| `get_graph` / `get_node_value` / `clear` | ✅ topology + values read back, 3 nodes deleted |
| `build_graph`: `slider(int,0..100,25)` → `library "Math.Sqrt"` + 1 wire | ✅ sqrt node = **5**, slider = 25 |
| `save` → `.dyn` | ✅ 2.9 KB valid `.dyn` written |
| `run` | not exercised (graph is in Automatic run mode; recompute is implicit) |

### What changed from the original plan during bring-up

1. **The plugin reads `commandRegistry.json`, not `command.json`.** `%AppData%\Autodesk\Revit\Addins\2026\revit_mcp_plugin\Commands\commandRegistry.json`, hand-maintained, not in the repo. `dynamo_op` had to be added there with `"assemblyPath": "RevitMCPCommandSet\\{VERSION}\\RevitMCPCommandSet.dll"`. Repo reference copy: `plugin/commandRegistry.reference.json`.
2. **Library nodes do NOT use `SearchModel`.** In 3.6 `DynamoModel.SearchModel` is a field and `Search()` takes `(string, LuceneSearchUtility, CancellationToken)`. Dropped that path entirely — `DynamoBridge.CreateNodeByName` uses `CreateNodeCommand(string id, string name, x, y, …)` and lets Dynamo resolve the name. No `DynamoModel` needed. DummyNode result → treated as "name not found".
3. **Sliders** are created by concrete type name `CoreNodeModels.Input.IntegerSlider` / `DoubleSlider`, then `Min`/`Max`/`Value` set via `UpdateModelValueCommand` (best-effort).
4. **The `拡張機能` menu item never appears** — `IViewExtensionSource.RequestAddExtension` does not fire in Dynamo 3.6. Not needed; the bridge attaches via `IExtension.Ready()`. Menu code left in as a harmless no-op / future PoC hook.
5. **`MakeConnectionCommand` wiring works as written** (Begin output → End input).
6. **`WorkspaceModel.Save(string)` works** via reflection (single-string overload present in 3.6).

### Round 3.9 — layout, node search, reverse-lookup (2026-09-04)

Grasshopper-parity conveniences, all verified live via socket:
- **`op:"layout"`** / `build_graph`+`batch` `layout:true` — `LayoutExtensions.DoGraphAutoLayout(WorkspaceModel)` (internal, reflection) = Dynamo's "Cleanup Node Layout". Place nodes anywhere (even all at one point) then auto-arrange into topological columns, no overlap.
- **`op:"search"`** (`query`, `max`) — GH double-click search. `NodeSearchModel.Search(string, LuceneSearchUtility, CancellationToken)` (nonpublic) via reflection (`SearchModel` public field + `LuceneUtility` internal prop on `DynamoModel`); substring-filter fallback. Returns `{name, creationName, fullName, category, kind}`. `creationName` (e.g. `Revit.Elements.Wall.ByCurveAndHeight@Autodesk.DesignScript.Geometry.Curve,double,...`) drops straight into `build_graph` `library.name`.
- **`op:"node_info"`** (`nodeId`) — GH Alt+Ctrl reverse-lookup. Returns the placed node's `category` (ribbon/library path), `creationName`, `description`, ports, state.
- Every node in `get_graph` / `build_graph` now carries `category` + `creationName`.

### Round 3.8 — run-wait deterministic completion (2026-09-04)

External-AI latency report (source-referenced, verified vs local DLL) pinned the 25 s hang:
**a clean-graph `Run()` fires `EvaluationCompleted` with `EvaluationTookPlace == false` and never fires `RefreshCompleted`.** Round 3.7's `EvaluationStarted` grace worked but was timing-based.

Round 3.8 replaces it: `BeginRunAndArm` subscribes **`EvaluationCompleted`** (+ `RefreshCompleted`).
- `EvaluationTookPlace == false` → no-op → complete immediately (existing `CachedValue` is current).
- `EvaluationTookPlace == true` → wait for `RefreshCompleted` (values refresh after).
- `EvaluationCompletedEventArgs.Error` → surfaced as `graphError` in the response.
`WaitRun` now also exposes `LastRunOutcome` (`noop`/`evaluated`/`refreshed`) + `LastRunError`; run response carries `outcome`. Verified public on 3.6.2: `HomeWorkspaceModel.EvaluationCount` (Int64), `EvaluationCompletedEventArgs.{EvaluationTookPlace,EvaluationSucceeded,Error}`, `NodeModel.NodeExecutionBegin/End` (for future profiling). `GraphRunInProgress` internal.

Report's remaining roadmap (highest value first): ① this run-wait fix (done), ② one logical `apply_graph_transaction` = one ExternalEvent (batch — done Round 3.7), ③ Manual + single Run (done), ④ ExternalEvent handler must not block on completion — use TCS to the socket thread (done Round 3.6 via `_deferred`), ⑤ persistent TCP + **4-byte length-prefix framing** (server's "one Read = one JSON" assumption breaks on persistent conns), ⑥ requestId `Stopwatch` structured trace end-to-end, ⑦ Wall-transaction / TuneUp node profiling, ⑧ collectible `AssemblyLoadContext` reloadable-logic + fixed shim (dev loop). Full report kept context — see `docs/perf-research-prompt.md` history.

### Round 3.7 — latency fixes: batch op, run-wait early-out (2026-09-04)

Diagnosed slowness in the wall-graph test (~10 socket round-trips, each triggering a full Automatic-mode re-eval → a Revit transaction per `connect`).

- **`op:"batch"`** — `{op:"batch", ops:[...], manualDuringBatch:true, run:true}` runs N sub-ops in ONE `ExecuteBatch` (= one ExternalEvent), sets `RunType=Manual` for the duration, restores it, then fires ONE final `RunCancelCommand`. Collapses the wall graph from ~10 round-trips + ~10 re-evals to ~2 + 1. `Dispatch()` extracted so any op is callable from batch. New `DynamoBridge.GetRunMode()`.
- **`run wait` early-out** — `BeginRunAndArm` now also subscribes `EvaluationStarted`; `WaitRun` returns immediately (`true`) if no evaluation starts within 1.2 s (was: always wait the full 25 s when nothing was dirty).
- **`scripts/test-wall-batch.js`** — timed parametric-wall build via batch.
- Not done: persistent client socket (server already loops for multiple requests per connection — `SocketService.HandleClientCommunication` has `while(connected)`; only the Node client reconnects per call). Low priority once batching lands. Also: hot-reload dev loop, profiling — see `docs/perf-research-prompt.md`.

### Round 3.6 — run+wait, set_run_mode, Civil 3D compile target (2026-09-04)

- **`run` + `wait:true`** — verified, no deadlock. `DynamoBridge.BeginRunAndArm()` on the UI thread reflection-subscribes `HomeWorkspaceModel.RefreshCompleted` (one-shot) and fires `RunCancelCommand`, returning `{"pending":true}` immediately; `DynamoOpEventHandler` then `Task.Run`s `bridge.WaitRun(25000)` **off the UI thread** and signals completion. Manual-mode test: build (value `null`) → `run wait:true` → `{"waited":true}` in 189 ms → value correct.
- **`set_run_mode`** (`mode`: Manual/Automatic/Periodic) — sets `RunSettings.RunType` via reflection.
- **`scripts/dyn.js`** — permanent CLI to drive the bridge over the socket directly (`node scripts/dyn.js '{"op":...}'`), for when the MCP tool index drops the `dynamo_*` tools.
- **Civil 3D compile target** — `DynamoMcpExtension/Civil3D/DynamoMcpExtension.Civil3D.csproj` compiles the **same** `DynamoBridge.cs` / `GraphOps.cs` / `DynamoMcpExtension.cs` against **Dynamo Core 3.4.1.7055** (Civil 3D 2026). Builds clean; all 16 bridge methods present; links 3.4.1. Confirms the bridge is host-portable. **Still needed for a working C3D bridge:** the socket/host layer — an AutoCAD `IExtensionApplication` plugin that marshals socket messages to the AutoCAD command context (`DocumentCollection.ExecuteInCommandContextAsync`) and reflects into `DynamoBridge.ExecuteBatch` (the Revit-side `RevitMCPPlugin` + `DynamoOpEventHandler` equivalent).

### Round 3.5 — corrections from external review, live-tested (2026-09-04)

Applied and verified (via direct socket to :8080; the MCP tool index was glitched — see note):

| Change | Result |
|---|---|
| `DynamoBridge` holds `ReadyParams` (not a cached `WorkspaceModel`) — resolves `CommandExecutive`/`CurrentWorkspace` per call | ✅ no stale ref |
| `python` node kind (`PythonNodeModels.PythonNode`, script via `"ScriptContent"`, engine via `"EngineName"`) | ✅ `OUT=IN[0]*IN[0]`, in 5 → out 25 |
| `get_node_value` structured (recurse `MirrorData`, leaf `.Data` → number/string/`{type,x,y,z}`) | ✅ `[1,4,9]` etc. |
| `set_lacing` (`"ArgumentLacing"` + enum name) | ✅ |
| `move` (`UpdateModelValueCommand "Position" "x;y"`) | ✅ |
| `connect` / `disconnect` on existing nodes | ✅ wire added/removed, confirmed via `get_graph` |
| `new` (`HomeWorkspaceModel.Clear()`) | ✅ |
| `set_dropdown` (`PopulateItems()` → `Items` → `SaveSelectedIndexImpl` → `"Value"`) | not tested (no dropdown without Revit context) |
| `run` proper `RefreshCompleted` wait | deferred (needs the wait off the UI thread) |

**MCP tool-index glitch:** after repeated mid-session MCP reconnects, Claude Code dropped the 3 `dynamo_*` tools from its index while the node server still advertises all 29. Fix = fresh `claude` session. Testing workaround: `%TEMP%\dyntest.js` talks to `localhost:8080` directly (`{jsonrpc,method:"dynamo_op",params:{op},id}`).

### Round 2 — added ops from the 3.6.2 API research (2026-09-04)

External-AI research report cross-checked against the local `DynamoCore.dll` (3.6.2) by reflection. Confirmed and implemented:

| New `dynamo_op` / field | API used | Verified |
|---|---|---|
| `delete` (specific `nodeIds`) | `DeleteModelCommand(guid)` | ✅ ctor confirmed |
| `move` (`nodeId`,`x`,`y`) | `UpdateModelValueCommand(guid, "Position", "x;y")` | ⚠ `UpdateValueCore` is protected; reached via command — test live |
| `connect` (existing nodes) | same `MakeConnectionCommand` Begin/End | ✅ (proven in build_graph) |
| `disconnect` | `DeleteModelCommand(connectorGuid)` → fallback `WorkspaceModel.ClearConnector` (internal, reflection) | ⚠ connector-guid delete unconfirmed — test live |
| node `state` + `messages` in every response | `NodeModel.State` (`ElementState`), `NodeModel.NodeInfos` (`List<Info>`: `Message`,`State`) | ✅ both public, fields confirmed |
| structured `get_node_value` | `MirrorData.IsNull`/`IsCollection`/`GetElements()` recurse; leaf `.Data` → number/string/`{type,x,y,z}` | ✅ pattern matches Dynamo's own render path |

Also confirmed for later: `WorkspaceModel.Save(string,bool,EngineController)` is **public** (current reflection in `Save` still works, no need to change); `EngineController.GetBuildWarnings()/GetRuntimeWarnings()` are internal (reflection only); `DSDropDownBase` lives at `CoreNodeModels.DSDropDownBase` (not `.Input.`), `PopulateItems()` public, select via `UpdateModelValueCommand(guid,"Value",index)` — dropdown support not yet built (Revit category/family dropdowns need document context).

Node-name conventions for `library` kind: ZeroTouch → `FunctionDescriptor.MangledName` (short name works when unambiguous); NodeModel package nodes → CLR `Type.ToString()`; custom `.dyf` → `FunctionId` GUID string.

### Redeploy notes

- Changing `DynamoMcpExtension.dll` needs Revit fully closed (Dynamo locks it); the `.csproj` post-build copies it to the package `bin\`.
- Changing `RevitMCPCommandSet.dll` needs Revit fully closed; `Assembly.LoadFrom` caches it per Revit session, so a toggle of "Revit MCP Switch" is not enough — restart Revit.
- Changing the node server (`server/`) needs `npm run build` + **Claude Code restart**.
- `commandRegistry.json` edits are picked up on the next OFF→ON toggle of "Revit MCP Switch" (no Revit restart needed for that file alone).

| Layer | New / changed | Compiles |
|---|---|---|
| `DynamoMcpExtension` | `DynamoBridge.cs` (+`ExecuteBatch`, `Model`, `Connect`/`SetNodeValue` widened), **new** `GraphOps.cs`, `DynamoMcpExtension.cs` (captures `DynamoModel`, adds wiring PoC menu), `.csproj` (+Newtonsoft ref) | yes |
| `commandset` | **new** `Commands/Dynamo/DynamoOpCommand.cs` (`dynamo_op`), **new** `Services/DynamoOpEventHandler.cs`, `command.json` (+entry, reformat) | yes (`Debug R26`) |
| `server` | **new** `src/tools/dynamo_build_graph.ts`, **new** `src/tools/dynamo_graph_control.ts` | yes |

**Single reflection boundary:** `DynamoBridge.ExecuteBatch(string json) → string json`, delegating to `GraphOps`.
**Ops:** `build_graph` / `run` / `get_graph` / `get_node_value` / `clear` / `save`.
**Node kinds:** `codeblock` / `library` / `slider`.

**Unverified against the 3.6 runtime** (all wrapped in try/catch, return structured errors — a
failure degrades that one op, it does not crash the bridge):
`MakeConnectionCommand` wiring · `RunCancelCommand` · slider value-set via `UpdateModelValueCommand`
· `WorkspaceModel.Save` · `NodeSearchModel.Search` result shape.

### Deploy & first test

1. **Close Revit** (both DLLs are locked while it runs).
2. Build + auto-deploy:
   ```
   cd DynamoMcpExtension && dotnet build -c Debug
   cd ../commandset      && dotnet build -c "Debug R26"
   cd ../server          && npm run build
   ```
3. **Restart Claude Code** (reloads the node server → new tools appear).
4. Start Revit → open a Dynamo **Home** graph → press "Revit MCP Switch".
5. Smoke test, in order:
   - Dynamo menu → **"MCP Bridge: 複数ノード結線テスト"** — proves `build_graph` + wiring in-process (no MCP).
   - From Claude: `dynamo_build_graph` with list→square→sum (expect sum 385).
   - From Claude: `dynamo_graph_control op=get_node_value` on the sum node.
6. Record which unverified items worked in the table above; fix the rest.

---

### Original plan


## Goal

Extend the `dynamo_run_code` bridge so Claude can build **multi-node Dynamo graphs**
(multiple nodes + wiring + input values + execution feedback) from natural-language
prompts — not just a single Code Block node.

## Current state (verified 2026-09-03)

- Full chain works: `Claude → node MCP server → socket :8080 → RevitMCPPlugin → commandset ExternalEvent → reflection → DynamoBridge → live graph`.
- `DynamoBridge` already contains `CreateNode`, `Connect`, `SetNodeValue`, `DeleteNode`, `CreateCodeBlockNode`.
  Only `CreateCodeBlockNode` is tested, and only it is reachable from the upper layers.
- The extension captures only `CommandExecutive` + `WorkspaceModel` in `Ready()` — **not** `DynamoModel`.

## Architecture decision — single JSON reflection boundary

Today `DynamoRunCodeEventHandler` reflects on a named method (`GetMethod("CreateCodeBlockNode")`).
Every new capability would add another fragile reflection call.

**Decision:** add exactly one method — `string DynamoBridge.ExecuteBatch(string requestJson)`
returning a JSON string. All graph-building logic lives inside `DynamoMcpExtension`, where it
has real Dynamo type references. The commandset reflects on this one method forever.

```
MCP tool     typed args (nodes[], edges[], op, …)
  → Command       validate → serialize to JSON → pass through
    → EventHandler   reflection: ExecuteBatch(json)   ← ONE call, forever
      → DynamoBridge.ExecuteBatch   parse → dispatch → build → serialize result
```

### Request / response envelope

```jsonc
// request
{
  "op": "build_graph",
  "nodes": [
    { "id": "a", "kind": "codeblock", "code": "(1..10);",  "x": 0,   "y": 0 },
    { "id": "b", "kind": "codeblock", "code": "x*x;",       "x": 300, "y": 0 }
  ],
  "edges": [
    { "from": "a", "fromPort": 0, "to": "b", "toPort": 0 }
  ]
}

// response
{
  "ok": true,
  "nodes": [
    { "id": "a", "guid": "…", "inPorts": [], "outPorts": [{ "index": 0, "name": "" }] },
    { "id": "b", "guid": "…", "inPorts": [{ "index": 0, "name": "x" }], "outPorts": [{ "index": 0, "name": "" }] }
  ],
  "errors": []
}
```

---

## Phase 0 — Prep & PoC (gates Phases 1–3)

**Goal:** de-risk the two unknowns before writing production layers:
1. Does `MakeConnectionCommand` wiring actually work as written in `DynamoBridge.Connect`?
2. How do we reach `DynamoModel` (needed for node search in Phase 2)?

| File | Change |
|---|---|
| `DynamoMcpExtension/DynamoBridge.cs` | add `ExecuteBatch(string)` skeleton returning `{"ok":true}`; add `DynamoModel Model { get; internal set; }` |
| `DynamoMcpExtension/DynamoMcpExtension.cs` | in `Ready()`, attempt to capture `DynamoModel` (document the route that works) |
| `DynamoMcpExtension/DynamoMcpExtension.cs` | in `TestButtonViewExtension`, add a 2-node wiring PoC |

**Steps**

1. PoC wiring test in `TestButtonViewExtension.Loaded`:
   - create CBN `"(1..5);"` at (0,0) → `guidA`
   - create CBN `"x*x;"` at (300,0) → `guidB`
   - `DynamoBridge.Instance.Connect(guidA, 0, guidB, 0)`
   - expect a wire on the canvas and `x*x` → `{1,4,9,16,25}`
2. If `Connect` fails, check in order:
   - `PortType` is from `Dynamo.Graph.Nodes` (already correct in source)
   - port index base (0-based expected)
   - `MakeConnectionCommand.Mode.Begin` must target the **output** node/port
   - fallback: `CurrentWorkspace.AddConnection(...)` / `ConnectorModel.Make(...)`
3. `DynamoModel` capture — try, in order, and record which works:
   - reflection over `ReadyParams p` backing fields
   - `p.StartupParams` backing fields
   - a Dynamo static accessor (`Dynamo.Applications.*`)
   - **Note:** `CreateNode`, `Connect`, and CBN creation only need
     `CommandExecutive` + `WorkspaceModel` + `EngineController` (already captured).
     `DynamoModel` is *only* required for search (Phase 2), so capture can be deferred to Phase 2
     if it proves hard here — but the PoC reflection attempt belongs in Phase 0.

**Done when:** two code-created nodes, wired by code, produce a correct downstream value,
and this file records exactly how `DynamoModel` is obtained (or that it is deferred).

---

## Phase 1 — Multiple Code Block nodes + wiring

**Goal:** a `dynamo_build_graph` MCP tool that places N Code Block nodes and wires them.

**Why first:** a CBN can call any library method, so N wired code blocks already cover most
real graphs. Lowest risk — reuses the proven `CreateCodeBlockNode` path.

| File | Change |
|---|---|
| `server/src/tools/dynamo_build_graph.ts` | **new** — typed tool (auto-registered by `register.ts`) |
| `commandset/Commands/Dynamo/DynamoBuildGraphCommand.cs` | **new** — validate + serialize to JSON |
| `commandset/Services/DynamoBuildGraphEventHandler.cs` | **new** — one reflection call to `ExecuteBatch` |
| `command.json` (repo root) | add `dynamo_build_graph` entry (and tidy the existing trailing-comma formatting) |
| `DynamoMcpExtension/DynamoBridge.cs` | implement `ExecuteBatch` for `op:"build_graph"` |

**Steps**

1. **`DynamoBridge.ExecuteBatch`**
   - parse JSON (use the Newtonsoft.Json that Dynamo 3.6 already loads in-process; match its version, `Private=false`)
   - `var map = new Dictionary<string, Guid>();`
   - for each node → `map[node.id] = CreateCodeBlockNode(node.code, node.x, node.y);`
   - for each created node, look up the model: `CurrentWorkspace.Nodes.First(n => n.GUID == guid)`, read `InPorts` / `OutPorts` → index + name
   - for each edge → `Connect(map[e.from], e.fromPort, map[e.to], e.toPort);`
   - return `{ ok, nodes:[{id,guid,inPorts,outPorts}], errors:[] }`
   - **undo grouping:** aim for one Ctrl+Z per build (`RecordGroupModelsForUndo` or equivalent).
     If not feasible for v1, accept N steps and record it as debt.
   - **partial failure:** if edge *k* fails, keep prior work, push to `errors[]`, return `ok:false`.
     Do not roll back created nodes — surprising to a user watching the canvas.
2. **EventHandler** — copy `DynamoRunCodeEventHandler.cs`; reflected method becomes `ExecuteBatch`;
   pass the raw request JSON string, return the raw response JSON string.
3. **Command** — copy `DynamoRunCodeCommand.cs`; `CommandName => "dynamo_build_graph"`;
   build the request JSON from `parameters`; raise timeout to 30 s.
4. **MCP tool**
   ```ts
   {
     nodes: z.array(z.object({
       id: z.string(),
       code: z.string(),
       x: z.number().optional(),
       y: z.number().optional(),
     })),
     edges: z.array(z.object({
       from: z.string(), fromPort: z.number(),
       to: z.string(),   toPort: z.number(),
     })).optional(),
   }
   ```
   Send `{ op: "build_graph", nodes, edges }`; return the response text (it carries the port map).

**Test**

- Prompt: "list 1..10, square each, sum them" → one build call, 3 nodes, 2 edges, downstream sum = **385**.
- Port round-trip: response lists `x` as the in-port of the `x*x;` node.

**Done when:** a 3-node wired graph is generated from one prompt and computes correctly,
and the response returns usable port indices.

---

## Phase 2 — Library nodes, sliders, inputs

**Goal:** place real Dynamo nodes by search name (`Point.ByCoordinates`, `Integer Slider`,
package / Zero-Touch nodes) — things a CBN can't express: UI nodes, side-effecting nodes,
interactive inputs.

**Prerequisite:** `DynamoModel` captured (finish the Phase 0 attempt here if it was deferred).

| File | Change |
|---|---|
| `DynamoMcpExtension/DynamoMcpExtension.cs` | capture `DynamoModel` in `Ready()` |
| `DynamoMcpExtension/DynamoBridge.cs` | `AddLibraryNode(string search, x, y)`, `AddNumberSlider(min,max,value,x,y)`; extend `ExecuteBatch` dispatch on node `kind` |
| `server/src/tools/dynamo_build_graph.ts` | node gets `kind: "codeblock" \| "library" \| "slider"`, plus `name` / `value` / `min` / `max` |

**Steps**

1. `AddLibraryNode`:
   - `var results = Model.SearchModel.Search(search);`
   - pick the best match (prefer exact `CreationName` / `FullName`); if none → error entry, don't throw
   - `var node = element.CreateNode();`
   - `CreateNode(node, x, y);`
2. `AddNumberSlider`: `new CoreNodeModels.Input.IntegerSlider()` / `DoubleSlider()`,
   set `Value` / `Min` / `Max`, then `CreateNode`. **Verify the concrete type names against Dynamo 3.6.**
3. `ExecuteBatch` switches on `kind`; wiring logic from Phase 1 is unchanged.
4. MCP tool: widen the node schema; document that `library` nodes need exact Dynamo search names
   and that port indices always come back in the response.

**Test**

- "Integer slider 0–100 at 25 → `Math.Sqrt`" → slider + ZT node + wire, result 5.
- "`Point.ByCoordinates` from three sliders" → 3 sliders + 1 node + 3 wires.

**Done when:** a slider-driven library-node graph is generated and recomputes live when the slider moves.

---

## Phase 3 — Close the loop (run, read, manage)

**Goal:** Claude can run the graph, read node outputs, and manage the canvas — so it can
verify results and iterate without manual clicks.

**Recommendation:** collapse to **one `dynamo_op` MCP tool** with an `op` discriminator
(`build_graph` / `run` / `get_graph` / `get_node_value` / `clear` / `save`) instead of
5 tools + 5 Commands + 5 EventHandlers. All dispatch stays in `ExecuteBatch`; the commandset
stays a single Command + EventHandler pair.

| File | Change |
|---|---|
| `DynamoMcpExtension/DynamoBridge.cs` | `RunGraph()`, `GetNodeValue(guid)`, `GetGraph()`, `ClearGraph()`, `SaveGraph(path)` |
| `server/src/tools/dynamo_op.ts` | single tool with `op` union; or keep `dynamo_build_graph` and add `dynamo_op` |
| `command.json` + `commandset/Commands/Dynamo/` + `commandset/Services/` | one generic `dynamo_op` Command + EventHandler forwarding `op` to `ExecuteBatch` |

**Steps**

1. `RunGraph`: for manual run mode call `home.Run()`; await `EvaluationCompleted`.
2. `GetNodeValue`: `node.CachedValue` → scalars and flat lists serialized directly;
   complex objects → `ToString()` + type name.
3. `GetGraph`: enumerate `CurrentWorkspace.Nodes` → `{guid,type,code?,x,y,inPorts,outPorts}`
   and `CurrentWorkspace.Connectors` → `{from,fromPort,to,toPort}`.
4. `ClearGraph`: `DeleteModelCommand` over all node GUIDs.
5. `SaveGraph`: `workspace.Save(path, …)` / `DynamoModel.SaveCommand` — check the 3.6 API.

**Test**

- Build → run → read the sum node → assert 385, entirely through MCP tools.
- Round-trip: build, `get_graph`, confirm topology matches the request.

**Done when:** Claude builds a graph, runs it, reads back a computed value, and clears the
canvas — all via MCP.

---

## Cross-cutting concerns

- **Thread:** every Dynamo mutation runs on the Revit UI thread (via `ExternalEvent`). Never spawn threads in `ExecuteBatch`.
- **ID mapping:** callers use string ids; `ExecuteBatch` owns the `string → Guid` map. GUIDs never appear in the tool input schema.
- **CBN ports are dynamic:** always create → read ports → wire. Never infer a port index from the code text.
- **Newtonsoft in-process:** reference the exact version Dynamo 3.6 ships, `Private=false` — same class of bug as `send_code_to_revit` / `Nice3point` double-load.
- **Version pinning:** all Dynamo API calls are verified against Dynamo 3.6 / Revit 2026 only. Flag any `[Obsolete]`.
- **Keep docs in sync:** update this file and `~/.claude/.../memory/revit-mcp-dynamo-bridge.md` after each phase.

## Build & deploy reference

| Layer | Build | Deploy target |
|---|---|---|
| Node MCP server | `cd server && npm install && npm run build` | `server/build/index.js` — **Claude Code restart** to reload |
| Commandset | `cd commandset && dotnet build -c "Debug R26"` | auto → `%AppData%\Autodesk\Revit\Addins\2026\revit_mcp_plugin\Commands\RevitMCPCommandSet\` |
| DynamoMcpExtension | `cd DynamoMcpExtension && dotnet build -c Debug` | copy `bin\DynamoMcpExtension.dll` → `%AppData%\Dynamo\Dynamo Revit\3.6\packages\DynamoMcpExtension\bin\` |

**Runtime prerequisites for every test**

1. Revit 2026 open; "Revit MCP Switch" ribbon button pressed (starts socket :8080).
2. Dynamo open on a **Home** workspace (not the Custom Node editor — no `EngineController` there).
3. `~/.claude.json` → `mcpServers.revit` → `node C:\Users\harap\mcp-servers-for-revit\server\build\index.js`.

**Known non-error:** CommandManager logs `创建命令实例失败 / Failed to create command instance` on success (upstream mislabeled log).

## Risk register

| Risk | Phase | Mitigation |
|---|---|---|
| `MakeConnectionCommand` wiring doesn't behave as written | 0 | PoC first; fallback `CurrentWorkspace.AddConnection` / `ConnectorModel.Make` |
| `DynamoModel` unreachable from `ReadyParams` | 0 / 2 | Only needed for search; PoC reflection; fallback static accessor |
| CBN port indices shift after edits | 1 | Always read ports post-create; never cache |
| Newtonsoft version clash in-process | 1 | Match Dynamo 3.6's version exactly, `Private=false` |
| Multi-step undo annoys users | 1 | Document as debt; `RecordGroup` in a later pass |
| Slider / ZT concrete types differ in 3.6 | 2 | Verify `CoreNodeModels.Input.*` names in a PoC |
