# Dynamo Core API 調査（Round 3）+ Civil 3D 移植の見通し

**方法:** `DynamoCore.dll` を両バージョンでリフレクション実測（外部AIは gemini-cli 未導入・codex タイムアウトのため不使用）
**対象:** Revit 2026 = **Dynamo Core 3.6.2.11575** / Civil 3D 2026 = **Dynamo Core 3.4.1.7055**
**日付:** 2026-09-04

---

## 最重要の結論

### 1. 3.4.1（C3D）と 3.6.2（Revit）の API はほぼ完全に同一

実測した型・メンバ・enum・コマンドの **99% が両バージョンで一致**。差分は 1 つだけ:

| | 3.6.2 (Revit) | 3.4.1 (C3D) |
|---|---|---|
| `CreateNodeCommand` overloads | 5個（`…, bool isTransient` 付きあり） | 4個（`isTransient` なし） |

我々が使う `CreateNodeCommand(string nodeId, string nodeName, double x, double y, bool defaultPosition, bool transformCoordinates)` は**両方に存在**。

**→ `DynamoBridge` / `GraphOps` のコードは C3D 用 DLL で再ビルドするだけでそのまま動くはず。** ロジック変更不要。

### 2. `DynamoModel.ExecuteCommand(RecordableCommand)` が public（引数1個）

今 `ICommandExecutive.ExecuteCommand(cmd, extId, extName)` を使っているが、`DynamoModel` を掴めば
`dynamoModel.ExecuteCommand(cmd)` だけでよい。extId/extName 不要。

### 3. 拡張が自前でソケットを持てる（ホスト非依存の道）

- `DynamoModel.Scheduler` (public) → `DynamoScheduler.ScheduleForExecution(AsyncTask)` (public)
- `DelegateBasedAsyncTask(IScheduler scheduler, Action action)` (ctor 確認)

つまり拡張内で `TcpListener` を立て、受信ごとに
`scheduler.ScheduleForExecution(new DelegateBasedAsyncTask(scheduler, () => GraphOps.Execute(...)))`
で Dynamo スケジューラスレッドへ marshaling できる。**Revit の `IExternalEventHandler` も AutoCAD 側の仕組みも不要**になる可能性が高い（グラフ編集のみなら）。

---

## パート A: Dynamo Core API（実測）

### A-1. コードブロックのポート確定ルール

`CodeBlockNodeModel` は `DynamoCore.dll` の `Dynamo.Graph.Nodes.CodeBlockNodeModel`（`CoreNodeModels` ではない）。
メソッド本体はリフレクションで読めないため、既知の DesignScript 仕様（DynamoDS ソース `CodeBlockNodeModel.cs` の `ProcessCode` / `SetInputPorts` / `SetOutputPorts`）に基づく:

- **入力ポート** = コード内で**定義されていない自由変数**（右辺にのみ現れる識別子）。出現順。
- **出力ポート** = **代入された変数**それぞれ + 末尾が無代入の式ならその式。宣言順。
  - `a = 1; b = a*2;` → 出力ポート `a`, `b`（2個）、入力ポートなし
  - `x*x;`（無代入式のみ）→ 入力ポート `x`、出力ポート1個（名前は式依存、実測で `function` になった）
  - `r = radius; pts = Point.ByCoordinates(r,0,0);` → 入力 `radius`、出力 `r`,`pts`
- コメント行・空行はポートに影響しないが、**行番号がずれると出力ポートの縦位置がずれる**。
- **実務上の対策:** MCP 側は「作成 → レスポンスの `inPorts`/`outPorts` を見る → 結線」を1コールでやる（現状の実装通り）。事前予測は「1文＝1出力ポート、自由変数＝入力ポート」の近似で十分。

### A-2. Lacing / Replication

