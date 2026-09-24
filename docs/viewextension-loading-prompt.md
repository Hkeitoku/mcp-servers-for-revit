# 調査依頼（限定）: Dynamo for Revit 2026 で ViewExtension をロードさせる最小手順

以下をそのまま調査用AI（Web検索可が望ましい）に貼ってください。
**この調査のゴールは1つだけ: 「自作 `IViewExtension` が Dynamo for Revit 2026 で確実にロードされ、
サイドバーにパネルを1枚出す最小構成」を確定すること。** UI 機能の中身は今回は対象外。

---

## 回答ルール
- 回答は日本語、型名・API・ファイル名は English。
- 対象: **Dynamo Core 3.6.2.11575 / Dynamo for Revit 2026 / .NET 8 / WPF (DynamoCoreWpf)**。
- 参照: github.com/DynamoDS/Dynamo（`v3.6.2` 優先）、DynamoDS/DynamoSamples、Dynamo Wiki、Autodesk Dynamo Developer docs。
- 各項目「結論 / 使うファイル・型・API / 具体例（XML と C# の実物）/ ソース根拠」。推測は「未確認」と明記。

## 前提（実機で判明していること）
- `IExtension`（非 View）の自作拡張は **動作している**。
  パッケージ `%AppData%\Dynamo\Dynamo Revit\3.6\packages\<pkg>\` に
  `pkg.json` +（フォルダ `extra\` 内に）`<Name>_ExtensionDefinition.xml`
  （`<ExtensionDefinition><AssemblyPath>..\bin\X.dll</AssemblyPath><TypeName>Ns.ClassMain</TypeName></ExtensionDefinition>`）
  を置く方式でロードされている。`bin\X.dll` も同梱。
- **効かなかった方法**: その `IExtension` から `IViewExtensionSource` を実装し
  `RequestAddExtension?.Invoke(new MyViewExtension())` を `Ready()` で呼んでも、
  Dynamo 3.6 では View Extension が登録されない（メニュー項目が出ない）。
- Dynamo for Revit は「Revit MCP Switch」等ではなく Revit の Dynamo リボンから起動する通常構成。

## 知りたいこと（この順で）

### 1. ViewExtension のパッケージ構成（3.6 / Revit 2026）
- `IViewExtension` を自作パッケージからロードさせる正しい方法。
  - 定義ファイルは `<Name>_ViewExtensionDefinition.xml` か。ルート要素・要素名
    （`AssemblyPath` / `TypeName` / その他必須属性）の**実物 XML**。
  - 置き場所: パッケージ内の `viewExtensions\` フォルダか、`extra\` か、両方可か。
    `IExtension` の `extra\*_ExtensionDefinition.xml` との違い。
  - `pkg.json` に ViewExtension 用の記述が要るか（`contents` / `node_libraries` 等）。
  - `bin\` に置く DLL、依存 DLL（`DynamoCoreWpf` は Private=false でよいか）。
- Dynamo 本体インストールの `viewExtensions\` フォルダ
  （例 `C:\Program Files\Autodesk\Revit 2026\AddIns\DynamoForRevit\viewExtensions\`）に
  直接置く方式との違い・優先順位・権限。ユーザー配布前提ならどちらが推奨か。
- DynamoSamples の `SampleViewExtension` が採用しているパッケージ構成（フォルダツリーと XML）。

### 2. ロードされたか確認する方法
- ロード成否がどのログに出るか（`%AppData%\...\Dynamo\Logs\` か Dynamo Console か）。
- 失敗時の典型的メッセージ（アセンブリ解決失敗 / 型不一致 / バージョン不一致）。

### 3. `IViewExtension` 最小スケルトン
次を満たす **コンパイルできる最小 C#** を提示:
- `Startup(ViewStartupParams)` / `Loaded(ViewLoadedParams)` / `Shutdown()` / `Dispose()` / `UniqueId` / `Name`
- `Loaded` で:
  - メニュー項目を1つ追加（`ViewLoadedParams.AddMenuItem` / `AddSeparator` の正しい使い方、`MenuBarType`）
  - サイドバー（拡張機能パネル）に WPF `UserControl` を1枚追加
    （`ViewLoadedParams.AddToExtensionsSideBar(...)` の正確なシグネチャ、
    引数の型 `ViewExtensionSideBarData` 等、パネルにタイトルを付ける方法）
- `ViewLoadedParams` から取れるもの一覧（`DynamoWindow`, `DynamoViewModel` 相当,
  `CommandExecutive`, `ViewModelCommandExecutive`, library の ViewModel、現在の workspace）。
  特に `DynamoViewModel` へのアクセス経路（`DynamoWindow.DataContext` で良いか）。

### 4. 同一プロセスの既存 `IExtension` との連携
- この ViewExtension を既存の `DynamoMcpExtension`（`IExtension`）と
  **同じパッケージ**に同居させられるか（1つの `pkg.json` に `IExtension` と `IViewExtension` の
  両方の定義を置く）。それとも別パッケージにすべきか。
- ViewExtension から、同プロセスにロード済みの `DynamoMcpExtension.DynamoBridge`（静的シングルトン）を
  参照して呼べるか（プロジェクト参照 or リフレクション）。

## 出力
- **項目1〜3を満たす「動く最小 ViewExtension」**: パッケージのフォルダツリー（全ファイル）+
  `*_ViewExtensionDefinition.xml` の実物 + `pkg.json` の実物 + `MyViewExtension.cs` の全文 +
  最小 `UserControl`（xaml/cs）。
- 3.6.2 でのソース/サンプル根拠。
- 「配置してから Dynamo で確認するまでの手順」を箇条書きで。
