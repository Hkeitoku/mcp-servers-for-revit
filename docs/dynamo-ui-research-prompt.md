# 調査依頼: Dynamo 3.6 のノード自動レイアウト + ViewExtension UI カスタム

以下をそのまま調査用AI（Codex / Gemini / Claude、Web検索可が望ましい）に貼ってください。

---

## 回答ルール
- 回答は日本語、API 名・型名は English。
- 対象: **Dynamo Core 3.6.2.11575 / Dynamo for Revit 2026 / .NET 8 / WPF**。3.4.1（Civil 3D 2026）差異があれば注記。
- 参照: github.com/DynamoDS/Dynamo（`v3.6.2` タグ優先）、Autodesk Dynamo Developer docs。
- 各項目に「結論 / 使う型・API・シグネチャ / 最小コード例 / ソース根拠」。推測は「未確認」と明記。

## 背景（実装済み・実機で問題発生中）

MCP ブリッジ（Dynamo `IExtension`）が `IExtension.Ready(ReadyParams)` で `CommandExecutive` と
`CurrentWorkspaceModel` を掴み、`ICommandExecutive.ExecuteCommand(RecordableCommand, extId, extName)`
経由でノードを生成・結線している（Revit 側の `ExternalEvent` = Revit UI スレッド上で実行）。

- ノード生成: `new DynamoModel.CreateNodeCommand(guidString, "Point.ByCoordinates", x, y, true, false)`
- 結線: `MakeConnectionCommand` Begin/End
- ノード名検索: `NodeSearchModel.Search(string, LuceneSearchUtility, CancellationToken)` を reflection（動作OK）
- 逆引き: 配置済み `NodeModel.Category` / `CreationName` / `Description`（public、動作OK）

### 発生中の問題
全ノードを同じ座標 `(0,0)` に置いてから
`Dynamo.Graph.Workspaces.LayoutExtensions.DoGraphAutoLayout(WorkspaceModel)`（internal static）を
reflection で呼んでいるが、**ノードが重なったまま整列されない**。
戻り値の `List` の `Count` は 2〜ノード数。例外は出ない。全ノードは結線済み。
呼び出しは Revit UI スレッド（ExternalEvent handler）内、`ExecuteBatch` の中。

---

# パート1: ノード自動レイアウトが効かない

## 1-1. `DoGraphAutoLayout` が X/Y を書き込む条件
- `DoGraphAutoLayout` は `NodeModel.Width` / `Height` を使ってレイアウトするか。
  これらの値は **WPF の `NodeView` が measure/render するまで** 0 または既定のままか。
  `CommandExecutive` でノードを作った直後（view 未レンダー）に layout を呼ぶと全ノードが
  同じ位置に落ちるか。
- `LayoutExtensions.cs` / `GraphLayout.Graph` / `GraphLayout.Node` の 3.6.2 実装を追い、
  「layout が実際に `NodeModel.X` / `Y` を更新する条件」「ノードサイズをどこから取るか」を特定。
- `DoGraphAutoLayout` は connected subgraph 単位か。孤立ノードは無視されるか。
- `RecordUndoGraphLayout` / `SaveLayoutGraph` が絡む前提条件。
- UI スレッド要件、`Dispatcher` / scheduler の要否。ExternalEvent（Revit UI スレッド）内で
  同期呼び出しして問題ないか。

## 1-2. `CreateNodeCommand` 直後にノードサイズは確定するか
- `CreateNodeCommand` 実行直後、`workspace.Nodes.First(n => n.GUID == guid).Width` は
  有効値か、それとも `NodeView` バインド後か。
- `NodeViewModel` / `NodeView.ActualWidth` を待つ方法（`Dispatcher.BeginInvoke(DispatcherPriority.Loaded)` 等）。
- Code Block はコード長でサイズが変わる。dropdown は項目名でサイズが変わる。これらの
  measured サイズを layout 前に確実に得る方法。

## 1-3. 正しい「MCP から確実に整列させる手順」
次のいずれか、最も堅い方法を最小コードで:
- (a) layout を UI thread の後段（`Dispatcher.BeginInvoke` / `RequestNodeVisualUpdate` 後）で呼ぶ
- (b) `DynamoViewModel` の "Cleanup Node Layout" コマンド（`GraphAutoLayoutCommand`?）を発火。
      view 側の前処理（サイズ確定）が入るならこちらが確実か。ViewExtension から `DynamoViewModel` に
      アクセスして menu command を実行する方法。
- (c) 自前 topological layering。各列を dependency 深さで決め、列内は縦に一定間隔。
      ノード幅は概算（Code Block = 一定 + コード行数×文字幅、dropdown = 一定、他 = 200 等）でも実用上OKか。
- (d) `CreateNodeCommand` に最初から計算済み座標を渡し layout 自体を不要にする（推奨？）。

## 1-4. `DynamoViewModel` へのアクセス
- `IExtension`（非 View）から `DynamoViewModel` を取る手段はあるか。
  現状 `ExtensionCommandExecutive` の private field `dynamoModel` から `DynamoModel` は取れている。
  `DynamoModel` → `DynamoViewModel` の経路（無い場合は ViewExtension 必須）。

---

# パート2: Dynamo ViewExtension UI カスタム（新規サブプロジェクト）

