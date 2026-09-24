# 調査依頼: Revit×Dynamo MCP ブリッジのレイテンシ改善

以下をそのまま調査用AI（Codex / Gemini / Claude、Web検索可が望ましい）に貼ってください。

**進捗メモ（2026-09-04）:** B の「バッチ ExternalEvent」は実装・検証済み（`op:"batch"` で N op を 1 ExternalEvent 実行、構築中 Manual 固定 + 最終 1 回 Run）。壁グラフが約10往復→2往復、体感15-30秒→3.4秒に短縮。C の「`run` の no-op 25秒ハング」も `EvaluationStarted` 早期リターンで解消済み。
→ **残りの主対象: A（常設 socket の是非と実装）、B（ExternalEvent の idle 発火待ちの実測値と、graph-only 編集で ExternalEvent を回避できるか）、C（Manual+単一Run のさらなる詰め、preview/freeze）、D（Revit トランザクションコスト）、E（ホットリロード開発ループ）、F（プロファイリング基盤）。**

---

## 回答ルール
- 回答は日本語、API 名・型名は English。
- 対象: **Dynamo Core 3.6.2.11575 (Revit 2026)**。Civil 3D 側 (Dynamo 3.4.1) の差異があれば明記。
- 参照: github.com/DynamoDS/Dynamo、Autodesk Revit API docs、既存 OSS。
- 各項目に「結論 / 使う API・設定 / 最小コード例 / 計測方法 / 3.4-3.6 差異」。推測は「未確認」と明記。
- 最後に「効果の大きい順の改善ロードマップ」。

## 現状アーキテクチャ（実測で遅い）

```
MCP client
  → Node.js MCP server         localhost:8080 TCP / JSON-RPC
       ※ コマンド毎に新規 TCP connect → 1 リクエスト → disconnect (withRevitConnection)
    → Revit アドイン RevitMCPPlugin (SocketService, クライアント毎にスレッド)
      → コマンド = ExternalEventCommandBase (RevitMCPSDK, コンパイル済み)
         RaiseAndWaitForCompletion(ms) が Revit ExternalEvent を Raise し、
         IWaitableExternalEventHandler が signal するまで socket スレッドをブロック
        → DynamoOpEventHandler.Execute(UIApplication)  ← Revit UI/idle スレッド
          → reflection で DynamoMcpExtension 内 DynamoBridge.ExecuteBatch(json)
            → ICommandExecutive.ExecuteCommand(RecordableCommand, extId, extName)
              → 現在開いている Dynamo Home グラフを編集
```

- グラフは通常 **Automatic run mode**。ノード生成 / 結線 / 値更新のたびに
  Dynamo scheduler スレッドで**フルの再評価**が走る。
- グラフに `Wall.ByCurveAndHeight` 等の Revit ノードがあると、再評価のたびに
  Revit トランザクションが開かれる (RevitServices の TransactionManager)。
- **実測**: 「パラメトリック壁グラフ」の構築で
  `build_graph`(1) + `set_dropdown`(2) + `connect`(4) + `run`(1) + `get_node_value`(1) + `get_graph`(1)
  ≒ 10 回の socket 往復。体感でかなり待つ。
- `run` に `wait:true` を付けると、再評価対象が無いケースでも
  `RefreshCompleted` を最大 25 秒待ってタイムアウトする。
- 開発ループ: `RevitMCPCommandSet.dll` / `DynamoMcpExtension.dll` を変えるたびに
  **Revit を完全終了 → リビルド → 再起動 → MCP Switch 再押下**（1〜2分/回）。
  理由: `Assembly.LoadFrom` がセッション内キャッシュ、Dynamo パッケージの DLL がロック。

---

## A. トランスポート層 (MCP ↔ Revit socket)

- **コマンド毎の TCP connect/teardown コスト。** 永続接続 / コネクションプールにした場合の
  短縮量。RevitMCPPlugin の `SocketService.HandleClientCommunication` は
  1 接続で複数リクエストを順次処理できる設計か（ループして読み続けるか、1回で閉じるか）。
  keep-alive 化に必要な server/client 双方の変更点。
- JSON フレーミング（現状クライアントは `JSON.parse(buffer)` が通るまで待つだけで
  length prefix 無し）。ndjson / length-prefixed にする利点はあるか。
- そもそも 1 プロセス（Revit）に対し 1 本の常設接続 + リクエストキューにすべきか。

## B. Revit ExternalEvent レイテンシ

- `ExternalEvent.Raise()` は Revit が idle になった時だけ発火する。
  典型的な発火待ち時間（ms オーダー？）と、モデルサイズ / UI 操作中の悪化。
- **純粋な Dynamo グラフ編集（Revit API を一切触らない: ノード生成 / 結線 / 値更新 / レイアウト）に
  ExternalEvent は必要か。** 不要なら軽量な代替:
  - `Application.Idling` イベント + 作業キュー
  - Dynamo 自身の scheduler (`DynamoModel.Scheduler` / `DelegateBasedAsyncTask`) にグラフ編集だけ載せる
  - どれが安全か。ホストノードが評価で走る瞬間だけ ExternalEvent、構造編集は scheduler、という
    ハイブリッドは成立するか。
