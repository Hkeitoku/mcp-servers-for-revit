# 開発環境セットアップガイド

他の AI（Cursor、Gemini、Claude Code など）が Revit MCP の開発を引き継ぐための環境構築手順です。

---

## システム要件

| 項目 | 要件 | 確認方法 |
|---|---|---|
| OS | Windows 10 / 11（64-bit） | `winver` |
| メモリ | 8GB 以上推奨（Revit + VS は 16GB が快適） | タスクマネージャー |
| ディスク | 50GB 以上空き | エクスプローラー |
| Revit | 2026（単体版または体験版） | Autodesk アカウント |
| .NET SDK | 8.0+ | `dotnet --version` |
| Node.js | 18+ | `node -v` |
| Git | 2.30+ | `git --version` |
| IDE（推奨） | Visual Studio 2022 Community（無料） | — |

---

## ステップ 1: ツール・SDK のインストール

### 1a. .NET 8 SDK

```powershell
# インストール済みか確認
dotnet --version

# 8.0 以上ならスキップ。無ければ：
# https://dotnet.microsoft.com/download/dotnet/8.0 から .NET 8.0 SDK をダウンロード
# Windows Installer でインストール
```

### 1b. Node.js 18+

```powershell
# 確認
node -v
npm -v

# 無ければ https://nodejs.org/ から LTS 版をダウンロード・インストール
# インストール後は PowerShell / コマンドプロンプトを**再起動**
```

### 1c. Git

```powershell
# 確認
git --version

# 無ければ https://git-scm.com/download/win から最新版をダウンロード
```

### 1d. Visual Studio 2022 Community（推奨）

```powershell
# C# 開発には VS 2022 が便利（ビルドエラーの診断、IntelliSense など）
# https://visualstudio.microsoft.com/vs/community/ からダウンロード

# インストール時に以下ワークロードを選択：
# - .NET desktop development
# - Desktop development with C++
# - Visual Studio extension development（オプション）
```

**VS Code でビルドする場合:**
```powershell
# C# Dev Kit + C# Test Explorer 拡張をインストール
code --install-extension ms-dotnettools.csharp
code --install-extension ms-dotnettools.vscode-dotnet-runtime
```

---

## ステップ 2: リポジトリのクローン

```powershell
# 作業ディレクトリを作成（例: C:\dev）
mkdir C:\dev
cd C:\dev

# リポジトリをクローン
git clone https://github.com/Hkeitoku/mcp-servers-for-revit.git
cd mcp-servers-for-revit

# ブランチ確認（main にいるはず）
git branch -a
git log --oneline -3
```

---

## ステップ 3: NuGet & npm パッケージの復元

### 3a. C# パッケージ（NuGet）

```powershell
cd C:\dev\mcp-servers-for-revit

# ソリューション全体を復元
dotnet restore mcp-servers-for-revit.sln

# 各プロジェクトも明示的に復元（オプション）
dotnet restore plugin/RevitMCPPlugin.csproj
dotnet restore commandset/RevitMCPCommandSet.csproj
dotnet restore DynamoMcpExtension/DynamoMcpExtension.csproj
```

### 3b. Node.js パッケージ

```powershell
cd C:\dev\mcp-servers-for-revit\server

# 依存パッケージをインストール（開発用 + 本番用）
npm install

# 本番ビルド用は --omit=dev でも OK
npm install --omit=dev
```

**社内プロキシ環境の場合:**
```powershell
npm config set registry "http://registry.npmjs.org/"
npm config set proxy http://proxy.company.com:8080
npm config set https-proxy http://proxy.company.com:8080
npm install
```

---

## ステップ 4: 初回ビルド

### 4a. C# プロジェクト（Revit 2026 用）

```powershell
cd C:\dev\mcp-servers-for-revit

# ソリューション全体をビルド（Release R26 構成）
dotnet build mcp-servers-for-revit.sln -c "Release R26" -v m

# ビルド成功の確認
# → plugin/bin/AddIn 2026 Release R26/ に .addin と .dll が出力される
```

**ビルド失敗時:**
```powershell
# キャッシュをクリアして再ビルド
dotnet clean mcp-servers-for-revit.sln
dotnet build mcp-servers-for-revit.sln -c "Release R26" -restore

# または Visual Studio から直接ビルド（エラー詳細が見やすい）
start mcp-servers-for-revit.sln
```

### 4b. Dynamo 拡張