**保存先リポジトリ: https://github.com/Hkeitoku/Dynamo-UI-**
（Dynamo の UI カスタム関連コードはすべてここ。既存の `mcp-servers-for-revit` とは分離。）

## 2-1. ViewExtension のパッケージング（3.6 / Revit 2026）
- **既知の問題**: `IExtension` から `IViewExtensionSource.RequestAddExtension` を呼んでも
  Dynamo 3.6 では発火せず、ViewExtension が動的登録されない。
- 正しい方法: package に `*_ViewExtensionDefinition.xml` を置く。
  - 正確な XML フォーマット（`<ViewExtensionDefinition>` の要素、`AssemblyPath` / `TypeName`）
  - 配置場所: `%AppData%\Dynamo\Dynamo Revit\3.6\packages\<pkg>\` の下の
    `extra\` か `viewExtensions\` か（両方の役割の違い）
  - `pkg.json` に必要な記述
- `IViewExtension.Loaded(ViewLoadedParams p)` から取れるもの:
  `DynamoWindow`, `DynamoViewModel`(= `DynamoWindow.DataContext`?), `CommandExecutive`,
  library の ViewModel、`AddMenuItem`, `AddToExtensionsSideBar`。
- **Revit 2026 で ViewExtension が確実にロードされる最小サンプル**（pkg 構成 + `Loaded` のコード）。

## 2-2. GH 風「ダブルクリックで検索ポップアップ」

**目標 UX（GH 準拠）:**
- キャンバスの空白をダブルクリック → **検索ポップアップ**を出す（既定の Code Block 即時生成はやめる）
- Code Block が欲しいときは検索欄で **`"` を入力**（先頭が `"` なら検索せず Code Block ノードを配置。
  GH でパネル/数値を出すのと同じ発想）。数値や `1..10` 等の式を入れたら Code Block に流す、も検討可。

**調べること:**
- Dynamo のキャンバス ダブルクリック既定 = Code Block 生成。このハンドラの場所
  （`WorkspaceView` / `WorkspaceViewModel` の `HandleMouseDoubleClick` 相当）と、
  ViewExtension から差し替え or 抑制する方法（イベントの `Handled` 設定、既定コマンドの上書き）。
- Dynamo の in-canvas search は既存: `Dynamo.ViewModels.SearchViewModel` /
  `Dynamo.UI.Views.InCanvasSearchControl` / `InCanvasSearchViewModel`。
  これをプログラムから任意座標に開く方法、ViewExtension から呼べるか。
- 検索欄の入力を監視し、先頭が `"` なら検索をキャンセルして
  `CreateNodeCommand(guid, "Code Block", x, y, ...)`（または `CodeBlockNodeModel` 直接生成）へ分岐する実装点。
- 検索結果選択 → キャンバスの元ダブルクリック座標にノード配置（`SearchViewModel` のノード生成イベント /
  `SearchElementBase.CreateAndConnectCommand` / drag-drop の代替）。

## 2-3. GH 風「配置ノードの Library 内位置をハイライト」（2枚目の写真の機能）
- 配置済みノード上で操作 → 左 Library パネルでそのノードのカテゴリ/項目まで
  スクロール & ハイライト。
- Library ツリーの ViewModel（`SearchViewModel` の `BrowserRootCategories` /
  `NodeCategoryViewModel` / `NodeSearchElementViewModel`）をカテゴリパスで辿り、
  該当項目を選択状態 + `BringIntoView` する方法。
- `NodeModel.Category`（public string、例 `"Revit.Elements.Wall"` `"Core.List.Actions"`）→
  Library ツリーノードへのマッピング。
- Library パネルの表示切替（折りたたまれている場合に開く）。

## 2-4. 設定タブ（詳細設定 UI）
- ViewExtension で Dynamo に独自のドッキング可能パネル / タブを追加する方法
  （`ViewLoadedParams.AddToExtensionsSideBar(ViewExtensionSideBarData)` の使い方、
  WPF UserControl のホスト）。
- ユーザー設定の永続化: `%AppData%` 独自 json か、Dynamo の `PreferenceSettings` に相乗り可能か。
- 設定項目の例: 検索呼び出しのキーバインド、自動レイアウトの間隔、逆引きハイライトの ON/OFF、
  MCP 連携の有効/無効。

## 2-5. 既存 MCP ブリッジとの関係
- この ViewExtension を既存 `DynamoMcpExtension`（`IExtension`）と同一 package に同居させるか、
  別 package にするか。同一プロセスなので ViewExtension から `DynamoMcpExtension.DynamoBridge`
  （search / node_info ロジック）を直接呼べるか。
- ビルド構成: ViewExtension は `DynamoCoreWpf` / WPF 参照が必要。既存拡張との参照分離。

---

## 出力
- **パート1**: `DoGraphAutoLayout` が X/Y を書き込む条件を特定し、
  「MCP（ExternalEvent/コマンド経由）から確実にノードを整列させる手順」を最小コードで1つ提示。
- **パート2**: ViewExtension の最小動作サンプル（package 構成 + `Loaded` コード）と、
  2-2 / 2-3 / 2-4 各機能の実装方針・キー API。
- 3.6.2 のソース参照を明記。
- 最後に「実装順の推奨ロードマップ」。
