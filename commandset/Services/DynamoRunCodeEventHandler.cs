// 配置先: commandset/Services/DynamoRunCodeEventHandler.cs
//
// 重要: DynamoMcpExtension.dll への直接のプロジェクト参照は持たない。
// Revitプロセス内で「Dynamoが既に読み込んでいる」DynamoMcpExtensionアセンブリを
// リフレクションで見つけ、その中のDynamoBridge.Instanceを操作する。
// こうしないと、commandset自身が持つ別コピーのDLLを読み込んでしまい、
// Dynamo側のDynamoBridge(シングルトン)とは別インスタンスになってしまう。

using System;
using System.Linq;
using System.Threading;
using Autodesk.Revit.UI;
using RevitMCPSDK.API.Interfaces;

namespace RevitMCPCommandSet.Services
{
    public class DynamoRunCodeEventHandler : IExternalEventHandler, IWaitableExternalEventHandler
    {
        private readonly ManualResetEvent _resetEvent = new ManualResetEvent(false);

        public string DesignScriptCode { get; set; }
        public double X { get; set; }
        public double Y { get; set; }

        public string ResultNodeId { get; private set; }
        public string ErrorMessage { get; private set; }

        public bool WaitForCompletion(int timeoutMilliseconds = 10000)
        {
            _resetEvent.Reset();
            return _resetEvent.WaitOne(timeoutMilliseconds);
        }

        public void Execute(UIApplication app)
        {
            try
            {
                ErrorMessage = null;

                // 現在のプロセス(=Revit.exe)内に既に読み込まれているアセンブリの中から
                // "DynamoMcpExtension" という名前のものを探す(Dynamoがパッケージとして
                // 起動時に読み込んでいるはず)。
                var dynamoMcpAssembly = AppDomain.CurrentDomain.GetAssemblies()
                    .FirstOrDefault(a => a.GetName().Name == "DynamoMcpExtension");

                if (dynamoMcpAssembly == null)
                {
                    ErrorMessage = "DynamoMcpExtension assembly not found in current process. " +
                                    "Is Dynamo open with the package loaded?";
                    return;
                }

                var bridgeType = dynamoMcpAssembly.GetType("DynamoMcpExtension.DynamoBridge");
                if (bridgeType == null)
                {
                    ErrorMessage = "DynamoBridge type not found in DynamoMcpExtension assembly.";
                    return;
                }

                // DynamoBridge.Instance (静的プロパティ) を取得
                var instanceProp = bridgeType.GetProperty("Instance",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                var bridgeInstance = instanceProp.GetValue(null);

                // IsReady プロパティを確認
                var isReadyProp = bridgeType.GetProperty("IsReady");
                bool isReady = (bool)isReadyProp.GetValue(bridgeInstance);
                if (!isReady)
                {
                    ErrorMessage = "DynamoBridge is not ready. Is a Dynamo graph open?";
                    return;
                }

                // CreateCodeBlockNode(string, double, double) メソッドを呼び出す
                var method = bridgeType.GetMethod("CreateCodeBlockNode");
                var guid = method.Invoke(bridgeInstance, new object[] { DesignScriptCode, X, Y });

                ResultNodeId = guid.ToString();
            }
            catch (System.Reflection.TargetInvocationException tie)
            {
                // リフレクション経由の例外は InnerException に本体が入る
                ErrorMessage = tie.InnerException?.Message ?? tie.Message;
            }
            catch (Exception ex)
            {
                ErrorMessage = ex.Message;
            }
            finally
            {
                _resetEvent.Set();
            }
        }

        public string GetName()
        {
            return "Dynamo Run Code";
        }
    }
}
