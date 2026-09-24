// DynamoBridge.cs
// 配置場所: プロジェクト "DynamoMcpExtension" (Dynamo Extension 用)
//
// 役割:
//   Dynamo が起動しグラフが開かれたときに ReadyParams を保持し、Revit 側の commandset から
//   「今開いている Dynamo グラフ」にコマンドを送れるようにする橋渡し役。
//
//   commandset からのリフレクション境界は ExecuteBatch(string) の 1 メソッドのみ。
//   複数ノード生成・結線・値設定・実行・グラフ取得などの高レベル処理は GraphOps。
//
// 重要 (Round 3.5 修正):
//   ReadyParams.CurrentWorkspaceModel は dynamoModel.CurrentWorkspace を返す live getter。
//   .dyn を開く / 新規グラフにすると DynamoModel.CurrentWorkspace が差し替わるため、
//   Ready() 時に WorkspaceModel を1回キャッシュすると stale になる。
//   → ReadyParams 自体を保持し、リクエスト毎に CurrentWorkspace を解決する。
//
// 注意:
//   Dynamo for Revit / Civil3D はどちらも同じ Dynamo Core をホストするため、このクラスは共通。

using System;
using System.Linq;
using System.Reflection;
using Dynamo.Extensions;
using Dynamo.Graph.Nodes;
using Dynamo.Graph.Workspaces;
using Dynamo.Models;

namespace DynamoMcpExtension
{
    /// <summary>
    /// Dynamo の実行中インスタンスへの唯一の参照点。
    /// </summary>
    public sealed class DynamoBridge
    {
        private static readonly Lazy<DynamoBridge> _instance =
            new Lazy<DynamoBridge>(() => new DynamoBridge());

        public static DynamoBridge Instance => _instance.Value;

        private DynamoBridge() { }

        private ReadyParams _ready;
        private DynamoModel _model; // 反射で1回だけ解決してキャッシュ

        /// <summary>コマンド実行の入口 (毎回 ReadyParams から取得)。</summary>
        public ICommandExecutive CommandExecutive => _ready?.CommandExecutive;

        /// <summary>現在開いているワークスペース (毎回 ReadyParams から取得 = live)。</summary>
        public WorkspaceModel CurrentWorkspace => _ready?.CurrentWorkspaceModel as WorkspaceModel;

        /// <summary>
        /// DynamoModel 本体。ExtensionCommandExecutive の private field 'dynamoModel' から反射取得。
        /// 現状 GraphOps では未使用だが将来用に保持。取得できなければ null。
        /// </summary>
        public DynamoModel Model
        {
            get
            {
                if (_model != null) return _model;
                try
                {
                    var ce = CommandExecutive;
                    if (ce == null) return null;
                    foreach (var f in ce.GetType().GetFields(
                        BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public))
                    {
                        if (typeof(DynamoModel).IsAssignableFrom(f.FieldType) &&
                            f.GetValue(ce) is DynamoModel dm) { _model = dm; return _model; }
                    }
                }
                catch { }
                return null;
            }
            internal set { _model = value; }
        }

        public bool IsReady => _ready?.CurrentWorkspaceModel != null && _ready.CommandExecutive != null;

        public string ExtensionUniqueId { get; internal set; }
        public string ExtensionName { get; internal set; }

        internal void Attach(ReadyParams ready)
        {
            _ready = ready;
        }

        // 旧シグネチャ互換 (呼び出し側が残っていてもコンパイルを通すため)。
        internal void UpdateWorkspace(WorkspaceModel workspace) { /* no-op: CurrentWorkspace は live */ }

        internal void Detach()
        {
            _ready = null;
            _model = null;
        }

        // ==================================================================
        // commandset からのリフレクション境界 (この 1 メソッドのみ)
        // ==================================================================
        public string ExecuteBatch(string requestJson) => GraphOps.Execute(this, requestJson);

        // ==================================================================
        // 低レベルコマンドラッパー (GraphOps から使用)
        // ==================================================================

        private void Exec(DynamoModel.RecordableCommand cmd)
        {
            if (!IsReady) throw new InvalidOperationException("Dynamo not ready. Open a Home graph first.");
            CommandExecutive.ExecuteCommand(cmd, ExtensionUniqueId, ExtensionName);
        }

        public Guid CreateNode(NodeModel node, double x, double y)
        {
            Exec(new DynamoModel.CreateNodeCommand(node, x, y, true, false));
            return node.GUID;
        }

        /// <summary>ライブラリ/UIノードを検索名または完全型名から生成。</summary>
        public Guid CreateNodeByName(string nameOrType, double x, double y)
        {
            if (string.IsNullOrWhiteSpace(nameOrType))
                throw new ArgumentException("node name is required");
            var guid = Guid.NewGuid();
            Exec(new DynamoModel.CreateNodeCommand(guid.ToString(), nameOrType, x, y, true, false));
            return guid;
        }

