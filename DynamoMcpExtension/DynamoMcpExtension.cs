// DynamoMcpExtension.cs
// 配置場所: プロジェクト "DynamoMcpExtension"
//
// IExtension 本体。パッケージ経由で読み込まれる (管理者権限不要)。
// IViewExtensionSource を実装することで、コード側から動的にメニュー項目を追加できる。
// XML マニフェスト不要。
//
// DynamoModel の取得:
//   ReadyParams からは DynamoModel を取れないため、動的登録した View Extension の
//   Loaded(ViewLoadedParams) から DynamoWindow.DataContext (= DynamoViewModel) 経由で
//   DynamoModel を取得し DynamoBridge へ渡す。これがライブラリノード検索に必要。

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using Dynamo.Extensions;
using Dynamo.Graph.Workspaces;
using Dynamo.Models;
using Dynamo.ViewModels;
using Dynamo.Wpf.Extensions;

namespace DynamoMcpExtension
{
    public class DynamoMcpExtensionMain : IExtension, IViewExtensionSource
    {
        public string UniqueId => "8f3c2e10-9b4a-4c7e-8b2d-1a2b3c4d5e6f";
        public string Name => "Dynamo MCP Bridge";

        // IViewExtensionSource の実装
        public event Func<string, IViewExtension> RequestLoadExtension;
        public event Action<IViewExtension> RequestAddExtension;
        public IEnumerable<IViewExtension> RequestedExtensions { get; } = new List<IViewExtension>();

        public void Startup(StartupParams sp)
        {
        }

        public void Ready(ReadyParams p)
        {
            DynamoBridge.Instance.ExtensionUniqueId = UniqueId;
            DynamoBridge.Instance.ExtensionName = Name;
            // ReadyParams 自体を保持 (CurrentWorkspaceModel は live getter。.dyn を開くと差し替わる)。
            DynamoBridge.Instance.Attach(p);

            // 保険: View Extension も一応登録 (発火すれば Loaded で Model を取得)。
            RequestAddExtension?.Invoke(new BridgeViewExtension());
        }

        public void Shutdown()
        {
            DynamoBridge.Instance.Detach();
        }

        public void Dispose()
        {
            DynamoBridge.Instance.Detach();
        }
    }

    /// <summary>
    /// DynamoModel を DynamoBridge へ渡し、動作確認メニューを追加する View Extension。
    /// </summary>
    internal class BridgeViewExtension : IViewExtension
    {
        public string UniqueId => "3d2f6a90-4b1c-4e5e-9a7f-2c8b1d3e4f5a";
        public string Name => "Dynamo MCP Bridge View";

        public void Startup(ViewStartupParams p) { }

        public void Loaded(ViewLoadedParams p)
        {
            // DynamoModel を取得して Bridge に渡す (ライブラリノード検索に必要)。
            try
            {
                if (p.DynamoWindow?.DataContext is DynamoViewModel dvm)
                    DynamoBridge.Instance.Model = dvm.Model;
            }
            catch { /* 取得できなくても codeblock ノードは動く */ }

            // --- 動作確認メニュー: 単一ノード ---
            var single = new MenuItem { Header = "MCP Bridge: テストノード作成 (10 + 5)" };
            single.Click += (s, a) =>
            {
                try
                {
                    var id = DynamoBridge.Instance.CreateCodeBlockNode("10 + 5;", 0, 0);
                    MessageBox.Show("ノードを作成しました。\nNodeId: " + id, "DynamoMcpExtension");
                }
                catch (Exception ex)
                {
                    MessageBox.Show("失敗:\n" + ex.Message, "DynamoMcpExtension");
                }
            };
            p.AddExtensionMenuItem(single);

            // --- 動作確認メニュー: 複数ノード + 結線 (Phase 0 PoC) ---
            var multi = new MenuItem { Header = "MCP Bridge: 複数ノード結線テスト ((1..5) -> x*x)" };
            multi.Click += (s, a) =>
            {
                try
                {
                    var req = @"{
                        ""op"":""build_graph"",
                        ""nodes"":[
                          {""id"":""a"",""kind"":""codeblock"",""code"":""(1..5);"",""x"":0,""y"":0},
                          {""id"":""b"",""kind"":""codeblock"",""code"":""x*x;"",""x"":320,""y"":0}
                        ],
                        ""edges"":[{""from"":""a"",""fromPort"":0,""to"":""b"",""toPort"":0}]
                    }";
                    var res = DynamoBridge.Instance.ExecuteBatch(req);
                    MessageBox.Show(res, "DynamoMcpExtension build_graph");
                }
                catch (Exception ex)
                {
                    MessageBox.Show("失敗:\n" + ex.Message, "DynamoMcpExtension");
                }
            };
            p.AddExtensionMenuItem(multi);
        }

        public void Shutdown() { }
        public void Dispose() { }
    }
}
