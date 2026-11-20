using System;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;
using revit_mcp_plugin.Chat;
using revit_mcp_plugin.ExternalAi;

namespace revit_mcp_plugin.Core
{
    public class Application : IExternalApplication
    {
        private ChatDockablePane chat;
        private UIApplication uiApp;

        public Result OnStartup(UIControlledApplication application)
        {
            McpEndpoints.RevitVersion = application.ControlledApplication.VersionNumber;
            chat = new ChatDockablePane();
            application.RegisterDockablePane(ChatDockablePane.Id, "中興 AI 助手", chat);
            // Revit 還原面板時不一定觸發 DockableFrameVisibilityChanged，所以面板內容顯示／隱藏時也重新確認；
            // 判斷一律以 Revit 的 DockablePane.IsShown() 為準。面板在啟動版面配置時可能短暫顯示後又隱藏，
            // 只看 WPF 的 IsVisible 會誤開 AI助手的服務（1.2.x 的問題）。
            chat.Panel.IsVisibleChanged += (_, __) => RefreshPane();

            // Revit 啟動完成後載入所有已安裝命令；AI助手的監聽器隨面板開關，外部AI服務只由按鈕開關
            application.ControlledApplication.ApplicationInitialized += OnApplicationInitialized;
            application.DockableFrameVisibilityChanged += OnDockableFrameVisibilityChanged;

            RibbonPanel mcpPanel = RibbonUi.GetOrCreatePanel(application);
            RibbonUi.AddAiAssistantButton(mcpPanel);
            RibbonUi.AddExternalAiButtons(mcpPanel);

            return Result.Succeeded;
        }

        private void OnApplicationInitialized(object sender, Autodesk.Revit.DB.Events.ApplicationInitializedEventArgs e)
        {
            try
            {
                uiApp = new UIApplication(sender as Autodesk.Revit.ApplicationServices.Application);
                SocketService.Instance.Initialize(uiApp);
                RefreshPane();
            }
            catch (Exception ex)
            {
                // 失敗時按 AI助手 會再嘗試初始化
                System.Diagnostics.Trace.WriteLine("Sinotech MCP initialization failed: " + ex.Message);
            }
        }

        // 以 Revit 回報的面板狀態決定 AI助手監聽器；Revit 尚未初始化完成時由 OnApplicationInitialized 再確認
        private void RefreshPane()
        {
            if (uiApp == null) return;
            try { McpServiceController.SetPaneShown(uiApp.GetDockablePane(ChatDockablePane.Id).IsShown()); }
            catch (Exception ex) { System.Diagnostics.Trace.WriteLine("Sinotech MCP pane check failed: " + ex.Message); }
        }

        private void OnDockableFrameVisibilityChanged(object sender, DockableFrameVisibilityChangedEventArgs e)
        {
            if (e.PaneId.Guid != ChatDockablePane.Id.Guid) return;
            McpServiceController.SetPaneShown(e.DockableFrameShown);
        }

        public Result OnShutdown(UIControlledApplication application)
        {
            application.DockableFrameVisibilityChanged -= OnDockableFrameVisibilityChanged;
            McpServiceController.Shutdown();
            chat?.Panel.Dispose();
            return Result.Succeeded;
        }
    }
}