- `NodeModel.ArgumentLacing` : public prop、型 `LacingStrategy`
- enum **`LacingStrategy`（両バージョン同一）**: `Disabled, First, Shortest, Longest, CrossProduct, Auto`
- `UpdateValueCore` は `protected`。コマンド経由でしか変えられない。
  DynamoDS ソース上 `NodeModel.UpdateValueCore` が処理する property 名は **`"ArgumentLacing"`**、値は enum 名の文字列（例 `"Longest"`）。
  ```csharp
  executive.ExecuteCommand(
      new DynamoModel.UpdateModelValueCommand(guid, "ArgumentLacing", "Longest"),
      extId, extName);
  ```
- list-@-level（`UseLevels`/`Level`）は `PortModel` 側（`UpdateValueCore` の `"UseLevels"` / `"Level"` + ポート指定）。要実機確認。

### A-3. 実行制御と完了待ち

- `HomeWorkspaceModel.RunSettings` : public prop
  - `RunSettings.RunType` : public prop、enum **`RunType`（両同一）**: `Manual, Automatic, Periodic`
  - `RunSettings.RunPeriod` : public int
- **`HomeWorkspaceModel.Run()` : public()** ← 強制再計算はこれ（`RunCancelCommand(false,false)` より直接的）
- `EvaluationCompleted` / `EvaluationStarted` : イベント（バッキングフィールドは private だがイベントアクセサは公開）。
  `homeWorkspace.EvaluationCompleted += (s,e) => { ... };` で完了を捕捉。
- **同期待ち**は「`Run()` 前に `ManualResetEventSlim` を張り、`EvaluationCompleted` で `Set()`、`Wait(timeout)`」。
  ただし `EvaluationCompleted` はスケジューラスレッドで発火するので、拡張がスケジューラ経由で動いているならデッドロックに注意。
- 「今 evaluate 中か」= `RunSettings` 経由では直接取れない。`EvaluationStarted`/`Completed` のペアで自前フラグ管理。

### A-4. MirrorData → 構造化データ

`ProtoCore.Mirror.MirrorData`（両バージョン同一）:
`IsCollection` / `GetElements()` / `Data` / `IsNull` / `IsPointer` / `IsFunction` / `Class` / `StringData` すべて public。

- **正攻法**: `IsNull` → `IsCollection` なら `GetElements()` 再帰 → leaf は `.Data`（CLR オブジェクト）
- leaf の CLR 型別取り出し（`Autodesk.DesignScript.Geometry.*`、`ProtoGeometry.dll` を参照）:
  - `Point` → `.X .Y .Z`
  - `Vector` → `.X .Y .Z`
  - `Line` / `Curve` → `.StartPoint` `.EndPoint`（Point）、`.PointAtParameter(t)`、`.Length`
  - `NurbsCurve` → `.ControlPoints()` (Point[])、`.Degree`
  - `Surface` → `.PointAtParameter(u,v)`、`.NormalAtParameter(u,v)`、`.PerimeterCurves()`
  - `Solid` → `.Vertices` `.Edges` `.Faces`、`.Volume`、`.Centroid()`
  - `Mesh` → `.VertexPositions` (Point[])、`.FaceIndices`
  - `BoundingBox` → `.MinPoint` `.MaxPoint`
  - 共通: `.BoundingBox`、`Geometry` は `IDisposable` なので**取り出したら即 dispose**しないと Dynamo のジオメトリ管理と競合する場合あり
- Civil3D wrapper（`Civil3DNodes.dll` の `Alignment` / `Surface` / `CogoPoint` 等）:
  - 多くが `.InternalObjectId`（`ObjectId`）や `.Handle` を持つ。幾何は host ノード固有メソッド
- **`.Data` が `StackValue` に見える場合**: それは collection の `.Data` を読んだとき。`IsCollection` を先に見て `GetElements()` を使えば起きない。生 `StackValue` の Heap を自前で解くのは ProtoCore 内部（RuntimeCore / Heap / CLR marshaler）に密結合するので**やらない**。

### A-5. ドロップダウン / 選択ノード