```powershell
cd C:\dev\mcp-servers-for-revit\DynamoMcpExtension

# ビルド（自動で %AppData% にコピーされる）
dotnet build DynamoMcpExtension.csproj -c Release

# 確認
ls $env:APPDATA\Dynamo\Dynamo\ Revit\3.6\packages\DynamoMcpExtension\bin\
```

### 4c. MCP サーバ（TypeScript）

```powershell
cd C:\dev\mcp-servers-for-revit\server

# ビルド（TypeScript → JavaScript）
npm run build

# 確認
ls build/index.js
ls build/tools/ | wc -l  # ツール数を表示（33 個が目安）
```

---

## ステップ 5: Revit との連動（開発環境）

### 5a. アドインの配置

```powershell
# Revit 2026 アドインディレクトリにコピー
$src = "C:\dev\mcp-servers-for-revit\plugin\bin\AddIn 2026 Release R26"
$dst = "$env:APPDATA\Autodesk\Revit\Addins\2026"

Copy-Item "$src\*" $dst -Recurse -Force
```

### 5b. Revit を起動

```powershell
# Revit 2026 を起動
& "C:\Program Files\Autodesk\Revit 2026\Revit.exe"

# 「不明なアドイン」警告が出たら：
#  [常に読み込む] を選択 ← 重要！「読み込まない」は選ばない
```

### 5c. MCP サーバを起動

```powershell
# **別のターミナルで** MCP サーバを起動（Revit とは別プロセス）
cd C:\dev\mcp-servers-for-revit\server
node build/index.js

# 以下が出れば OK
# 已注册工具: ai_element_filter.js
# 已注册工具: analyze_model_statistics.js
# ...
# 已注册工具: dynamo_build_graph.js
# 已注册工具: dynamo_run_code.js
```

---

## ステップ 6: MCP クライアント登録（Claude Code 用）

開発中は Claude Code から MCP サーバを実行します。

### 6a. ~/.claude.json に登録

```powershell
# ホームディレクトリの .claude.json を編集
# (または VS Code で $PROFILE\..\..\.claude.json を開く)

$claudeJson = "$env:USERPROFILE\.claude.json"
$config = @{
    mcpServers = @{
        "revit" = @{
            "command" = "node"
            "args" = @("C:\dev\mcp-servers-for-revit\server\build\index.js")
        }
    }
}

$config | ConvertTo-Json -Depth 5 | Set-Content $claudeJson -Encoding UTF8
```

### 6b. Claude Code を再起動

```powershell
# Claude Code を完全終了して起動し直す
# → リボン下のハンマーアイコンが「connected」になれば OK
```

---

## ステップ 7: テスト・開発ワークフロー

### 7a. ローカルで動作確認

```powershell
# ターミナル 1: MCP サーバを起動（前述）
cd C:\dev\mcp-servers-for-revit\server
node build/index.js

# ターミナル 2: 疎通テスト
echo '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2024-11-05","capabilities":{},"clientInfo":{"name":"test","version":"1"}}}' `
  | node C:\dev\mcp-servers-for-revit\server\build\index.js

# 応答: {"result":{"protocolVersion":"2024-11-05",...},"jsonrpc":"2.0","id":1}
```

### 7b. Claude Code で動作確認

1. Claude Code を起動
2. Revit が起動していて、アドインが読み込まれていることを確認
3. チャットで「Revit の現在のビュー情報を取得して」と入力
4. MCP サーバが `get_current_view_info` を実行 → ビュー情報が返ってくる

### 7c. コード変更 → 再ビルド → テスト

```powershell
# C# を変更した場合
cd C:\dev\mcp-servers-for-revit
dotnet build plugin/ commandset/ -c "Release R26" -v q
# Revit を再起動して確認

# TypeScript を変更した場合
cd C:\dev\mcp-servers-for-revit\server
npm run build
# MCP サーバプロセスを再起動（Ctrl+C → node build/index.js）
```

---

## ステップ 8: IDE の推奨設定

### Visual Studio 2022

```
ファイル → オプション → テキストエディタ → C# → コード スタイル
  - インデント: 4 スペース（プロジェクト設定に合わせる）
  - 末尾の空白を削除: ON

