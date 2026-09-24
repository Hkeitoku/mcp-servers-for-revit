# Revit MCP 開発引き継ぎガイド

このドキュメントは、AI 開発ツール（Claude Code、Cursor、Gemini など）向けの引き継ぎ指示です。

## プロジェクト概要

**Revit MCP** — Claude（AI アシスタント）から Autodesk Revit を操作する MCP（Model Context Protocol）サーバとプラグイン一式。

| 役割 | 言語 | 場所 |
|---|---|---|
| MCP サーバ（AI ↔ Revit の橋） | TypeScript/Node | `server/src/` → ビルド: `server/build/` |
| Revit プラグイン | C# (.NET 8) | `plugin/` |
| Revit API 実装本体 | C# (.NET 8) | `commandset/` |
| Dynamo 連携拡張 ★ | C# (.NET 8) | `DynamoMcpExtension/` |
| ビルド・テスト設定 | PowerShell/JSON | `scripts/` / `packaging/` |

**★ = 前回のセッションで追加された独自改造**（本家 mcp-servers-for-revit には未マージ）

---

## ビルド・実行方法

### 前提

- **Windows 11** (必須)
- **Revit 2026**（他バージョンは別途対応が必要）
- **.NET 8 SDK** / **Node.js 18+**
- **Visual Studio 2022** または `dotnet` CLI

### ビルド手順

```bash
# 1. C# 部分（プラグイン + CommandSet）
dotnet build mcp-servers-for-revit.sln -c "Release R26" -v m

# 2. Dynamo 拡張（C#）
cd DynamoMcpExtension
dotnet build DynamoMcpExtension.csproj -c Release

# 3. MCP サーバ（TypeScript）
cd ../server
npm install
npm run build
```

**出力先**:
- プラグイン: `plugin/bin/AddIn 2026 Release R26/`
- Dynamo: `DynamoMcpExtension/bin/Release/net8.0-windows/DynamoMcpExtension.dll`
  + 自動コピー先: `%APPDATA%/Dynamo/Dynamo Revit/3.6/packages/DynamoMcpExtension/bin/`
- サーバ: `server/build/`

### 実行環境

**開発中（ローカル）:**
```
~/.claude.json に登録済み:
  command: node
  args: ["<repo>/server/build/index.js"]
```

Claude Code を開くと MCP サーバが自動起動します。

**配布形式：**
`packaging/build-package.ps1` で ZIP を生成 → エンドユーザは `install.bat` をダブルクリック

---

## アーキテクチャ

```
Claude (AI)
    ↕ stdio (JSON-RPC)
MCP Server (Node.js)
    ↕ WebSocket
Revit Plugin (C#)  ← ここが常駐
    ↓
CommandSet (C#)  ← 実装本体
    ↓
Revit API / Dynamo
```

**フロー例: `get_current_view_info` を呼ぶ**
1. Claude が `{"method": "get_current_view_info"}` を MCP サーバに送る
2. Node が WebSocket でプラグイン側に転送
3. プラグイン内の `GetCurrentViewInfoEventHandler` が実行
4. CommandSet で `UIDocument.ActiveView` を取得 → JSON で返す
5. Dynamo ツール呼び出しはさらに `DynamoMcpExtension` を経由

---

## 重要な落とし穴集

### 1. **Python ノードが実行されない** 

症状: `DynamoMcpExtension` を更新しても、Dynamo で Python ノードが実行されないまま。

原因: `ScriptContent` を設定しても Dynamo が「dirty」と見なさず、実行をスキップする。

対策 (`DynamoMcpExtension/DynamoBridge.cs` に実装済み):
```csharp
node.MarkNodeAsModified(forceExecute: true);  // ← 必須
```

### 2. **Dynamo auto-layout が Civil3D で使えない**

症状: `op:"layout"` が `"No auto-layout path available"` を返す（Revit では動く）。

原因: `DynamoView` ウィンドウを探すが Civil3D にはない。

対策: 未修正。layout 機能は Revit 用のみ。