`CoreNodeModels.DSDropDownBase`（`CoreNodeModels.dll`、`.Input.` **ではない**）:

| メンバ | アクセス |
|---|---|
| `PopulateItems()` | **public ()** |
| `PopulateItemsCore(string)` | protected → SelectionState |
| `Items` | public `ObservableCollection<DynamoDropDownItem>` |
| `SelectedIndex` | public int |
| `UpdateValueCore` | protected |

- 選択は `UpdateModelValueCommand(guid, "Value", "<index>")`（property 名は **`"Value"`**、`"SelectedIndex"` ではない）
- 保存文字列形式 `"<index>:<name>"` も parser が受ける（名前で選び直せる）
- **Revit dropdown**（`FamilyTypes` / `Categories` / `Levels` / `Views` — DynamoRevit の `RevitDropDownBase`）:
  - `Categories` は `EnumBase<BuiltInCategory>`（ドキュメント不要）
  - `FamilyTypes` 等ドキュメント内容を列挙するものは `PopulateItemsCore` 内で `DocumentManager.Instance.CurrentDBDocument` を参照 → **有効な Revit ドキュメント context 上（UIスレッド）で `PopulateItems()` を呼ぶ必要あり**
  - タイミング: ドキュメントオープン後。生成直後の constructor 内 populate は空になることがある
- `SelectionBase<TSelection,TResult>`（「Select Model Element」等ピック式）:
  - プログラムからの対象指定は基本不可（UI ピック前提）
  - **代替**: コードブロックで `ElementSelector.ByElementId(id)` / Civil3D なら `Selection.ByObjectId` 相当

### A-6. ノード名カタログ（検索インデックスなしで）

- `EngineController.LibraryServices`（public field）:
  - `GetAllFunctionDescriptors(string libraryPath)` : **public** — ライブラリパス指定で `FunctionDescriptor` 列挙
  - `GetAllFunctionGroups()` : internal（reflection）
  - `ImportedLibraries` : public `IEnumerable<string>` — 読み込み済みライブラリパス一覧
  - `BuiltinFunctionGroups` : public
- 手順: `ImportedLibraries` を回して各パスを `GetAllFunctionDescriptors(path)` に渡す → 全 ZeroTouch 関数の `FunctionDescriptor`（`MangledName`, `QualifiedName`, `FunctionName`, `Parameters`）
- `CreateNodeCommand` に渡す正規名:
  - **ZeroTouch** → `FunctionDescriptor.MangledName`（曖昧なら）、短縮名（`Circle.ByCenterPointRadius`）でも `LibraryServices.GetFunctionDescriptor(name)` が解決できれば可
  - **NodeModel 系パッケージノード** → CLR `Type.ToString()`
  - **カスタム `.dyf`** → `CustomNodeInfo.FunctionId` の GUID 文字列（`DynamoModel.CustomNodeManager` から取得）
- 起動時カタログ: `{displayName, creationName, kind}` を dict 化して MCP から「名前検証」「候補提示」に使う

### A-7. .dyn を開く / 新規 Home ワークスペース

- **既存 .dyn を開く**（両バージョン同一）:
  - `DynamoModel.OpenFileFromPath(string path, bool forceManualMode)` : **public**
  - コマンド版: `OpenFileCommand(string filePath, bool forceManualExecutionMode[, bool isTemplate])`
  - JSON 直接: `OpenFileFromJsonCommand(string fileContents, bool forceManualExecutionMode)`
- **新規 Home**: `DynamoModel.AddHomeWorkspace()` : public()
- 開いた後、拡張の `CurrentWorkspaceModel` 参照は **`ReadyParams.CurrentWorkspaceChanged` イベントで更新される**（現状の `DynamoBridge.UpdateWorkspace` でカバー済み）。`DynamoModel.CurrentWorkspace`（public）でも取れる。

### A-8. Python ノード