ビルド → ビルド構成 → Release R26 を選択
```

### VS Code

```json
// .vscode/settings.json
{
    "[csharp]": {
        "editor.formatOnSave": true,
        "editor.defaultFormatter": "ms-dotnettools.csharp"
    },
    "[typescript]": {
        "editor.formatOnSave": true,
        "editor.codeActionsOnSave": {
            "source.fixAll": "explicit"
        }
    },
    "omnisharp.defaultProject": "${workspaceFolder}/plugin/RevitMCPPlugin.csproj"
}
```

---

## ステップ 9: トラブルシューティング

### "dotnet: コマンドが見つかりません"

```powershell
# .NET SDK がインストールされていない
# または PATH に含まれていない

# 確認
dotnet --version
# 出力されない場合は再インストール + PC 再起動

# または絶対パス指定
& "C:\Program Files\dotnet\dotnet.exe" --version
```

### "npm: 内部エラー"

```powershell
# npm キャッシュをクリア
npm cache clean --force

# 再インストール
rm -Recurse -Force node_modules
npm install
```

### "Revit アドインが読み込まれない"

```powershell
# 1. アドインファイルを確認
Test-Path "$env:APPDATA\Autodesk\Revit\Addins\2026\mcp-servers-for-revit.addin"

# 2. ビルド出力が新しいか確認
ls -l "$env:APPDATA\Autodesk\Revit\Addins\2026\revit_mcp_plugin\RevitMCPPlugin.dll"

# 3. Revit ログを確認
# C:\Users\<username>\AppData\Local\Autodesk\Revit\Addins\2026\RevitMCPPlugin.log

# 4. 再配置
dotnet build plugin/ -c "Release R26"
Copy-Item "plugin/bin/AddIn 2026 Release R26/*" "$env:APPDATA\Autodesk\Revit\Addins\2026" -Recurse -Force
# Revit 再起動
```

### "Python ノードが実行されない"

**原因:** Dynamo 拡張の DLL が古い、または Dynamo が dirty フラグを認識しない

```powershell
# 1. Dynamo 拡張を再ビルド
cd DynamoMcpExtension
dotnet build DynamoMcpExtension.csproj -c Release

# 2. Revit と Dynamo を再起動

# 3. 確認（AGENTS.md の「Python ノード実行」セクション参照）
```

---

## ステップ 10: 継続的な開発

### 推奨ワークフロー

1. **毎日の起動**
   ```powershell
   # ターミナル 1: MCP サーバ
   cd C:\dev\mcp-servers-for-revit\server
   node build/index.js
   
   # ターミナル 2: IDE
   code C:\dev\mcp-servers-for-revit
   
   # ターミナル 3: Revit
   & "C:\Program Files\Autodesk\Revit 2026\Revit.exe"
   ```

2. **コード変更**
   - ファイルを編集
   - IDE で保存（自動フォーマット有効の場合）

3. **ビルド**
   ```powershell
   # C# 変更時
   dotnet build plugin/ commandset/ -c "Release R26" -v q
   # Revit 再起動
   
   # TypeScript 変更時
   npm run build  # from server/
   # MCP サーバを再起動（Ctrl+C → node build/index.js）
   ```

4. **テスト**
   - Claude Code で `get_current_view_info` など実行
   - Revit で実際の操作を確認

### ビルド自動化（オプション）

```powershell
# PowerShell ウォッチスクリプト（build.ps1）
$watcher = New-Object System.IO.FileSystemWatcher
$watcher.Path = "C:\dev\mcp-servers-for-revit\server\src"
$watcher.Filter = "*.ts"
$watcher.IncludeSubdirectories = $true

Register-ObjectEvent -InputObject $watcher -EventName "Changed" -Action {
    cd C:\dev\mcp-servers-for-revit\server
    npm run build
    Write-Host "✓ Rebuilt at $(Get-Date)" -ForegroundColor Green
}

Read-Host "Watching... (Ctrl+C to stop)"
```

---

## 参考

| 項目 | 参照先 |
|---|---|
| アーキテクチャ・ハマりどころ | `AGENTS.md` |
| ツール一覧・スキーマ | `server/build/tools/` |
| ビルド設定 | `.csproj` / `tsconfig.json` / `package.json` |
| 配布パッケージ作成 | `packaging/build-package.ps1` |
| テスト・CI | `.github/workflows/release.yml` |

---

**最終更新**: 2026-09-24  
**対象**: Windows 11, Revit 2026, .NET 8, Node.js 18+  
**想定ユーザー**: AI 開発ツール（Cursor, Gemini, Claude Code など）
