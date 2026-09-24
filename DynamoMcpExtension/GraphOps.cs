// GraphOps.cs
// 配置場所: プロジェクト "DynamoMcpExtension"
//
// 役割:
//   DynamoBridge.ExecuteBatch(json) から呼ばれる、複数ノードグラフ操作の実体。
//   JSON リクエストを解釈し、ノード生成 / 結線 / 値設定 / 実行 / グラフ取得 / クリア /
//   保存 をおこない、JSON レスポンス文字列を返す。
//
//   例外はすべてここで捕捉し {"ok":false,"errors":[...]} を返す。
//   commandset (リフレクション呼び出し側) に例外を伝播させない方針。
//
// 対応 op:
//   build_graph     nodes[] + edges[]
//   run             グラフを強制再計算
//   get_graph       現在のノード / コネクタ一覧
//   get_node_value  ノードの評価結果 (nodeId 省略時は全ノード)
//   clear           全ノード削除
//   save            path へ .dyn 保存 (ビルドによっては未対応)
//
// ノード kind:
//   codeblock  { code }
//   library    { name }          Dynamo ライブラリ検索名 (例 "Point.ByCoordinates")
//   slider     { min,max,value,integer }

using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using Dynamo.Graph.Nodes;
using Dynamo.Graph.Workspaces;
using Dynamo.Models;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace DynamoMcpExtension
{
    internal static class GraphOps
    {
        public static string Execute(DynamoBridge bridge, string requestJson)
        {
            var errors = new List<string>();
            try
            {
                if (!bridge.IsReady)
                    return Fail("DynamoBridge is not ready. Open a Dynamo Home graph first.");

                JObject req;
                try { req = JObject.Parse(requestJson ?? ""); }
                catch (Exception ex) { return Fail("Invalid request JSON: " + ex.Message); }

                var op = (req["op"]?.ToString() ?? "build_graph").Trim().ToLowerInvariant();

                if (op == "batch") return Batch(bridge, req);
                return Dispatch(bridge, op, req, errors);
            }
            catch (Exception ex)
            {
                return Fail("Unhandled: " + ex.Message);
            }
        }

        /// <summary>単一 op のディスパッチ (batch からも呼ばれる)。</summary>
        private static string Dispatch(DynamoBridge bridge, string op, JObject req, List<string> errors)
        {
            switch (op)
            {
                case "build_graph": return BuildGraph(bridge, req, errors);
                case "run":         return Run(bridge, req);
                case "get_graph":   return GetGraph(bridge);
                case "get_node_value": return GetNodeValue(bridge, req);
                case "clear":       return Clear(bridge, errors);
                case "save":        return Save(bridge, req);
                case "delete":      return Delete(bridge, req, errors);
                case "move":        return Move(bridge, req, errors);
                case "connect":     return Connect(bridge, req, errors);
                case "disconnect":  return Disconnect(bridge, req, errors);
                case "set_lacing":  return SetLacing(bridge, req);
                case "set_dropdown": return SetDropdown(bridge, req);
                case "new":         return NewGraph(bridge);
                case "open":        return OpenGraph(bridge, req);
                case "set_run_mode": return SetRunMode(bridge, req);
                case "set_value":   return SetValue(bridge, req);
                case "layout":      return Layout(bridge);
                case "search":      return SearchNodes(bridge, req);
                case "node_info":   return NodeInfo(bridge, req);
                case "predict_ports": return PredictPorts(bridge, req);
                default:            return Fail("Unknown op: " + op);
            }
        }

        // ---------------------------------------------------------------
        // batch: 複数 op を 1 リクエスト = 1 ExternalEvent で実行。
        //   { "op":"batch", "ops":[{op...},...],
        //     "manualDuringBatch":true (既定),   ← 構築中は Manual にして逐次再評価を止める
        //     "run":true (既定) }                ← 最後に 1 回だけ再評価 (fire-and-return)
        // build_graph の edges と組み合わせれば、壁グラフ 1 個 = 1 往復・1 再評価にできる。
        // ---------------------------------------------------------------
        private static string Batch(DynamoBridge bridge, JObject req)
        {
            var ops = req["ops"] as JArray ?? new JArray();
            bool manual = req["manualDuringBatch"]?.Value<bool>() ?? true;
            bool finalRun = req["run"]?.Value<bool>() ?? true;

            string prevMode = null;
            if (manual)
            {
                try { prevMode = bridge.GetRunMode(); bridge.SetRunMode("Manual"); } catch { }
            }

            var results = new JArray();
            var errors = new List<string>();
            foreach (var sub in ops.OfType<JObject>())
            {
                var subOp = (sub["op"]?.ToString() ?? "").Trim().ToLowerInvariant();
                try
                {
                    var subErrors = new List<string>();
                    var r = Dispatch(bridge, subOp, sub, subErrors);
                    results.Add(SafeParse(r));
                    errors.AddRange(subErrors.Select(e => subOp + ": " + e));
                }
                catch (Exception ex)
                {
                    errors.Add(subOp + ": " + ex.Message);
                    results.Add(new JObject { ["ok"] = false, ["error"] = ex.Message });
                }
            }

            // layout:true なら全 op 実行後・最終 Run 前に自動レイアウト
            if (req["layout"]?.Value<bool>() == true)
                try { bridge.AutoLayout(); } catch (Exception ex) { errors.Add("layout: " + ex.Message); }

            if (manual && prevMode != null)
                try { bridge.SetRunMode(prevMode); } catch { }

            if (finalRun)
            {
                try
                {
                    bridge.CommandExecutive.ExecuteCommand(
                        new DynamoModel.RunCancelCommand(false, false),
                        bridge.ExtensionUniqueId, bridge.ExtensionName);
                }
                catch (Exception ex) { errors.Add("final run: " + ex.Message); }
            }

            return new JObject
            {
                ["ok"] = errors.Count == 0,
                ["results"] = results,
                ["errors"] = JArray.FromObject(errors),
            }.ToString(Formatting.None);
        }

        private static JToken SafeParse(string s)
        {
            try { return JToken.Parse(s); } catch { return new JValue(s); }
        }

        // ---------------------------------------------------------------
        // build_graph
        // ---------------------------------------------------------------
        private static string BuildGraph(DynamoBridge bridge, JObject req, List<string> errors)
        {
            var nodes = req["nodes"] as JArray ?? new JArray();
            var edges = req["edges"] as JArray ?? new JArray();

            // caller の文字列 id -> 実際の Guid
            var idMap = new Dictionary<string, Guid>();
            var resultNodes = new JArray();

            foreach (var n in nodes.OfType<JObject>())
            {
                var callerId = n["id"]?.ToString() ?? Guid.NewGuid().ToString("N");
                var kind = (n["kind"]?.ToString() ?? "codeblock").Trim().ToLowerInvariant();
                double x = n["x"]?.Value<double>() ?? 0;
                double y = n["y"]?.Value<double>() ?? 0;

                try
                {
                    Guid guid;
                    switch (kind)
                    {
                        case "codeblock":
                            guid = bridge.CreateCodeBlockNode(n["code"]?.ToString() ?? "", x, y);
                            break;
                        case "library":
                            guid = CreateLibraryNode(bridge, n["name"]?.ToString() ?? "", x, y);
                            break;
                        case "slider":
                            guid = CreateSlider(bridge, n, x, y);
                            break;
                        case "python":
                            guid = bridge.CreatePythonNode(
                                n["code"]?.ToString() ?? n["script"]?.ToString() ?? "",
                                n["engine"]?.ToString() ?? "CPython3", x, y);
                            break;
                        default:
                            errors.Add($"node '{callerId}': unknown kind '{kind}'");
                            continue;
                    }
                    idMap[callerId] = guid;
                }
                catch (Exception ex)
                {
                    errors.Add($"node '{callerId}' ({kind}): {ex.Message}");
                }
            }

            // ノードのポート情報を読み取る (結線先を caller が知るために返す)
            foreach (var kv in idMap)
            {
                var model = FindNode(bridge, kv.Value);
                resultNodes.Add(DescribeNode(kv.Key, model, kv.Value));
            }

            // 結線。fromPort/toPort は数値インデックス or ポート名(文字列)どちらでも可。
            foreach (var e in edges.OfType<JObject>())
            {
                var from = e["from"]?.ToString();
                var to = e["to"]?.ToString();

                if (from == null || to == null || !idMap.ContainsKey(from) || !idMap.ContainsKey(to))
                {
                    errors.Add($"edge {from} -> {to}: unknown node id");
                    continue;
                }

                var fromNode = FindNode(bridge, idMap[from]);
                var toNode = FindNode(bridge, idMap[to]);
                int fromPort = ResolvePort(fromNode, e["fromPort"], true, errors, $"edge {from}->{to} fromPort");
                int toPort = ResolvePort(toNode, e["toPort"], false, errors, $"edge {from}->{to} toPort");
                if (fromPort < 0 || toPort < 0) continue;

                try
                {
                    bridge.Connect(idMap[from], fromPort, idMap[to], toPort);
                }
                catch (Exception ex)
                {
                    errors.Add($"edge {from}:{fromPort} -> {to}:{toPort}: {ex.Message}");
                }
            }

            // layout:true なら Dynamo の自動レイアウトでノードの重なりを解消
            if (req["layout"]?.Value<bool>() == true)
                try { bridge.AutoLayout(); } catch (Exception ex) { errors.Add("layout: " + ex.Message); }

            var res = new JObject
            {
                ["ok"] = errors.Count == 0,
                ["nodes"] = resultNodes,
                ["errors"] = JArray.FromObject(errors),
            };
            return res.ToString(Formatting.None);
        }

        // ---------------------------------------------------------------
        // library node: CreateNodeCommand(string id, string name, ...) に委譲。
        // SearchModel / DynamoModel を使わないので 3.6 の API 差異に影響されない。
        // 生成後にワークスペースへ実在するか、DummyNode でないかを検証する。
        // ---------------------------------------------------------------
        private static Guid CreateLibraryNode(DynamoBridge bridge, string name, double x, double y)
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException("library node requires 'name'");

            var guid = bridge.CreateNodeByName(name, x, y);

            var model = bridge.CurrentWorkspace.Nodes.FirstOrDefault(nn => nn.GUID == guid);
            if (model == null)
                throw new InvalidOperationException(
                    $"Dynamo did not create a node for '{name}' (unknown node name?).");

            var tn = model.GetType().Name;
            if (tn.IndexOf("Dummy", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                bridge.DeleteNode(guid);
                throw new InvalidOperationException(
                    $"'{name}' resolved to a DummyNode (name not found in the loaded libraries).");
            }
            return guid;
        }

        // ---------------------------------------------------------------
        // slider: IntegerSlider / DoubleSlider を型名から生成し Min/Max/Value を設定
        // ---------------------------------------------------------------
        private static Guid CreateSlider(DynamoBridge bridge, JObject n, double x, double y)
        {
            bool integer = n["integer"]?.Value<bool>() ?? false;
            var typeName = integer
                ? "CoreNodeModels.Input.IntegerSlider"
                : "CoreNodeModels.Input.DoubleSlider";
            var guid = CreateLibraryNode(bridge, typeName, x, y);

            // Min / Max を先に、その後 Value (Dynamo は Value を Min..Max にクランプするため順序が重要)
            var ci = CultureInfo.InvariantCulture;
            if (n["min"] != null)
                TrySetValue(bridge, guid, "Min", n["min"].Value<double>().ToString(ci));
            if (n["max"] != null)
                TrySetValue(bridge, guid, "Max", n["max"].Value<double>().ToString(ci));
            if (n["value"] != null)
                TrySetValue(bridge, guid, "Value", n["value"].Value<double>().ToString(ci));

            return guid;
        }

        private static void TrySetValue(DynamoBridge bridge, Guid guid, string prop, string value)
        {
            try { bridge.SetNodeValue(guid, prop, value); } catch { /* best effort */ }
        }

        // ---------------------------------------------------------------
        // run: RunCancelCommand で強制再計算。
        // wait:true なら RefreshCompleted まで待つ (EventHandler 側がバックグラウンドで WaitRun)。
        // ---------------------------------------------------------------
        private static string Run(DynamoBridge bridge, JObject req)
        {
            bool wait = req["wait"]?.Value<bool>() ?? false;
            try
            {
                if (wait)
                {
                    bridge.BeginRunAndArm();
                    // "pending":true を見た EventHandler が別スレッドで WaitRun → _resetEvent.Set
                    return "{\"ok\":true,\"pending\":true}";
                }

                bridge.CommandExecutive.ExecuteCommand(
                    new DynamoModel.RunCancelCommand(false, false),
                    bridge.ExtensionUniqueId, bridge.ExtensionName);
                return "{\"ok\":true}";
            }
            catch (Exception ex)
            {
                return Fail("run failed: " + ex.Message);
            }
        }

        // ---------------------------------------------------------------
        // get_graph
        // ---------------------------------------------------------------
        private static string GetGraph(DynamoBridge bridge)
        {
            var ws = bridge.CurrentWorkspace;
            var nodesArr = new JArray();
            foreach (var node in ws.Nodes)
                nodesArr.Add(DescribeNode(null, node, node.GUID));

            var connArr = new JArray();
            foreach (var c in ws.Connectors)
            {
                connArr.Add(new JObject
                {
                    ["from"] = c.Start?.Owner?.GUID.ToString(),
                    ["fromPort"] = c.Start?.Index ?? -1,
                    ["to"] = c.End?.Owner?.GUID.ToString(),
                    ["toPort"] = c.End?.Index ?? -1,
                });
            }

            return new JObject
            {
                ["ok"] = true,
                ["nodes"] = nodesArr,
                ["connectors"] = connArr,
            }.ToString(Formatting.None);
        }

        // ---------------------------------------------------------------
        // get_node_value
        // ---------------------------------------------------------------
        private static string GetNodeValue(DynamoBridge bridge, JObject req)
        {
            var ws = bridge.CurrentWorkspace;
            var idStr = req["nodeId"]?.ToString();

            IEnumerable<NodeModel> targets;
            if (!string.IsNullOrWhiteSpace(idStr) && Guid.TryParse(idStr, out var g))
                targets = ws.Nodes.Where(n => n.GUID == g);
            else
                targets = ws.Nodes;

            var arr = new JArray();
            foreach (var node in targets)
            {
                var o = new JObject
                {
                    ["guid"] = node.GUID.ToString(),
                    ["name"] = node.Name,
                    ["state"] = node.State.ToString(),
                    ["value"] = StringifyMirror(GetCachedValue(node)),
                };
                var msgs = GetNodeMessages(node);
                if (msgs.Count > 0) o["messages"] = JArray.FromObject(msgs);
                arr.Add(o);
            }
            return new JObject { ["ok"] = true, ["values"] = arr }.ToString(Formatting.None);
        }

        // ---------------------------------------------------------------
        // clear
        // ---------------------------------------------------------------
        private static string Clear(DynamoBridge bridge, List<string> errors)
        {
            var guids = bridge.CurrentWorkspace.Nodes.Select(n => n.GUID).ToList();
            foreach (var g in guids)
            {
                try { bridge.DeleteNode(g); }
                catch (Exception ex) { errors.Add($"delete {g}: {ex.Message}"); }
            }
            return new JObject
            {
                ["ok"] = errors.Count == 0,
                ["deleted"] = guids.Count,
                ["errors"] = JArray.FromObject(errors),
            }.ToString(Formatting.None);
        }

        // ---------------------------------------------------------------
        // save (best effort)
        // ---------------------------------------------------------------
        private static string Save(DynamoBridge bridge, JObject req)
        {
            var path = req["path"]?.ToString();
            if (string.IsNullOrWhiteSpace(path))
                return Fail("save requires 'path'");

            var ws = bridge.CurrentWorkspace;
            var save = ws.GetType()
                .GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .FirstOrDefault(m => m.Name == "Save" && m.GetParameters().Length >= 1
                                     && m.GetParameters()[0].ParameterType == typeof(string));
            if (save == null)
                return Fail("Workspace.Save(string) not available in this build.");

            var pars = save.GetParameters();
            var args = new object[pars.Length];
            args[0] = path;
            for (int i = 1; i < pars.Length; i++)
                args[i] = GetDefault(pars[i].ParameterType);

            try
            {
                save.Invoke(ws, args);
                return new JObject { ["ok"] = true, ["path"] = path }.ToString(Formatting.None);
            }
            catch (Exception ex)
            {
                return Fail("save failed: " + (ex.InnerException?.Message ?? ex.Message));
            }
        }

        // ---------------------------------------------------------------
        // delete: 指定ノードのみ削除 ( nodeIds: string[] )
        // ---------------------------------------------------------------
        private static string Delete(DynamoBridge bridge, JObject req, List<string> errors)
        {
            var ids = (req["nodeIds"] as JArray)?.Select(t => t.ToString()).ToList()
                      ?? new List<string>();
            if (req["nodeId"] != null) ids.Add(req["nodeId"].ToString());

            int deleted = 0;
            foreach (var s in ids)
            {
                if (!Guid.TryParse(s, out var g)) { errors.Add($"bad guid: {s}"); continue; }
                try { bridge.DeleteNode(g); deleted++; }
                catch (Exception ex) { errors.Add($"delete {s}: {ex.Message}"); }
            }
            return new JObject
            {
                ["ok"] = errors.Count == 0,
                ["deleted"] = deleted,
                ["errors"] = JArray.FromObject(errors),
            }.ToString(Formatting.None);
        }

        // ---------------------------------------------------------------
        // move: 既存ノードの座標変更 ( nodeId, x, y )
        // ---------------------------------------------------------------
        private static string Move(DynamoBridge bridge, JObject req, List<string> errors)
        {
            if (!Guid.TryParse(req["nodeId"]?.ToString(), out var g))
                return Fail("move requires 'nodeId' (guid)");
            double x = req["x"]?.Value<double>() ?? 0;
            double y = req["y"]?.Value<double>() ?? 0;
            try { bridge.MoveNode(g, x, y); return "{\"ok\":true}"; }
            catch (Exception ex) { return Fail("move failed: " + ex.Message); }
        }

        // ---------------------------------------------------------------
        // connect / disconnect: 既存ノード間の配線 ( from,fromPort,to,toPort = guid/int )
        // ---------------------------------------------------------------
        private static string Connect(DynamoBridge bridge, JObject req, List<string> errors)
        {
            if (!Guid.TryParse(req["from"]?.ToString(), out var a) ||
                !Guid.TryParse(req["to"]?.ToString(), out var b))
                return Fail("connect requires 'from' and 'to' node guids");
            int fp = req["fromPort"]?.Value<int>() ?? 0;
            int tp = req["toPort"]?.Value<int>() ?? 0;
            try { bridge.Connect(a, fp, b, tp); return "{\"ok\":true}"; }
            catch (Exception ex) { return Fail("connect failed: " + ex.Message); }
        }

        private static string Disconnect(DynamoBridge bridge, JObject req, List<string> errors)
        {
            if (!Guid.TryParse(req["from"]?.ToString(), out var a) ||
                !Guid.TryParse(req["to"]?.ToString(), out var b))
                return Fail("disconnect requires 'from' and 'to' node guids");
            int fp = req["fromPort"]?.Value<int>() ?? 0;
            int tp = req["toPort"]?.Value<int>() ?? 0;
            try
            {
                var hit = bridge.Disconnect(a, fp, b, tp);
                return new JObject { ["ok"] = true, ["removed"] = hit }.ToString(Formatting.None);
            }
            catch (Exception ex) { return Fail("disconnect failed: " + ex.Message); }
        }

        // ---------------------------------------------------------------
        // set_lacing: nodeId + strategy (Auto/Longest/Shortest/CrossProduct/First/Disabled)
        // ---------------------------------------------------------------
        private static string SetLacing(DynamoBridge bridge, JObject req)
        {
            if (!Guid.TryParse(req["nodeId"]?.ToString(), out var g))
                return Fail("set_lacing requires 'nodeId' (guid)");
            var strat = req["strategy"]?.ToString();
            if (string.IsNullOrWhiteSpace(strat))
                return Fail("set_lacing requires 'strategy'");
            try { bridge.SetLacing(g, strat); return "{\"ok\":true}"; }
            catch (Exception ex) { return Fail("set_lacing failed: " + ex.Message); }
        }

        // ---------------------------------------------------------------
        // set_dropdown: nodeId + (index | item) — DSDropDownBase を populate して選択
        // ---------------------------------------------------------------
        private static string SetDropdown(DynamoBridge bridge, JObject req)
        {
            if (!Guid.TryParse(req["nodeId"]?.ToString(), out var g))
                return Fail("set_dropdown requires 'nodeId' (guid)");
            var node = bridge.CurrentWorkspace.Nodes.FirstOrDefault(n => n.GUID == g);
            if (node == null) return Fail("node not found");

            try
            {
                var nt = node.GetType();
                // PopulateItems() を呼ぶ
                nt.GetMethod("PopulateItems", BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null)
                  ?.Invoke(node, null);

                var items = nt.GetProperty("Items")?.GetValue(node) as System.Collections.IList;
                if (items == null) return Fail("node has no Items (not a dropdown?)");

                int index;
                if (req["index"] != null)
                {
                    index = req["index"].Value<int>();
                }
                else
                {
                    var want = req["item"]?.ToString();
                    index = -1;
                    for (int i = 0; i < items.Count; i++)
                    {
                        var name = items[i]?.GetType().GetProperty("Name")?.GetValue(items[i]) as string;
                        if (name == want) { index = i; break; }
                    }
                    if (index < 0)
                        return Fail($"dropdown item not found: {want}. available: " +
                            string.Join(", ", items.Cast<object>().Select(it =>
                                it?.GetType().GetProperty("Name")?.GetValue(it) as string)));
                }

                // SaveSelectedIndexImpl(int, IList) static でシリアライズ値を生成
                var save = FindStatic(node.GetType(), "SaveSelectedIndexImpl");
                var serialized = save != null
                    ? save.Invoke(null, new object[] { index, items }) as string
                    : index.ToString();

                bridge.SetNodeValue(g, "Value", serialized);
                return new JObject { ["ok"] = true, ["index"] = index, ["value"] = serialized }
                    .ToString(Formatting.None);
            }
            catch (Exception ex)
            {
                return Fail("set_dropdown failed: " + (ex.InnerException?.Message ?? ex.Message));
            }
        }

        private static MethodInfo FindStatic(Type t, string name)
        {
            for (var cur = t; cur != null; cur = cur.BaseType)
            {
                var m = cur.GetMethod(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                if (m != null) return m;
            }
            return null;
        }

        // ---------------------------------------------------------------
        // new / open
        // ---------------------------------------------------------------
        private static string NewGraph(DynamoBridge bridge)
        {
            try { bridge.ClearGraph(); return "{\"ok\":true}"; }
            catch (Exception ex) { return Fail("new failed: " + ex.Message); }
        }

        // ---------------------------------------------------------------
        // search: ノード名を部分一致検索 (Grasshopper のダブルクリック検索相当)。
        //   { "op":"search", "query":"wall", "max":15 }
        //   結果の creationName を build_graph の library.name にそのまま渡せる。
        // NodeSearchModel.Search(string, LuceneSearchUtility, CancellationToken) を反射。
        // 失敗時は SearchModel の NodeSearchElement コレクションを部分一致フィルタ。
        // ---------------------------------------------------------------
        private static string SearchNodes(DynamoBridge bridge, JObject req)
        {
            var query = req["query"]?.ToString() ?? req["q"]?.ToString();
            if (string.IsNullOrWhiteSpace(query))
                return Fail("search requires 'query'");
            int max = req["max"]?.Value<int>() ?? 20;

            var model = bridge.Model;
            if (model == null)
                return Fail("DynamoModel not available (library-node search needs it).");

            const BindingFlags F = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            var mt = model.GetType();
            var searchModel = mt.GetField("SearchModel", F)?.GetValue(model)
                              ?? mt.GetProperty("SearchModel", F)?.GetValue(model);
            if (searchModel == null) return Fail("SearchModel not found in this build.");

            IEnumerable hits = null;

            // 1) Lucene 検索
            try
            {
                var luceneProp = mt.GetProperty("LuceneUtility", F);
                var lucene = luceneProp?.GetValue(model);
                var luceneType = luceneProp?.PropertyType;
                if (luceneType != null)
                {
                    var searchMi = searchModel.GetType().GetMethod("Search", F, null,
                        new[] { typeof(string), luceneType, typeof(System.Threading.CancellationToken) }, null);
                    if (searchMi != null)
                        hits = searchMi.Invoke(searchModel,
                            new object[] { query, lucene, System.Threading.CancellationToken.None }) as IEnumerable;
                }
            }
            catch { hits = null; }

            // 2) フォールバック: SearchModel の NodeSearchElement コレクションを部分一致
            if (hits == null)
            {
                try
                {
                    var coll = searchModel.GetType()
                        .GetProperties(F)
                        .Where(p => typeof(IEnumerable).IsAssignableFrom(p.PropertyType)
                                    && p.PropertyType.IsGenericType
                                    && p.PropertyType.GetGenericArguments()[0].Name.Contains("NodeSearchElement"))
                        .Select(p => { try { return p.GetValue(searchModel) as IEnumerable; } catch { return null; } })
                        .FirstOrDefault(v => v != null);
                    if (coll != null)
                        hits = coll.Cast<object>().Where(e => Contains(e, "Name", query) || Contains(e, "FullName", query));
                }
                catch { }
            }

            if (hits == null) return Fail("search unavailable (no Lucene index and no fallback collection).");

            var arr = new JArray();
            int n = 0;
            foreach (var e in hits)
            {
                if (n++ >= max) break;
                var et = e.GetType();
                arr.Add(new JObject
                {
                    ["name"] = et.GetProperty("Name")?.GetValue(e) as string,
                    ["creationName"] = et.GetProperty("CreationName")?.GetValue(e) as string,
                    ["fullName"] = et.GetProperty("FullName")?.GetValue(e) as string,
                    ["category"] = et.GetProperty("FullCategoryName")?.GetValue(e) as string,
                    ["kind"] = et.GetProperty("ElementType")?.GetValue(e)?.ToString(),
                });
            }
            return new JObject { ["ok"] = true, ["query"] = query, ["results"] = arr }.ToString(Formatting.None);
        }

        private static bool Contains(object obj, string prop, string term)
        {
            var v = obj.GetType().GetProperty(prop)?.GetValue(obj) as string;
            return v != null && v.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static string SafeStr(object obj, string prop)
        {
            try { return obj.GetType().GetProperty(prop)?.GetValue(obj) as string; } catch { return null; }
        }

        // ---------------------------------------------------------------
        // node_info: 配置済みノードの逆引き。
        //   { "op":"node_info", "nodeId":"<guid>" }
        //   → name / type / category (リボンのどこにあるか) / creationName / description / ports
        // ---------------------------------------------------------------
        private static string NodeInfo(DynamoBridge bridge, JObject req)
        {
            if (!Guid.TryParse(req["nodeId"]?.ToString(), out var g))
                return Fail("node_info requires 'nodeId' (guid)");
            var node = bridge.CurrentWorkspace.Nodes.FirstOrDefault(n => n.GUID == g);
            if (node == null) return Fail("node not found");

            var o = DescribeNode(null, node, g);
            o["description"] = SafeStr(node, "Description");
            o["ok"] = true;
            return o.ToString(Formatting.None);
        }

        // ---------------------------------------------------------------
        // predict_ports: Code Block を生成せずにポート形状を予測。
        //   { "op":"predict_ports", "code":"line = Line.ByStartPointEndPoint(a,b);" }
        //   → { inPorts:[...], outPorts:[...] }  (build_graph の edge を名前指定で書ける)
        // ---------------------------------------------------------------
        private static string PredictPorts(DynamoBridge bridge, JObject req)
        {
            var code = req["code"]?.ToString();
            if (code == null) return Fail("predict_ports requires 'code'");
            try
            {
                var (inP, outP) = bridge.PredictCodeBlockPorts(code);
                return new JObject
                {
                    ["ok"] = true,
                    ["inPorts"] = JArray.FromObject(inP),
                    ["outPorts"] = JArray.FromObject(outP),
                }.ToString(Formatting.None);
            }
            catch (Exception ex) { return Fail("predict_ports failed: " + ex.Message); }
        }

        private static string Layout(DynamoBridge bridge)
        {
            try
            {
                var n = bridge.AutoLayout();
                return new JObject { ["ok"] = true, ["laidOut"] = n }.ToString(Formatting.None);
            }
            catch (Exception ex) { return Fail("layout failed: " + ex.Message); }
        }

        private static string OpenGraph(DynamoBridge bridge, JObject req)
        {
            var path = req["path"]?.ToString();
            if (string.IsNullOrWhiteSpace(path))
                return Fail("open requires 'path'");
            try { bridge.OpenGraph(path); return new JObject { ["ok"] = true, ["path"] = path }.ToString(Formatting.None); }
            catch (Exception ex) { return Fail("open failed: " + ex.Message); }
        }

        // ---------------------------------------------------------------
        // set_value: 任意ノードのプロパティを UpdateModelValueCommand で更新
        //   nodeId, property (既定 "Value"), value
        //   スライダー値・CBN の "Code"・input ノード等に使える
        // ---------------------------------------------------------------
        private static string SetValue(DynamoBridge bridge, JObject req)
        {
            if (!Guid.TryParse(req["nodeId"]?.ToString(), out var g))
                return Fail("set_value requires 'nodeId' (guid)");
            var prop = req["property"]?.ToString();
            if (string.IsNullOrEmpty(prop)) prop = "Value";
            var val = req["value"]?.ToString();
            if (val == null) return Fail("set_value requires 'value'");
            try { bridge.SetNodeValue(g, prop, val); return "{\"ok\":true}"; }
            catch (Exception ex) { return Fail("set_value failed: " + ex.Message); }
        }

        private static string SetRunMode(DynamoBridge bridge, JObject req)
        {
            var mode = req["mode"]?.ToString();
            if (string.IsNullOrWhiteSpace(mode))
                return Fail("set_run_mode requires 'mode' (Manual/Automatic/Periodic)");
            try
            {
                var applied = bridge.SetRunMode(mode);
                return new JObject { ["ok"] = true, ["mode"] = applied }.ToString(Formatting.None);
            }
            catch (Exception ex) { return Fail("set_run_mode failed: " + ex.Message); }
        }

        // ===============================================================
        // helpers
        // ===============================================================

        private static NodeModel FindNode(DynamoBridge bridge, Guid guid)
            => bridge.CurrentWorkspace.Nodes.FirstOrDefault(n => n.GUID == guid);

        /// <summary>ポート指定 (int index or string name) を index に解決。失敗時 -1 + errors に追加。</summary>
        private static int ResolvePort(NodeModel node, JToken tok, bool isOutput, List<string> errors, string ctx)
        {
            if (node == null) { errors.Add(ctx + ": node missing"); return -1; }
            var ports = isOutput ? node.OutPorts : node.InPorts;

            if (tok == null) return 0;
            if (tok.Type == JTokenType.Integer)
            {
                int i = tok.Value<int>();
                if (i < 0 || i >= ports.Count) { errors.Add($"{ctx}: index {i} out of range (0..{ports.Count - 1})"); return -1; }
                return i;
            }
            var name = tok.ToString();
            for (int i = 0; i < ports.Count; i++)
                if (string.Equals(ports[i].Name, name, StringComparison.OrdinalIgnoreCase))
                    return i;
            errors.Add($"{ctx}: port '{name}' not found. available: " +
                       string.Join(", ", ports.Select(p => p.Name)));
            return -1;
        }

        private static JObject DescribeNode(string callerId, NodeModel node, Guid guid)
        {
            var o = new JObject { ["guid"] = guid.ToString() };
            if (callerId != null) o["id"] = callerId;
            if (node == null) { o["missing"] = true; return o; }

            o["name"] = node.Name;
            o["type"] = node.GetType().Name;
            // 逆引き: このノードがライブラリ/リボンのどこにあるか
            var cat = SafeStr(node, "Category");
            if (!string.IsNullOrEmpty(cat)) o["category"] = cat;
            var cn = SafeStr(node, "CreationName");
            if (!string.IsNullOrEmpty(cn)) o["creationName"] = cn;

            var inPorts = new JArray();
            foreach (var p in node.InPorts)
                inPorts.Add(new JObject { ["index"] = p.Index, ["name"] = p.Name });
            var outPorts = new JArray();
            foreach (var p in node.OutPorts)
                outPorts.Add(new JObject { ["index"] = p.Index, ["name"] = p.Name });

            o["inPorts"] = inPorts;
            o["outPorts"] = outPorts;

            // 実行状態と警告/エラーメッセージ (NodeModel.State / NodeInfos は 3.6.2 で public)
            o["state"] = node.State.ToString();
            var msgs = GetNodeMessages(node);
            if (msgs.Count > 0) o["messages"] = JArray.FromObject(msgs);
            return o;
        }

        /// <summary>NodeModel.NodeInfos (List&lt;Info&gt;: Message + State) を文字列化。</summary>
        private static List<string> GetNodeMessages(NodeModel node)
        {
            var result = new List<string>();
            try
            {
                var infos = node.GetType().GetProperty("NodeInfos")?.GetValue(node) as IEnumerable;
                if (infos == null) return result;
                foreach (var info in infos)
                {
                    var it = info.GetType();
                    var msg = it.GetField("Message")?.GetValue(info) as string
                              ?? it.GetProperty("Message")?.GetValue(info) as string;
                    var st = (it.GetField("State")?.GetValue(info)
                              ?? it.GetProperty("State")?.GetValue(info))?.ToString();
                    if (!string.IsNullOrEmpty(msg))
                        result.Add(st != null ? $"[{st}] {msg}" : msg);
                }
            }
            catch { }
            return result;
        }

        private static object GetCachedValue(NodeModel node)
        {
            try { return node.GetType().GetProperty("CachedValue")?.GetValue(node); }
            catch { return null; }
        }

        /// <summary>
        /// ProtoCore.Mirror.MirrorData を構造化 JSON へ。
        /// collection は再帰、leaf は .Data を CLR オブジェクトとして取り出し、
        /// 数値/文字列/bool はそのまま、Point/Vector 等は {type,x,y,z}、それ以外は {type, text} に。
        /// (Dynamo 本体の render-package 生成と同じ IsCollection→GetElements→Data 経路)
        /// </summary>
        private static JToken StringifyMirror(object mirror)
        {
            if (mirror == null) return JValue.CreateNull();
            var t = mirror.GetType();
            try
            {
                if ((bool?)t.GetProperty("IsNull")?.GetValue(mirror) == true)
                    return JValue.CreateNull();

                if ((bool?)t.GetProperty("IsCollection")?.GetValue(mirror) == true)
                {
                    var elems = t.GetMethod("GetElements")?.Invoke(mirror, null) as IEnumerable;
                    var arr = new JArray();
                    if (elems != null)
                        foreach (var e in elems) arr.Add(StringifyMirror(e));
                    return arr;
                }

                var data = t.GetProperty("Data")?.GetValue(mirror);
                return LeafToJson(data)
                       ?? new JValue(t.GetProperty("StringData")?.GetValue(mirror) as string
                                     ?? mirror.ToString());
            }
            catch
            {
                return new JValue(mirror.ToString());
            }
        }

        private static JToken LeafToJson(object data)
        {
            if (data == null) return JValue.CreateNull();
            if (data is string s) return new JValue(s);
            if (data is bool || data is int || data is long || data is double || data is float
                || data is decimal || data is short || data is byte)
                return new JValue(data);

            var dt = data.GetType();

            // Point / Vector など X/Y/Z を持つものは座標で返す
            var xyz = TryXyz(data, dt);
            if (xyz != null) { xyz["type"] = dt.Name; return xyz; }

            // Line / Curve: StartPoint / EndPoint
            var sp = dt.GetProperty("StartPoint"); var ep = dt.GetProperty("EndPoint");
            if (sp != null && ep != null)
            {
                return new JObject
                {
                    ["type"] = dt.Name,
                    ["start"] = TryXyz(sp.GetValue(data), sp.PropertyType),
                    ["end"] = TryXyz(ep.GetValue(data), ep.PropertyType),
                };
            }

            // BoundingBox: MinPoint / MaxPoint
            var mn = dt.GetProperty("MinPoint"); var mx = dt.GetProperty("MaxPoint");
            if (mn != null && mx != null)
            {
                return new JObject
                {
                    ["type"] = dt.Name,
                    ["min"] = TryXyz(mn.GetValue(data), mn.PropertyType),
                    ["max"] = TryXyz(mx.GetValue(data), mx.PropertyType),
                };
            }

            return new JObject { ["type"] = dt.Name, ["text"] = data.ToString() };
        }

        private static JObject TryXyz(object obj, Type t)
        {
            if (obj == null) return null;
            var px = t.GetProperty("X"); var py = t.GetProperty("Y"); var pz = t.GetProperty("Z");
            if (px == null || py == null || pz == null || px.PropertyType != typeof(double)) return null;
            try
            {
                return new JObject
                {
                    ["x"] = (double)px.GetValue(obj),
                    ["y"] = (double)py.GetValue(obj),
                    ["z"] = (double)pz.GetValue(obj),
                };
            }
            catch { return null; }
        }

        private static object GetDefault(Type t)
            => t.IsValueType ? Activator.CreateInstance(t) : null;

        private static string Fail(string message)
            => new JObject
            {
                ["ok"] = false,
                ["errors"] = new JArray { message },
            }.ToString(Formatting.None);
    }
}
