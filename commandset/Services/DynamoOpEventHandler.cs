// 配置先: commandset/Services/DynamoOpEventHandler.cs
//
// DynamoMcpExtension.dll への直接参照は持たない。
// Revit プロセス内に Dynamo が既に読み込んでいる DynamoMcpExtension アセンブリを
// リフレクションで見つけ、DynamoBridge.Instance.ExecuteBatch(json) を 1 回だけ呼ぶ。
// これがこの機能群の唯一のリフレクション境界。

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autodesk.Revit.UI;
using RevitMCPSDK.API.Interfaces;

namespace RevitMCPCommandSet.Services
{
    public class DynamoOpEventHandler : IExternalEventHandler, IWaitableExternalEventHandler
    {
        private readonly ManualResetEvent _resetEvent = new ManualResetEvent(false);
        private bool _deferred;

        /// <summary>ExecuteBatch に渡す JSON リクエスト文字列。</summary>
        public string RequestJson { get; set; }

        /// <summary>ExecuteBatch が返した JSON レスポンス文字列。</summary>
        public string ResultJson { get; private set; }

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
                ResultJson = null;
                _deferred = false;

                var dynamoMcpAssembly = AppDomain.CurrentDomain.GetAssemblies()
                    .FirstOrDefault(a => a.GetName().Name == "DynamoMcpExtension");

                if (dynamoMcpAssembly == null)
                {
                    ErrorMessage = "DynamoMcpExtension assembly not found in current process. " +
                                   "Is Dynamo open with the MCP Bridge package loaded?";
                    return;
                }

                var bridgeType = dynamoMcpAssembly.GetType("DynamoMcpExtension.DynamoBridge");
                if (bridgeType == null)
                {
                    ErrorMessage = "DynamoBridge type not found in DynamoMcpExtension assembly.";
                    return;
                }

                var instanceProp = bridgeType.GetProperty("Instance",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                var bridgeInstance = instanceProp.GetValue(null);

                var isReadyProp = bridgeType.GetProperty("IsReady");
                bool isReady = (bool)isReadyProp.GetValue(bridgeInstance);
                if (!isReady)
                {
                    ErrorMessage = "DynamoBridge is not ready. Is a Dynamo Home graph open?";
                    return;
                }

                var method = bridgeType.GetMethod("ExecuteBatch", new[] { typeof(string) });
                if (method == null)
                {
                    ErrorMessage = "DynamoBridge.ExecuteBatch(string) not found. " +
                                   "Rebuild and redeploy DynamoMcpExtension.dll.";
                    return;
                }

                ResultJson = (string)method.Invoke(bridgeInstance, new object[] { RequestJson });

                // op:"run" with wait:true → ExecuteBatch は RunCancelCommand を発行しただけで
                // {"pending":true} を返す。UI スレッドをブロックせず、別スレッドで
                // RefreshCompleted (= DynamoBridge.WaitRun) を待ってから完了シグナル。
                if (ResultJson != null && ResultJson.Contains("\"pending\":true"))
                {
                    var waitRun = bridgeType.GetMethod("WaitRun", new[] { typeof(int) });
                    if (waitRun != null)
                    {
                        _deferred = true;
                        var inst = bridgeInstance;
                        var outcomeProp = bridgeType.GetProperty("LastRunOutcome");
                        var errProp = bridgeType.GetProperty("LastRunError");
                        Task.Run(() =>
                        {
                            try
                            {
                                bool done = (bool)waitRun.Invoke(inst, new object[] { 20000 });
                                var outcome = outcomeProp?.GetValue(inst) as string ?? (done ? "done" : "timeout");
                                var runErr = errProp?.GetValue(inst) as string;
                                if (runErr != null)
                                    ResultJson = "{\"ok\":false,\"waited\":" + (done ? "true" : "false") +
                                                 ",\"outcome\":\"" + outcome + "\",\"graphError\":" +
                                                 Newtonsoft.Json.JsonConvert.ToString(runErr) + "}";
                                else
                                    ResultJson = "{\"ok\":true,\"waited\":" + (done ? "true" : "false") +
                                                 ",\"outcome\":\"" + outcome + "\"}";
                            }
                            catch (Exception ex)
                            {
                                ErrorMessage = "run wait failed: " +
                                    ((ex as System.Reflection.TargetInvocationException)?.InnerException?.Message ?? ex.Message);
                            }
                            finally { _resetEvent.Set(); }
                        });
                    }
                }
            }
            catch (System.Reflection.TargetInvocationException tie)
            {
                ErrorMessage = tie.InnerException?.Message ?? tie.Message;
            }
            catch (Exception ex)
            {
                ErrorMessage = ex.Message;
            }
            finally
            {
                if (!_deferred) _resetEvent.Set();
            }
        }

        public string GetName()
        {
            return "Dynamo Op";
        }
    }
}
