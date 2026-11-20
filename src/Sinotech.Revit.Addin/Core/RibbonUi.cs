using System;
using System.Linq;
using System.Reflection;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Autodesk.Revit.UI;
using revit_mcp_plugin.ExternalAi;

namespace revit_mcp_plugin.Core
{
    internal static class RibbonUi
    {
        internal const string TabName = "中興MCP";
        internal const string PanelName = "MCP";
        // Internal ID is separate from the displayed name.
        internal const string AiAssistantButtonId = "Sinotech.MCP.ShowAiAssistant";
        internal const string ExternalAiButtonId = "Sinotech.MCP.ExternalAiService";
        internal const string ExternalSetupButtonId = "Sinotech.MCP.ExternalAiSetup";
        private static PushButton externalAiButton;

        /// <summary>Creates the tab/panel (shared if they already exist) and returns the panel.</summary>
        internal static RibbonPanel GetOrCreatePanel(UIControlledApplication app)
        {
            try { app.CreateRibbonTab(TabName); }
            catch (Autodesk.Revit.Exceptions.ArgumentException)
            {
                // Tab already exists (another Sinotech add-in); reuse it.
            }
            return app.GetRibbonPanels(TabName).FirstOrDefault(p => p.Name == PanelName)
                ?? app.CreateRibbonPanel(TabName, PanelName);
        }

        internal static void AddAiAssistantButton(RibbonPanel panel)
        {
            if (panel.GetItems().Any(item => item.Name == AiAssistantButtonId)) return;

            var assembly = Assembly.GetExecutingAssembly();
            var data = new PushButtonData(AiAssistantButtonId, "AI助手", assembly.Location,
                typeof(ShowAiAssistantCommand).FullName)
            {
                // The supplied 32px PNG is used as LargeImage only (no 16px Image).
                LargeImage = RibbonImages.LoadImage("Sinotech.Mcp.Images.AiAssistant.png"),
                ToolTip = "【功能介紹】\n在 Revit 內使用 Codex、Claude 或 Gemini 與 AI 助手對話，透過 MCP 查詢並編修目前的 Revit 模型。",
                LongDescription = "【使用步驟】\n1. 開啟 Revit 專案，點選「AI助手」開啟對話面板。\n2. 選擇 AI 引擎並登入（Codex／Claude／Gemini）。\n3. 輸入需求，AI 透過 MCP 工具讀取或修改模型。\n4. 修改模型前請先核對預覽內容；需要時可使用 Revit Undo 還原，並自行儲存模型。",
                ToolTipImage = RibbonImages.LoadImage("Sinotech.Mcp.Images.AiAssistant.Original.png", 250)
            };
            panel.AddItem(data);
        }