        public void Connect(Guid startNodeId, int startPortIndex, Guid endNodeId, int endPortIndex)
        {
            Exec(new DynamoModel.MakeConnectionCommand(
                startNodeId, startPortIndex, PortType.Output,
                DynamoModel.MakeConnectionCommand.Mode.Begin));
            Exec(new DynamoModel.MakeConnectionCommand(
                endNodeId, endPortIndex, PortType.Input,
                DynamoModel.MakeConnectionCommand.Mode.End));
        }

        public void SetNodeValue(Guid nodeId, string propertyName, string value)
            => Exec(new DynamoModel.UpdateModelValueCommand(nodeId, propertyName, value));

        public void DeleteNode(Guid nodeId)
            => Exec(new DynamoModel.DeleteModelCommand(nodeId));

        /// <summary>キャンバス上でノードを移動 (NodeModel.UpdateValueCore が "Position" を処理)。</summary>
        public void MoveNode(Guid nodeId, double x, double y)
        {
            var ci = System.Globalization.CultureInfo.InvariantCulture;
            Exec(new DynamoModel.UpdateModelValueCommand(
                nodeId, "Position", x.ToString(ci) + ";" + y.ToString(ci)));
        }

        /// <summary>Lacing を設定 (値は LacingStrategy の enum 名: Auto/Longest/Shortest/CrossProduct/First/Disabled)。</summary>
        public void SetLacing(Guid nodeId, string strategy)
            => Exec(new DynamoModel.UpdateModelValueCommand(nodeId, "ArgumentLacing", strategy));