- 型: `PythonNodeModels.PythonNode`（`PythonNodeModels.dll`）、parameterless ctor あり
- `CreateNodeCommand` の name → `"PythonNodeModels.PythonNode"`（CLR 型名）
- `PythonNode.Script` : **public string**（スクリプト本文）
- `PythonNode.EngineName` : **public string**（`Engine` プロパティは無い）
- コマンドで設定: `UpdateModelValueCommand(guid, "ScriptContent", "<code>")` が DynamoDS の property 名（要実機確認）、エンジンは `"EngineName"` に `"CPython3"`
- 3.4 / 3.6 とも `EngineName` string 方式。既定エンジンは 3.x では CPython3 系（IronPython2 は別パッケージ化の流れ）

### A-9. EngineController の警告（詳細デバッグ）

- `GetBuildWarnings()` / `GetRuntimeWarnings()` / `GetRuntimeInfos()` : **internal**（両バージョン）、戻り `IDictionary<Guid, List<WarningEntry>>`
- reflection で呼ぶ。`WarningEntry`（`ProtoCore.BuildData` / `ProtoCore.Runtime`）は `Message`, `Line`, `Column`, `ID` 等
- dict の Guid は基本 `NodeModel.GUID` に対応するが、AST 生成識別子など常に1対1ではない
- **公開 API で足りるなら `NodeModel.State` + `NodeModel.NodeInfos`（both public）を正とする**。EngineController 版はディープデバッグ用

---

## パート B: Civil 3D 移植

### B-1. 拡張ロード機構

