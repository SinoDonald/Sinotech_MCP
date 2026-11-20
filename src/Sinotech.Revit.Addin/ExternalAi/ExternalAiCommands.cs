using System;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace revit_mcp_plugin.ExternalAi
{
    /// <summary>
    /// 「外部AI服務」：開關供外部 Codex、Claude 使用的本機 MCP 服務（127.0.0.1:20800＋年份，與 AI助手的連線互相獨立）。
    /// 首次啟用且尚未設定任何 AI 用戶端時，自動開啟「外部設定」視窗。
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    public sealed class ExternalAiCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            try
            {
                bool enabled = McpServiceController.ToggleExternal(commandData.Application);
                if (enabled && !ExternalAiSetupWindow.AnyConfigured())
                    ExternalAiSetupWindow.ShowSingle(commandData.Application.MainWindowHandle,
                        "外部AI服務已開啟。尚未偵測到 AI 用戶端設定，請勾選要使用的 AI 程式後按「套用設定」。");
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                message = "外部AI服務切換失敗：" + ex.Message;
                return Result.Failed;
            }
        }
    }

    /// <summary>
    /// 「外部設定」：開啟外部 AI 連線設定視窗（Codex、Claude Code、Claude Desktop）；不切換服務開關
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    public sealed class ExternalAiSetupCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            try
            {
                ExternalAiSetupWindow.ShowSingle(commandData.Application.MainWindowHandle);
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                message = "外部設定無法開啟：" + ex.Message;
                return Result.Failed;
            }
        }
    }
}
