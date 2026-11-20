using System;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using revit_mcp_plugin.Chat;
using revit_mcp_plugin.Core;

namespace revit_mcp_plugin.ExternalAi
{
    /// <summary>
    /// AI助手與外部AI服務的兩個監聽器各自開關、互不影響：
    /// AI助手面板顯示時開啟內部監聽（動態埠號），關閉面板即停止；
    /// 外部AI服務只由按鈕開關（固定埠號 20800＋年份），每次啟動 Revit 預設關閉。
    /// </summary>
    public static class McpServiceController
    {
        public static bool PaneShown { get; private set; }

        /// <summary>外部AI服務是否真的在監聽（按鈕狀態以此為準）</summary>
        public static bool ExternalEnabled => SocketService.Instance.External.IsRunning;

        /// <summary>外部AI服務狀態改變時通知（Ribbon 按鈕更新文字與圖示）</summary>
        public static event Action<bool> ExternalChanged;

        /// <summary>AI助手面板顯示狀態改變（以 Revit 的 DockablePane.IsShown 為準）</summary>
        public static void SetPaneShown(bool shown)
        {
            PaneShown = shown;
            var service = SocketService.Instance;
            if (!service.IsInitialized) return;
            var listener = service.Internal;
            try
            {
                if (!shown) { listener.Stop(); return; }
                if (listener.IsRunning) return;
                // 優先沿用本次 Revit 先前的埠號，AI助手背景程式不需重新取得；被占用時改由系統指定
                int previous = listener.Port;
                try { listener.Start(previous, McpEndpoints.InternalToken); }
                catch (InvalidOperationException) when (previous != 0) { listener.Start(0, McpEndpoints.InternalToken); }
                McpEndpoints.WriteInternalSession(listener.Port);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine("Sinotech MCP internal listener failed: " + ex.Message);
            }
        }

        /// <summary>切換外部AI服務；啟用失敗（例如埠號被占用）時丟出例外並維持關閉</summary>
        public static bool ToggleExternal(UIApplication uiApp)
        {
            var service = SocketService.Instance;
            if (service.External.IsRunning)
            {
                service.External.Stop();
                ExternalChanged?.Invoke(false);
                return false;
            }
            service.Initialize(uiApp);
            var endpoint = McpEndpoints.LoadOrCreateExternal(McpEndpoints.RevitVersion);
            try { service.External.Start(endpoint.Port, endpoint.Token); }
            catch (InvalidOperationException ex)
            {
                ExternalChanged?.Invoke(false);
                throw new InvalidOperationException(
                    $"{ex.Message}\n可能已有另一個 Revit {McpEndpoints.RevitVersion} 開啟外部AI服務；請先關閉該服務，" +
                    $"或修改 {McpEndpoints.ExternalConfigPath(McpEndpoints.RevitVersion)} 的 port 後，在「外部設定」重新套用。");
            }
            ExternalChanged?.Invoke(true);
            return true;
        }

        public static void Shutdown()
        {
            try { SocketService.Instance.StopAll(); } catch (Exception) { }
            McpEndpoints.DeleteInternalSession();
        }
    }

    /// <summary>
    /// 外部 AI 設定來源：目前安裝（或開發目錄）內的 node.exe、MCP Server 腳本與本年份外部連線設定檔
    /// </summary>
    public static class ExternalAiSource
    {
        public static ExternalAiEntry Resolve(string year)
        {
            var launch = ChatLaunchSettings.Load();
            // ChatLaunchSettings 找的是 build/chat/index.js；外部用戶端使用上一層的 MCP Server（build/index.js）
            var script = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(launch.RuntimeScript)), "index.js"));
            if (!File.Exists(script)) throw new FileNotFoundException("找不到 MCP Server（runtime/build/index.js），請確認安裝完整。", script);
            McpEndpoints.LoadOrCreateExternal(year);
            return new ExternalAiEntry
            {
                Name = ExternalAiEntry.NameFor(year),
                Command = Path.GetFullPath(launch.NodeExecutable),
                Script = script,
                ConfigPath = McpEndpoints.ExternalConfigPath(year)
            };
        }

        /// <summary>
        /// 以唯讀命令 get_current_view_info，透過外部AI服務的埠號與權杖確認連線（非同步，不可在 Revit 主執行緒同步等待）
        /// </summary>
        public static async Task<string> TestConnection(string year, int timeoutMs = 15000)
        {
            var endpoint = McpEndpoints.LoadOrCreateExternal(year);
            using (var client = new TcpClient())
            {
                var connect = client.ConnectAsync("127.0.0.1", endpoint.Port);
                if (await Task.WhenAny(connect, Task.Delay(3000)).ConfigureAwait(false) != connect || connect.IsFaulted || !client.Connected)
                    throw new InvalidOperationException($"無法連到外部AI服務（127.0.0.1:{endpoint.Port}）：請先在 Revit {year} 點選「外部AI服務」使其為「開」。");
                var stream = client.GetStream();
                var request = JsonConvert.SerializeObject(new { jsonrpc = "2.0", method = "get_current_view_info", @params = new { }, id = Guid.NewGuid().ToString("N"), token = endpoint.Token });
                var bytes = Encoding.UTF8.GetBytes(request);
                await stream.WriteAsync(bytes, 0, bytes.Length).ConfigureAwait(false);
                var buffer = new byte[65536];
                var received = new StringBuilder();
                var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
                while (DateTime.UtcNow < deadline)
                {
                    var read = stream.ReadAsync(buffer, 0, buffer.Length);
                    if (await Task.WhenAny(read, Task.Delay(Math.Max(1, (int)(deadline - DateTime.UtcNow).TotalMilliseconds))).ConfigureAwait(false) != read) break;
                    int count = await read.ConfigureAwait(false);
                    if (count == 0) break;
                    received.Append(Encoding.UTF8.GetString(buffer, 0, count));
                    try
                    {
                        var response = JObject.Parse(received.ToString());
                        if (response["error"] != null) throw new InvalidOperationException("Revit 回應錯誤：" + response["error"]?["message"]);
                        var view = response["result"]?["name"] ?? response["result"]?["Name"];
                        return $"已透過外部AI服務（127.0.0.1:{endpoint.Port}）連線到 Revit {year}" + (view == null ? "" : $"（目前視圖：{view}）") + "。";
                    }
                    catch (JsonReaderException) { /* 資料尚未收完 */ }
                }
                throw new TimeoutException("已連到服務但 Revit 沒有回應：請結束 Revit 中正在進行的指令或關閉對話框後再試。");
            }
        }
    }
}