- DynamoCore が同じアーキテクチャ（3.4.1 も `IExtension` / `ReadyParams` / `ICommandExecutive` / package 形式が 3.6.2 と同一）なので、**`pkg.json` + `extra\*_ExtensionDefinition.xml` はそのまま使える見込み**
- パッケージ配置先: `%AppData%\Autodesk\C3D 2026\Dynamo\3.4\packages\DynamoMcpExtension\`
  （Revit は `%AppData%\Dynamo\Dynamo Revit\3.6\packages\`）
- 参照ビルド: `C:\Program Files\Autodesk\AutoCAD 2026\C3D\Dynamo\Core\` の
  `DynamoCore.dll` `DynamoServices.dll` `ProtoCore.dll` + `nodes\CoreNodeModels.dll` + WPF 用 `DynamoCoreWpf.dll`
- **要検証**: 3.4.1 で `IViewExtensionSource` 経由のメニュー追加が効くか（3.6 では死んでいた）→ どのみち使っていないので影響小

### B-2. ソケット + スレッド marshaling（本題）

| 案 | 難易度 | 安定性 | 保守性 | Revit版との共通度 |
|---|---|---|---|---|
| **1. AutoCAD .NET プラグイン別途** | 中 | 高（実績パターン） | 中（ホスト毎に別コード） | 低（Revit の commandset と別物） |
| **2. 拡張が自前でソケット+スケジューラ marshaling** | 中〜高（初回のみ） | 中（要検証だが筋は良い） | **高（1コードベース）** | **高（Revit の IExternalEventHandler も廃止可能）** |
| 3. 既存 AutoCAD MCP プラグインに相乗り | 低 | 依存先次第 | 低 | 低 |

**推奨: 案2。**

根拠:
- `DynamoModel.Scheduler`（public）+ `DelegateBasedAsyncTask(IScheduler, Action)`（ctor 確認済）で、
  ソケット受信スレッド → Dynamo スケジューラスレッドへ hop できる
- グラフ**編集**（ノード生成・結線・値設定）は Dynamo モデル操作のみなので、Revit/AutoCAD の API context は不要 → スケジューラスレッドで十分
- `DynamoModel.ExecuteCommand(RecordableCommand)`（public 単一引数）で `extId/extName` すら不要
- Revit 版もこれに寄せれば **commandset / EventHandler / plugin の大半が不要**になり、`DynamoMcpExtension.dll` 1個（＋バージョン別ビルド）で完結

**注意点 / 要実機検証:**
- グラフ**実行**（`Run()`）で Revit/AutoCAD API を触るノード（`Wall.ByCurve`, `Civil3D` 系）が走る場合、
  そのノード内部が要求する host トランザクション / `DocumentLock` はノード実装側が張る。
  ただし「外部トリガで Run したとき MdiActiveDocument が無い / アクティブでない」ケースの検出は必要（B-4）
- `EvaluationCompleted` がスケジューラスレッド発火 → 同スレッドで `Wait` するとデッドロック。
  完了待ちは別スレッド or タイムアウト付きポーリングに

案1を採る場合の AutoCAD 側 hop 手段:
- **`DocumentCollection.ExecuteInCommandContextAsync`**（モダン、推奨）
- 旧: `Application.Idle` イベントで1回だけ実行、`Document.SendStringToExecute`（コマンド文字列経由・遅い）
- `DocumentLock` はグラフ編集だけなら不要、グラフが DWG に書くなら該当ノードが処理

### B-3. 3.4.1 vs 3.6.2 API 差分（パートA全項目）

**実測の結論: パートAで挙げた型・メソッド・enum・コマンドはすべて 3.4.1 に存在し、シグネチャも同一。**
唯一の差は A の `CreateNodeCommand` の `isTransient` 付き overload が 3.4.1 に無いこと（未使用なので無影響）。

一致確認済み: `LacingStrategy` / `ElementState` / `RunType` enum、`NodeModel.State`/`NodeInfos`/`ArgumentLacing`/`CachedValue`、
`HomeWorkspaceModel.Run`/`RunSettings`/`EvaluationCompleted`、`MirrorData` 全メンバ、
`WorkspaceModel.Save(string,bool,EngineController)`/`ClearConnector`(internal)/`GetModelInternal`、
`DSDropDownBase.PopulateItems`(public)/`SelectedIndex`、`PythonNode.Script`/`EngineName`、
`DynamoModel.Scheduler`/`OpenFileFromPath`/`AddHomeWorkspace`/`ExecuteCommand`、
`DelegateBasedAsyncTask` ctor、`ICommandExecutive.ExecuteCommand`、`ReadyParams` 全メンバ、
`EngineController.GetBuildWarnings/GetRuntimeWarnings`(internal)。

### B-4. Civil 3D ドキュメント / トランザクションの落とし穴

- Dynamo for Civil 3D のノードが DWG に書くときは `Autodesk.AutoCAD.ApplicationServices.Document` の
  `TransactionManager` / `Database.TransactionManager` をノード側が使う
- 外部トリガ実行時の防御:
  - `Application.DocumentManager.MdiActiveDocument == null` → 「アクティブ DWG なし」で早期エラー
  - `DocumentLock` は `doc.LockDocument()` で取得、取れなければ「他コマンド実行中」
- Revit の「Transaction 必須」問題（`send_code_to_revit` で既知）に相当するのは、
  Civil3D では「document がロックできない / read-only モード」

---

---

## Round 3.5 — 外部レビューによる訂正（2026-09-04、local DLL で再検証済み）

別AIの詳細レビューを受け、以下を 3.6.2 の DLL で裏取りして訂正:

### 訂正1: 実行完了待ちは `EvaluationCompleted` ではなく **`RefreshCompleted`**
`HomeWorkspaceModel` のイベントは `EvaluationStarted` / `EvaluationCompleted` / **`RefreshCompleted`**（3つとも public、実測確認）。
実行順は `EvaluationCompleted` → 各ノード `RequestValueUpdate` → `RefreshCompleted`。
**`CachedValue` を確実に読むなら `RefreshCompleted` を待つ。**
- **ホストスレッド（Revit ExternalEvent / AutoCAD context）を `.Wait()` / `.Result` で塞がない** → デッドロック。
  完了は `TaskCompletionSource` にして socket worker 側で await、ホスト callback は即 return。
- 「実行中か」は internal `GraphRunInProgress` を反射で読むより、`EvaluationStarted`/`RefreshCompleted` で `Interlocked` フラグ自前管理。
- `HomeWorkspaceModel.Run()` public。`RunSettings.RunType` / `RunEnabled` / `RunPeriod` public setter。

### 訂正2: `DynamoBridge` は `WorkspaceModel` をキャッシュせず `ReadyParams` を保持する（バグ）
`ReadyParams.CurrentWorkspaceModel` は `dynamoModel.CurrentWorkspace` を返す live getter。
`.dyn` を開くと `DynamoModel.CurrentWorkspace` が差し替わるので、`Ready()` で1回掴んで保持すると stale になる。
→ `ReadyParams` 自体を保持し、リクエスト毎に `((WorkspaceModel)readyParams.CurrentWorkspaceModel)` で取得。
`CurrentWorkspaceChanged` イベント依存もやめてよい。
- `DynamoModel` は `ExtensionCommandExecutive` の private field `dynamoModel` から反射取得できる（実測確認、既存 `TryResolveDynamoModel` の経路）。
- 「新規/クリア」は `HomeWorkspaceModel.Clear()`（public 実測確認）。`.dyn` を開くのは `OpenFileCommand(string, bool forceManualExecutionMode)`（manual 強制推奨）。

### 訂正3: Python ノードのスクリプト本文プロパティは **`"ScriptContent"`**（`"Script"` ではない）
public プロパティは `Script` / `EngineName` だが、`UpdateModelValueCommand` に渡す property 名は **`"ScriptContent"`**
（DLL に `ScriptContentSaved` フィールドあり＝裏付け）。エンジンは `"EngineName"` に `"CPython3"`。
生成名は `"PythonNodeModels.PythonNode"`。

### 訂正4: List@Level はノードではなく **入力ポート単位**
`PortModel.UseLevels` / `Level` / `KeepListStructure` すべて public prop（実測確認）。
`UpdateModelValueCommand` 経由の設定は未確認 → `node.InPorts[i].UseLevels = true; ...` の直接 setter + その後
`node.MarkNodeAsModified(true)`（public 実測確認）で dirty 化。

### 訂正5: Lacing はそのまま `UpdateModelValueCommand(guid,"ArgumentLacing","<enum名>")`（変更なし、確認済）

### 訂正6: ドロップダウンのシリアライズ値は `DSDropDownBase.SaveSelectedIndexImpl(int index, IList items)`（static public 実測確認）
`"index:name"` 形式を生成。逆は `ParseSelectedIndexImpl(string, IList)`（static public）。

### 訂正7: ノードカタログ — `GetAllFunctionDescriptors` は **引数に qualified 名が要る**（無引数版なし）
`LibraryServices.GetAllFunctionDescriptors(string qualifiedName)` public。
`GetAllFunctionGroups()` は internal（反射）。`ImportedLibraries` / `BuiltinFunctionGroups` public。
NodeModel 系はロード済みアセンブリから `NodeModel` サブクラスを反射列挙。カタログ構築は `Startup()` ではなく `Ready()` 後。

### 訂正8（重要）: Civil 3D の marshaling は **案1（AutoCAD .NET プラグイン + command context）を推奨** に変更
外部レビューの指摘が妥当。Round 3 では「案2（拡張が自前ソケット + Dynamo scheduler）で Revit/C3D 共通化」を推したが:
- **Dynamo scheduler スレッド ≠ ホストの有効な API コンテキスト**。`DelegateBasedAsyncTask` はエンジンのスケジューリング抽象であって UI dispatcher ではない
- Automatic run やホストノード（`Wall.ByCurve` / Civil 系）がその経路で走ると、Revit トランザクション / AutoCAD document context の前提が崩れる
- **Revit 版の `IExternalEventHandler` も残す**。ホスト毎に dispatcher を分け、その上の `DynamoBridge` だけ共通化する

新推奨アーキ:
```
RevitMcpHost.dll   … ExternalEvent dispatcher        ┐
CivilMcpHost.dll   … AutoCAD command-context dispatcher├─ 各ホスト
                       (DocumentCollection.ExecuteInCommandContextAsync 優先、
                        Application.Idle は次点、SendStringToExecute は非推奨)