        /// <summary>指定した出力→入力ポート間のコネクタを1本削除。</summary>
        public bool Disconnect(Guid startNodeId, int startPortIndex, Guid endNodeId, int endPortIndex)
        {
            var ws = CurrentWorkspace;
            if (ws == null) throw new InvalidOperationException("Dynamo not ready.");

            var connector = ws.Connectors.FirstOrDefault(c =>
                c.Start != null && c.End != null &&
                c.Start.Owner.GUID == startNodeId && c.Start.Index == startPortIndex &&
                c.End.Owner.GUID == endNodeId && c.End.Index == endPortIndex);
            if (connector == null) return false;

            var connGuid = connector.GUID;
            Exec(new DynamoModel.DeleteModelCommand(connGuid));

            if (ws.Connectors.Any(c => c.GUID == connGuid))
            {
                var mi = typeof(WorkspaceModel).GetMethod("ClearConnector",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                if (mi == null)
                    throw new InvalidOperationException("WorkspaceModel.ClearConnector not found in this build.");
                mi.Invoke(ws, new object[] { connector });
            }
            return true;
        }

        /// <summary>Code Block Node を DesignScript コードから生成。</summary>
        public Guid CreateCodeBlockNode(string designScriptCode, double x, double y)
        {
            var homeWorkspace = CurrentWorkspace as HomeWorkspaceModel;
            if (homeWorkspace == null || homeWorkspace.EngineController == null)
                throw new InvalidOperationException(
                    "Current workspace has no EngineController (Custom Node editor instead of Home graph?).");

            var cbn = new CodeBlockNodeModel(
                designScriptCode, Guid.NewGuid(), x, y,
                homeWorkspace.EngineController.LibraryServices,
                CurrentWorkspace.ElementResolver);

            Exec(new DynamoModel.CreateNodeCommand(cbn, x, y, true, false));
            return cbn.GUID;
        }

        /// <summary>
        /// Code Block のポートを「生成せずに」予測する。
        /// workspace に追加しない probe 用 CodeBlockNodeModel を作り、
        /// Dynamo 自身の parser にポート形状を計算させる (独自 DesignScript 解析より堅い)。
        /// 戻り値: (inPortNames, outPortNames)。
        /// </summary>
        public (string[] In, string[] Out) PredictCodeBlockPorts(string code)
        {
            var home = CurrentWorkspace as HomeWorkspaceModel;
            if (home == null || home.EngineController == null)
                throw new InvalidOperationException("No EngineController (not a Home graph?).");

            var probe = new CodeBlockNodeModel(home.EngineController.LibraryServices);
            probe.SetCodeContent(code ?? "", CurrentWorkspace.ElementResolver);
            return (probe.InPorts.Select(p => p.Name).ToArray(),
                    probe.OutPorts.Select(p => p.Name).ToArray());
        }

        /// <summary>Python ノードを生成。スクリプト本文は "ScriptContent"、エンジンは "EngineName"。</summary>
        public Guid CreatePythonNode(string script, string engine, double x, double y)
        {
            var guid = CreateNodeByName("PythonNodeModels.PythonNode", x, y);
            if (!string.IsNullOrEmpty(engine))
                try { SetNodeValue(guid, "EngineName", engine); } catch { }
            if (script != null)
                SetNodeValue(guid, "ScriptContent", script);

            // UpdateModelValueCommand("ScriptContent", ...) は PythonNode を dirty にしない
            // (CodeBlockNode の "Code" と違い、値は変わるが再評価対象と見なされない)。
            // 明示的に dirty 化してマークしないと run/RunCancelCommand が
            // EvaluationCompleted(EvaluationTookPlace=false) = no-op を返し、二度と実行されない。
            try
            {
                var node = CurrentWorkspace?.Nodes?.FirstOrDefault(n => n.GUID == guid);
                node?.MarkNodeAsModified(forceExecute: true);
            }
            catch { }

            return guid;
        }

        /// <summary>現在の Home グラフを空にする (新規相当)。</summary>
        public void ClearGraph()
        {
            if (CurrentWorkspace is HomeWorkspaceModel home) home.Clear();
            else throw new InvalidOperationException("Current workspace is not a Home graph.");
        }

        /// <summary>既存 .dyn をセッションに開く (manual 実行モード強制)。</summary>
        public void OpenGraph(string path)
            => Exec(new DynamoModel.OpenFileCommand(path, true));

        // ------------------------------------------------------------------
        // run + 完了待ち。
        // UI スレッドをブロックすると Dynamo の評価完了 callback (idle/scheduler 上) が
        // 進まずデッドロックするため:
        //   BeginRunAndArm()  … UI スレッドで RefreshCompleted を1回購読 + RunCancelCommand 発行 → 即 return
        //   WaitRun(timeout)  … 別スレッドで RefreshCompleted を待つ
        // ------------------------------------------------------------------
        // run + 完了待ち (Round 3.8: EvaluationCompleted.EvaluationTookPlace で no-op を確定判定)
        //   clean graph の Run() は EvaluationCompleted(EvaluationTookPlace=false) を発火し
        //   RefreshCompleted を発火しない。よって:
        //     EvaluationCompleted(took=false) → no-op、即完了 (既存 CachedValue が最新)
        //     EvaluationCompleted(took=true)  → 実評価、RefreshCompleted まで待つ (値が更新される)
        private readonly System.Threading.ManualResetEventSlim _runDone =
            new System.Threading.ManualResetEventSlim(false);
        private EventInfo _refreshEvent, _completedEvent;
        private Delegate _refreshHandler, _completedHandler;
        private HomeWorkspaceModel _armedHome;
        private volatile string _runOutcome;   // "noop" / "evaluated" / "refreshed"
        private volatile Exception _runError;

        public void BeginRunAndArm()
        {
            var home = CurrentWorkspace as HomeWorkspaceModel;
            if (home == null) throw new InvalidOperationException("Current workspace is not a Home graph.");

            _runDone.Reset();
            _runOutcome = null;
            _runError = null;
            UnarmRun();

            _refreshEvent = typeof(HomeWorkspaceModel).GetEvent("RefreshCompleted");
            _completedEvent = typeof(HomeWorkspaceModel).GetEvent("EvaluationCompleted");
            if (_refreshEvent == null || _completedEvent == null)
                throw new InvalidOperationException("HomeWorkspaceModel evaluation events not found.");

            _armedHome = home;
            _refreshHandler = Delegate.CreateDelegate(_refreshEvent.EventHandlerType, this,
                typeof(DynamoBridge).GetMethod(nameof(OnRefreshCompleted), BindingFlags.Instance | BindingFlags.NonPublic));
            _completedHandler = Delegate.CreateDelegate(_completedEvent.EventHandlerType, this,
                typeof(DynamoBridge).GetMethod(nameof(OnEvaluationCompleted), BindingFlags.Instance | BindingFlags.NonPublic));
            _refreshEvent.AddEventHandler(home, _refreshHandler);
            _completedEvent.AddEventHandler(home, _completedHandler);

            Exec(new DynamoModel.RunCancelCommand(false, false));
        }

        private void UnarmRun()
        {
            if (_armedHome == null) return;
            try { _refreshEvent?.RemoveEventHandler(_armedHome, _refreshHandler); } catch { }
            try { _completedEvent?.RemoveEventHandler(_armedHome, _completedHandler); } catch { }
        }

        private void OnEvaluationCompleted(object sender, object args)
        {
            bool took = true;
            try
            {
                var t = args.GetType();
                took = (bool)(t.GetProperty("EvaluationTookPlace")?.GetValue(args) ?? true);
                _runError = t.GetProperty("Error")?.GetValue(args) as Exception;
            }
            catch { }

            if (!took)
            {
                _runOutcome = "noop";
                _runDone.Set();
                UnarmRun();
            }
            else
            {
                _runOutcome = "evaluated"; // RefreshCompleted を待つ
            }
        }

        private void OnRefreshCompleted(object sender, object args)
        {
            _runOutcome = "refreshed";
            _runDone.Set();
            UnarmRun();
        }

        /// <summary>完了を待つ。戻り値: 完了したか (no-op も true)。詳細は LastRunOutcome / LastRunError。</summary>
        public bool WaitRun(int timeoutMs)
        {
            var ok = _runDone.Wait(timeoutMs);
            UnarmRun();
            return ok;
        }

        public string LastRunOutcome => _runOutcome;
        public string LastRunError => _runError?.Message;

        /// <summary>
        /// Dynamo の「ノードのレイアウトをクリーンアップ」。
        /// まず DynamoViewModel.DoGraphAutoLayout を呼ぶ (View 側でノードサイズが確定してから
        /// 整列するので重なりが解消される)。VM が取れなければ model-level にフォールバック
        /// (こちらは NodeView 未描画だとノードが重なったままになることがある)。
        /// </summary>
        public int AutoLayout()
        {
            var ws = CurrentWorkspace;
            if (ws == null) throw new InvalidOperationException("Dynamo not ready.");

            var dvm = FindDynamoViewModel();
            if (dvm != null)
            {
                // ノード生成直後は WPF が NodeView を描画しておらず Width/Height=0 のため、
                // その場で整列すると全ノードが同じ位置に落ちる。
                // Dispatcher に Background 優先度で積み、描画パスの後に整列させる。
                Action run = () =>
                {
                    try
                    {
                        var m = dvm.GetType().GetMethod("DoGraphAutoLayout", new[] { typeof(object) });
                        if (m != null) { m.Invoke(dvm, new object[] { null }); return; }
                        var cmd = dvm.GetType().GetProperty("GraphAutoLayoutCommand")?.GetValue(dvm);
                        cmd?.GetType().GetMethod("Execute", new[] { typeof(object) })
                            ?.Invoke(cmd, new object[] { null });
                    }
                    catch { }
                };

                var disp = FindDynamoWindow()?.Dispatcher
                           ?? System.Windows.Application.Current?.Dispatcher;
                if (disp != null)
                {
                    disp.BeginInvoke(run, System.Windows.Threading.DispatcherPriority.Background);
                    return ws.Nodes.Count();
                }
                run(); // dispatcher が取れなければ同期
                return ws.Nodes.Count();
            }

            // フォールバック: model-level (internal)
            var t = typeof(WorkspaceModel).Assembly
                .GetType("Dynamo.Graph.Workspaces.LayoutExtensions");
            var mi = t?.GetMethod("DoGraphAutoLayout",
                BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public,
                null, new[] { typeof(WorkspaceModel) }, null);
            if (mi == null) throw new InvalidOperationException("No auto-layout path available.");
            mi.Invoke(null, new object[] { ws });
            return ws.Nodes.Count();
        }

        /// <summary>WPF の Application.Current から Dynamo のウィンドウを返す。</summary>
        private static System.Windows.Window FindDynamoWindow()
        {
            try
            {
                var app = System.Windows.Application.Current;
                if (app == null) return null;
                foreach (System.Windows.Window w in app.Windows)
                    if (w?.GetType().FullName == "Dynamo.Controls.DynamoView")
                        return w;
            }
            catch { }
            return null;
        }

        /// <summary>Dynamo のウィンドウの DataContext (DynamoViewModel) を返す。</summary>
        private static object FindDynamoViewModel() => FindDynamoWindow()?.DataContext;

        /// <summary>実行モードを設定 ("Manual" / "Automatic" / "Periodic")。</summary>
        public string SetRunMode(string mode)
        {
            var home = CurrentWorkspace as HomeWorkspaceModel;
            if (home == null) throw new InvalidOperationException("Current workspace is not a Home graph.");

            var runTypeProp = home.RunSettings.GetType().GetProperty("RunType");
            var value = Enum.Parse(runTypeProp.PropertyType, mode, true);
            runTypeProp.SetValue(home.RunSettings, value);
            return runTypeProp.GetValue(home.RunSettings)?.ToString();
        }

        public string GetRunMode()
        {
            var home = CurrentWorkspace as HomeWorkspaceModel;
            if (home == null) return null;
            return home.RunSettings.GetType().GetProperty("RunType")?.GetValue(home.RunSettings)?.ToString();
        }
    }
}
