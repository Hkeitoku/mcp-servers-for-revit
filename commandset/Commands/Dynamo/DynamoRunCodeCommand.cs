// 配置先: commandset/Commands/Dynamo/DynamoRunCodeCommand.cs

using System;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPSDK.API.Base;
using RevitMCPCommandSet.Services;

namespace RevitMCPCommandSet.Commands.Dynamo
{
    public class DynamoRunCodeCommand : ExternalEventCommandBase
    {
        private static readonly object _executionLock = new object();
        private DynamoRunCodeEventHandler _handler => (DynamoRunCodeEventHandler)Handler;

        public override string CommandName => "dynamo_run_code";

        public DynamoRunCodeCommand(UIApplication uiApp)
            : base(new DynamoRunCodeEventHandler(), uiApp)
        {
        }

        public override object Execute(JObject parameters, string requestId)
        {
            lock (_executionLock)
            {
                string code = parameters?["code"]?.ToString();
                if (string.IsNullOrWhiteSpace(code))
                    throw new ArgumentException("Parameter 'code' (DesignScript text) is required.");

                double x = parameters?["x"]?.ToObject<double>() ?? 0;
                double y = parameters?["y"]?.ToObject<double>() ?? 0;

                _handler.DesignScriptCode = code;
                _handler.X = x;
                _handler.Y = y;

                if (!RaiseAndWaitForCompletion(15000))
                    throw new TimeoutException("Dynamo run code operation timed out. Is a Dynamo graph open?");

                if (_handler.ErrorMessage != null)
                    throw new Exception($"Dynamo run code failed: {_handler.ErrorMessage}");

                return new { success = true, nodeId = _handler.ResultNodeId };
            }
        }
    }
}