- **バッチ実行（最重要）**: N 個のサブ op を **1 回の ExternalEvent** で実行する
  （idle 待ち 1 回・トランザクション context 1 回・最終再評価 1 回）。
  - リクエストプロトコル設計（`{"op":"batch","ops":[...]}`）。
  - 途中失敗時の扱い（全体 abort か、部分成功 + errors[] か）。
  - 現状 `build_graph` は既にバッチ化済み。`connect` / `set_dropdown` / `set_value` /
    `run` を同じ 1 リクエストに畳む形。

## C. Dynamo 評価パフォーマンス

- Automatic モードで `CreateNodeCommand` / `MakeConnectionCommand` / `UpdateModelValueCommand`
  ごとにフル再評価が走る。**バッチ構築中は `RunSettings.RunType = Manual` に固定し、
  最後に 1 回だけ `Run()`** するのがベストか。落とし穴（dirty ノードが残る / preview が古い /
  input ノードの反映タイミング）。
- `NodeModel.IsFrozen`（フリーズ）で構築中サブツリーの評価を抑制できるか。
- 構築中に background geometry preview / `NodeModel.IsVisible` を切ると評価は速くなるか。
  `EngineController` / `IExecutionSession` の preview 生成コスト。
- 評価コストを下げる `DynamoModel` / `HomeWorkspaceModel` 設定
  （`MaxTesselationDivisions`、geometry scaling、`EnablePreviewBubbles`、
  `IsShowingConnectors`、`ProfileWorkflow` など）。
- **`RunCancelCommand` はグラフが clean なとき即 return するか、no-op 評価をスケジュールするか。**
  （`run wait:true` が 25 秒ハングする原因。`HomeWorkspaceModel.GraphRunInProgress` や
  dirty ノード数を見て「待つ必要なし」を高速判定する方法。）
- `EvaluationStarted` / `EvaluationCompleted` / `RefreshCompleted` のうち
  「評価対象なし」を最短で知れるシグナル。

## D. Revit トランザクションコスト

- `Wall.ByCurveAndHeight` を含むグラフの再評価は、入力が変わっていなくても
  毎回 Revit トランザクションを開く/閉じるか。element binding / `TraceData` のオーバーヘッド。
- RevitServices `TransactionManager` の再評価時の挙動、sub-transaction のグルーピング。
- Revit 書き込みノードの実行を明示 "commit" まで遅延できるか
  （Dynamo Player 風、`RunType.Manual` で十分か）。

## E. 開発ループ（Revit を再起動せずにリビルド反映）

- 現状: DLL 変更 → Revit 完全終了が必須。
- 候補と実現手順:
  - **`AddInManager`**（Revit SDK / RevitLookup 同梱）で external command をホットロード。
  - commandset を `Assembly.Load(File.ReadAllBytes(path))` でロードしてファイルロックを避け、
    MCP Switch トグルで新ビルドに差し替え（`SocketService.Initialize` は毎トグル走る）。
  - `DynamoMcpExtension` のロジック（`GraphOps` / `DynamoBridge` の実処理）を
    **別 DLL に切り出し、拡張本体は薄い shim** にして、実処理 DLL を
    `Assembly.Load(byte[])` で毎回ロードし直す（パッケージ DLL のロックを回避）。
  - AppDomain 分離 + Unload（.NET 8 では `AssemblyLoadContext` collectible）。
  - "reload" MCP コマンドで実処理アセンブリを再ロード。
  - `dotnet watch` 併用の是非。
- Dynamo パッケージ DLL（Dynamo がロックする）を、拡張が起動後に
  `AssemblyLoadContext` で差し替え可能にする現実的な設計。

## F. 計測 / プロファイリング

- 壁 1 サイクルの時間内訳を測る方法:
  - Revit ジャーナルファイルのタイムスタンプ
  - Dynamo 組み込みプロファイリング（`TuneUp` パッケージ、`Dynamo.Logging`、
    `HomeWorkspaceModel` の run 時間ログ）
  - `EngineController` / `Dynamo.Scheduler` のタスク時間
  - ブリッジ各段（socket 受信 / ExternalEvent 発火待ち / ExecuteBatch / 各 RecordableCommand /
    再評価）に `Stopwatch` を仕込むポイント
- 「ExternalEvent 発火待ち」と「Dynamo 評価」と「Revit トランザクション」の
  どれが支配的かを切り分ける手順。

---

## 出力
- A〜F 各項目: 結論 / API・設定 / 最小 C#(または TS) / 計測方法 / 3.4-3.6 差異。
- 最後に **効果の大きい順の改善ロードマップ**（例: バッチ ExternalEvent → 常設 socket →
  Manual モード + 単一 Run → ホットリロード → 計測基盤）。