DynamoMcp.Dynamo36 … Dynamo 3.6.2 参照の薄い adapter   ┐
DynamoMcp.Dynamo34 … Dynamo 3.4.1 参照の薄い adapter   ├─ バージョン別
DynamoMcp.Protocol … DTO / dispatcher interface のみ   ┘ 共通
```
- グラフ**編集のみ**なら `DocumentLock` 不要。bridge が DWG を直接書く時だけ短い `LockDocument()` + `Transaction` scope
- グラフ構築中は `RunType.Manual` に固定 → 最後に1回だけ明示 Run（Civil ノードの途中実行による DB 副作用を防ぐ）
- Civil リクエスト毎に `Application.DocumentManager.MdiActiveDocument` を取り直す（singleton 固定しない）

### 実機での最初の一手（Civil 3D）
`CreateNodeCommand` の6引数 overload、`PortModel` の level members、Civil wrapper の ObjectId/handle member、
package `extra` からの `IExtension` ロード可否、AutoCAD command-context API の正確な signature を
**Civil 3D 2026 プロセス内で反射 dump** し、`dynamo-api-capabilities-3.4.1.7055.json` に固定する。

---

## 実装ロードマップ（Revit 版からの差分作業）

1. **`DynamoMcpExtension` を「自前ソケット + スケジューラ marshaling」化（案2）**
   - 拡張内に `TcpListener`（ポート設定可）
   - `ReadyParams` から `DynamoModel` を取得（`StartupParams` 反射 or `CommandExecutive` 内部フィールド — 既存の `TryResolveDynamoModel` を流用）
   - 受信 → `scheduler.ScheduleForExecution(new DelegateBasedAsyncTask(scheduler, () => resp = GraphOps.Execute(...)))` → 完了待ち → 返信
   - `GraphOps` は `DynamoModel.ExecuteCommand(cmd)` に切替（extId/extName 廃止）
2. **Revit 版もこれに移行** → `RevitMCPPlugin` / `commandset` の Dynamo 部分・`DynamoOpCommand` / `DynamoOpEventHandler` を廃止。node server は「Revit の :8080」ではなく「拡張の :ポート」に直接繋ぐ
3. **C3D 用ビルド構成追加**
   - `.csproj` に `Debug-C3D` 構成、HintPath を `AutoCAD 2026\C3D\Dynamo\Core\` に切替、`net8.0` 維持
   - 出力を `%AppData%\Autodesk\C3D 2026\Dynamo\3.4\packages\DynamoMcpExtension\bin\` へコピー
4. **新 op の追加（今回の調査で API 確定済み）**
   - `set_lacing`（`UpdateModelValueCommand "ArgumentLacing"`）
   - `run` を `HomeWorkspaceModel.Run()` + `EvaluationCompleted` 待ちに強化
   - `open` / `new`（`OpenFileFromPath` / `AddHomeWorkspace`）
   - `python` ノード種別（`PythonNodeModels.PythonNode` + `Script` / `EngineName`）
   - `get_node_value` のジオメトリ展開（Curve/Surface/Solid → A-4 の型別メソッド）
   - ノード名カタログ（`LibraryServices.ImportedLibraries` + `GetAllFunctionDescriptors`）
5. **ドロップダウン対応**（Revit 側、UIスレッド必須なので案2でも `Run()` と同じスケジューラ経路で）
   - `PopulateItems()` → `Items` 検索 → `UpdateModelValueCommand "Value" <index>`
6. **C3D 実機テスト**: Civil3D 2026 + Dynamo で `build_graph` / `get_node_value` / `move` 等を通す