### 3. **doc ロック無しで AutoCAD/Civil3D API を呼ぶとクラッシュ**

症状: Dynamo Python ノードから `CogoPoints.Add()` を呼ぶと AutoCAD が落ちる。

原因: Dynamo Python スレッドから doc ロック無しに高レベル API を叩くとスレッド違反。

対策:
```csharp
using (var lock = new DocumentLock(doc, DocumentLockMode.ProtectedNested, 0)) {
    using (var tx = db.TransactionManager.StartTransaction()) {
        // ← ここで API 呼び出し
        tx.Commit();
    }
}
```

### 4. **Revit のリボンにボタンが出ない**

症状: アドインをインストールしても、Revit 起動時に「不明なアドイン」警告が出て、Settings ボタンが現れない。

チェックリスト:
- `%AppData%/Autodesk/Revit/Addins/2026/mcp-servers-for-revit.addin` が存在するか？
- Revit 起動時に「常に読み込む」を選択したか？（「読み込まない」を選ぶと次回以降も出ない）
- `command.json` は配置されているか？
- CommandSet DLL (`revit_mcp_plugin/Commands/RevitMCPCommandSet/2026/*.dll`) があるか？

### 5. **npm install が社内プロキシで失敗**

症状: `npm ERR! 403 Forbidden`

対策:
```bash
npm config set registry "http://registry.npmjs.org/"
npm install --no-audit --no-fund
```

### 6. **MCP サーバの initialize が失敗する**

症状: Claude が「ツールが見つかりません」と言う。

確認:
```bash
node server/build/index.js
# {"method":"initialize","params":{...}} を入力
# {"result":{"tools":[...]}} が返ってくるか？
```

→ 返ってこなければサーバビルドが古い。`npm run build` を再実行。

---

## 独自改造の詳細（Dynamo 連携）

### 追加されたツール

| ツール | 説明 |
|---|---|
| `dynamo_build_graph` | nodes + edges 配列から Dynamo グラフを構築 |
| `dynamo_run_code` | グラフを実行し、ノード値や実行結果を取得 |
| `dynamo_graph_control` | ノード削除、エラー確認など（未実装） |

### 実装ファイル

| ファイル | 役割 |
|---|---|
| `server/src/tools/dynamo_*.ts` | ツール定義（Node 側） |
| `commandset/Commands/Dynamo/*.cs` | Revit アドイン側コマンド |
| `commandset/Services/DynamoOpEventHandler.cs` | JSON → Dynamo 操作 の変換 |
| `DynamoMcpExtension/DynamoBridge.cs` | Dynamo Core API の低レベル操作 |
| `DynamoMcpExtension/GraphOps.cs` | グラフ操作ロジック |

### 使用例

Claude に「Dynamo で 1 から 5 の二乗を計算して」と言う:

```json
{
  "method": "dynamo_build_graph",
  "params": {
    "nodes": [
      {"id": "n1", "type": "Core.Range", "x": 0, "y": 0, "ports": {...}},
      {"id": "n2", "type": "Core.Math.Power", "x": 200, "y": 0, "ports": {...}}
    ],
    "edges": [
      {"from": {"nodeId": "n1", "port": 0}, "to": {"nodeId": "n2", "port": 0}}
    ]
  }
}
```

→ `dynamo_run_code` で実行 → `[1, 4, 9, 16, 25]` が返る

---

## 未完成タスク

### 🔴 Critical

- **`dynamo_run_code` の wait タイムアウト** (Civil3D のみ)
  実処理は完走するが、シグナル同期でタイムアウトが出る。
  → 実行後は `get_node_value` で別途確認が必要。

### 🟡 Important

- **Toposolid 化** (Revit Revit 側のみ)
  Civil3D 地形を Revit で使える Toposolid に変換。
  → 方針決定済み（共有座標 + ファイルブリッジ）。未実装。

- **断面図・縦断図出力** 
  切断位置指定などの UI が必要。