        /// <summary>「外部AI服務」開關與「外部設定」按鈕</summary>
        internal static void AddExternalAiButtons(RibbonPanel panel)
        {
            var assembly = Assembly.GetExecutingAssembly();
            externalAiButton = panel.GetItems().FirstOrDefault(item => item.Name == ExternalAiButtonId) as PushButton;
            if (externalAiButton == null)
            {
                externalAiButton = panel.AddItem(new PushButtonData(ExternalAiButtonId, "外部AI\n服務：關", assembly.Location,
                    typeof(ExternalAiCommand).FullName)
                {
                    LargeImage = RibbonImages.WithStatus(RibbonImages.LoadImage(ExternalAiImage), OffColor),
                    ToolTipImage = RibbonImages.LoadImage("Sinotech.Mcp.Images.ExternalAi.Original.png", 250),
                    ToolTip = "【功能介紹】\n開關供外部 Codex、Claude Code、Claude Desktop 使用的本機 MCP 服務，讓外部 AI 查詢與修改目前的 Revit 模型。不需開啟 AI助手；每次啟動 Revit 預設關閉。",
                    LongDescription = "【使用步驟】\n1. 點選使按鈕顯示「服務：開」。首次使用且尚未設定 AI 程式時，會自動開啟「外部設定」。\n" +
                        "2. 在「外部設定」勾選 Codex／Claude Code／Claude Desktop 並套用，重新啟動該 AI 程式。\n" +
                        "3. 在 AI 程式中先要求「讀取目前 Revit 視圖資訊，不要修改模型」確認連線。\n" +
                        "4. 再次點選即可關閉；關閉 AI助手不會關閉本服務。外部AI服務與 AI助手是兩個獨立連線，外部 AI 只能在本服務開啟時連線（本機埠號 20800＋年份，例如 2025＝20825，需權杖）。"
                }) as PushButton;
            }
            if (!panel.GetItems().Any(item => item.Name == ExternalSetupButtonId))
                panel.AddItem(new PushButtonData(ExternalSetupButtonId, "外部\n設定", assembly.Location, typeof(ExternalAiSetupCommand).FullName)
                {
                    LargeImage = RibbonImages.LoadImage("Sinotech.Mcp.Images.ExternalSetup.png"),
                    ToolTipImage = RibbonImages.LoadImage("Sinotech.Mcp.Images.ExternalSetup.Original.png", 250),
                    ToolTip = "【功能介紹】\n快速把 Sinotech MCP 設定到本機的 Codex、Claude Code、Claude Desktop；只修改本年份的 sinotech_revit_<年份> 項目並先備份，保留其他設定。",
                    LongDescription = "【使用步驟】\n1. 先完全結束要設定的 AI 程式。\n2. 點選「外部設定」，勾選 AI 程式後按「套用設定」。\n" +
                        "3. 開啟「外部AI服務」後按「測試 Revit 連線」。\n4. 重新啟動 AI 程式即可使用；不需要時可按「移除設定」。\n" +
                        "不會代為登入 AI 帳號，也不會略過 AI 程式的工具授權提示。"
                });
            McpServiceController.ExternalChanged -= SetExternalAiState;
            McpServiceController.ExternalChanged += SetExternalAiState;
        }

        private const string ExternalAiImage = "Sinotech.Mcp.Images.ExternalAi.png";
        private static readonly Color OffColor = Color.FromRgb(150, 156, 166);
        private static readonly Color OnColor = Color.FromRgb(31, 160, 76);

        internal static void SetExternalAiState(bool enabled)
        {
            if (externalAiButton == null) return;
            externalAiButton.ItemText = "外部AI\n服務：" + (enabled ? "開" : "關");
            externalAiButton.LargeImage = RibbonImages.WithStatus(RibbonImages.LoadImage(ExternalAiImage), enabled ? OnColor : OffColor);
        }
    }

    internal static class RibbonImages
    {
        /// <summary>在 32×32 圖示右下角加上狀態圓點（綠＝服務開、灰＝關）</summary>
        internal static BitmapSource WithStatus(BitmapSource icon, Color status)
        {
            var visual = new DrawingVisual();
            using (var context = visual.RenderOpen())
            {
                context.DrawImage(icon, new System.Windows.Rect(0, 0, 32, 32));
                context.DrawEllipse(new SolidColorBrush(status), new Pen(Brushes.White, 1.5), new System.Windows.Point(26, 26), 4.5, 4.5);
            }
            var bitmap = new RenderTargetBitmap(32, 32, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            bitmap.Freeze();
            return bitmap;
        }

        internal static BitmapSource LoadImage(string resource, int width = 0)
        {
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resource)
                ?? throw new InvalidOperationException("Missing ribbon image: " + resource))
            {
                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                if (width > 0) image.DecodePixelWidth = width;
                image.StreamSource = stream;
                image.EndInit();
                // The PNG carries 300 DPI: 32 pixels would then occupy only ~10 WPF
                // units in the ribbon. Normalize the in-memory DPI to 96 without
                // touching the file or its pixels, so it shows at 32x32 units.
                var pixelsSource = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
                int stride = pixelsSource.PixelWidth * 4;
                var pixels = new byte[stride * pixelsSource.PixelHeight];
                pixelsSource.CopyPixels(pixels, stride, 0);
                var source = BitmapSource.Create(pixelsSource.PixelWidth, pixelsSource.PixelHeight,
                    96, 96, PixelFormats.Bgra32, null, pixels, stride);
                source.Freeze();
                return source;
            }
        }
    }
}
