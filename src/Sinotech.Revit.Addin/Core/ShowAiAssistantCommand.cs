using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using revit_mcp_plugin.Chat;
using revit_mcp_plugin.ExternalAi;

namespace revit_mcp_plugin.Core
{
    [Transaction(TransactionMode.Manual)]
    public class ShowAiAssistantCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            try
            {
                // AI助手開啟時啟動 AI助手專用的監聽器、關閉時停止（面板事件也會處理，這裡再確保一次）；
                // 不影響外部AI服務
                var service = SocketService.Instance;
                service.Initialize(commandData.Application);
                var pane = commandData.Application.GetDockablePane(ChatDockablePane.Id);
                if (pane.IsShown())
                {
                    pane.Hide();
                    McpServiceController.SetPaneShown(false);
                }
                else
                {
                    McpServiceController.SetPaneShown(true);
                    pane.Show();
                }
                return Result.Succeeded;
            }
            catch (System.Exception ex)
            {
                message = ex.Message;
                return Result.Failed;
            }
        }
    }
}