- **Revit Dynamo ツールの再登録不具合**
  `dynamo_run_code` がツール一覧に出ないバグ（不安定な再登録メカニズム）。

### 🟢 Nice-to-have

- **レイアウト・等高線・高低差計算**
- **造成面・切土盛土量計算**
- **Dynamo グラフの再利用可能化**（いまは ad-hoc な Python ノード）

---

## 配布パッケージ作成

```bash
powershell -ExecutionPolicy Bypass -File "packaging/build-package.ps1" -Version 1.0.1
```

出力: `dist/RevitMCP-Setup-v1.0.1.zip` (約 20MB)

内容:
```
RevitMCP-Setup/
├─ install.bat / uninstall.bat        # ダブルクリック起動
├─ はじめにお読みください.md
└─ payload/
   ├─ install.ps1 / uninstall.ps1    # インストーラ本体
   ├─ revit-addin/                   # Revit プラグイン
   ├─ dynamo-package/                # Dynamo 拡張
   └─ server/                        # MCP サーバ（ビルド済み + package.json）
```

インストール時に `npm install --omit=dev` を実行（ネット接続必須）。

---

## コード規約・レビューポイント

### C# CommandSet

- Revit トランザクション は必ず `using (var tx = ...)` で。
- 要素削除時は `doc.Delete(elementId)` の前に参照カウント確認。
- API 19 から非推奨メソッドが増加中（Revit 2026 で削除予定）。

### TypeScript MCP サーバ

- ツール定義は `src/tools/` の独立ファイル + `src/tools/register.ts` で自動登録。
- スキーマは Zod で型安全に（`create_point_based_element.ts` を参考）。
- エラーハンドリングは `error` フィールドで JSON 返却（exception は吐かない）。

### Dynamo 拡張

- リフレクション多用（Dynamo API の安定性が低い）。
- 3.4 / 3.6 の API は互換性あり（`layoutSpecs.json` や `library.html` はバイト一致）。

---

## 参考リンク・ファイル

| 対象 | 場所 |
|---|---|
| 作業記録（前セッション） | `../claudecode_revitMCP/作業まとめ_2026-09-05.md` |
| Civil3D 側 MCP | `C:/Users/harap/Documents/Civil3dMCP/` (git 管理外) |
| Dynamo UI Plus | `C:/Users/harap/Dynamo-UI/` / `C:/Users/harap/Civil3d-Dynamo-UI/` |
| ビルド出力 DB | `server/revit-data.db` (SQLite, store_project_data など用) |

---

## よく使うコマンド

```bash
# 1. アドインのみリビルド（早い）
dotnet build plugin/ commandset/ -c "Release R26" -v q

# 2. MCP サーバのみリビルド
cd server && npm run build

# 3. Revit 起動時スクリプト実行
& "C:\Program Files\Autodesk\Revit 2026\Revit.exe" /b start_mcp.scr

# 4. Dynamo パッケージ確認
ls "$env:APPDATA\Dynamo\Dynamo Revit\3.6\packages\DynamoMcpExtension"

# 5. MCP サーバの疎通確認
echo '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2024-11-05","capabilities":{},"clientInfo":{"name":"test","version":"1"}}}' | node server/build/index.js
```

---

## トラブルシューティング Flow

1. **Revit ボタンが出ない** → アドイン配置 → Revit 再起動 → 警告で「常に読み込む」
2. **ツールが Claude に出ない** → MCP サーバの initialize を確認 → `npm run build` 再実行
3. **Dynamo グラフが実行されない** → Python ノード？ → `MarkNodeAsModified(forceExecute:true)` を確認
4. **Civil3D で待ちタイムアウト** → 実処理は完走 → `get_node_value` で別途確認
5. **npm install が失敗** → プロキシ設定 → レジストリを `http://registry.npmjs.org/` に変更

---

**最終更新**: 2026-09-24 by Claude  
**対象Revit**: 2026 (.NET 8)  
**対象MCP**: mcp-servers-for-revit v1.0.0 + Dynamo 改造
