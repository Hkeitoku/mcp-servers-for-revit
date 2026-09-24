// 配置先: commandset/Commands/Dynamo/DynamoOpCommand.cs
//
// 複数ノードグラフ操作の汎用コマンド。
// parameters (op / nodes / edges / nodeId / path ...) をそのまま JSON 文字列にして
// DynamoBridge.ExecuteBatch に渡し、返ってきた JSON をそのまま返す。
// リフレクション境界は DynamoOpEventHandler の ExecuteBatch 1 メソッドのみ。

using System;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPSDK.API.Base;
using RevitMCPCommandSet.Services;

namespace RevitMCPCommandSet.Commands.Dynamo
{
    public class DynamoOpCommand : ExternalEventCommandBase
    {
        private static readonly object _executionLock = new object();
        private DynamoOpEventHandler _handler => (DynamoOpEventHandler)Handler;

        public override string CommandName => "dynamo_op";

        public DynamoOpCommand(UIApplication uiApp)
            : base(new DynamoOpEventHandler(), uiApp)
        {
        }

        public override object Execute(JObject parameters, string requestId)
        {
            lock (_executionLock)
            {
                if (parameters == null)
                    throw new ArgumentException("Parameters are required.");

                // op が無ければ build_graph 扱い。
                if (parameters["op"] == null)
                    parameters["op"] = "build_graph";

                _handler.RequestJson = parameters.ToString(Newtonsoft.Json.Formatting.None);

                // run+wait は RefreshCompleted 待ちで長くなり得るので余裕を持たせる。
                var timeoutMs = parameters["wait"]?.ToObject<bool>() == true ? 60000 : 30000;
                if (!RaiseAndWaitForCompletion(timeoutMs))
                    throw new TimeoutException("Dynamo op timed out. Is a Dynamo Home graph open?");

                if (_handler.ErrorMessage != null)
                    throw new Exception($"Dynamo op failed: {_handler.ErrorMessage}");

                // ExecuteBatch は常に JSON 文字列を返す。パースして返却。
                try
                {
                    return JToken.Parse(_handler.ResultJson ?? "{}");
                }
                catch
                {
                    return new { ok = true, raw = _handler.ResultJson };
                }
            }
        }
    }
}
