using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using revit_mcp_plugin.Core;

namespace revit_mcp_plugin.ExternalAi
{
    /// <summary>MCP 程式連到 Revit 所需的埠號與權杖（以 SINOTECH_MCP_CONFIG 指向的 JSON 檔提供）</summary>
    public sealed class McpEndpoint
    {
        [JsonProperty("port")] public int Port { get; set; }
        [JsonProperty("token")] public string Token { get; set; }
        [JsonProperty("revitVersion")] public string RevitVersion { get; set; }
    }

    /// <summary>
    /// 埠號規劃（多個 Revit 年份、AI助手／外部AI服務任意組合都不衝突）：
    /// <list type="bullet">
    /// <item>AI助手：每次啟動 Revit 由系統指定空閒埠號、產生新權杖，寫入 %LOCALAPPDATA%\Sinotech\McpInternal\revit-&lt;PID&gt;.json，只交給 AI助手的背景程式</item>
    /// <item>外部AI服務：20800＋Revit 年份（2024＝20824、2025＝20825、2026＝20826），避開 TYBIM_MCP 的 17824～17826、21824～21826；
    /// 埠號與權杖保存在 %LOCALAPPDATA%\Sinotech\McpExternal\&lt;年份&gt;\external.json，可手動修改埠號</item>
    /// </list>
    /// 權杖只存在目前 Windows 使用者的本機資料夾，不寫入安裝檔或 AI 用戶端設定檔。
    /// </summary>
    public static class McpEndpoints
    {
        public const int ExternalPortBase = 20800;

        /// <summary>目前 Revit 年份（例如 "2025"），由 Application.OnStartup 設定</summary>
        public static string RevitVersion { get; internal set; } = "";

        private static string Root => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Sinotech");

        // ------------------------------------------------------------ AI助手（內部）

        private static string _internalToken;

        /// <summary>本次 Revit 工作階段的內部權杖（每次啟動 Revit 重新產生）</summary>
        public static string InternalToken => _internalToken ?? (_internalToken = SocketService.NewToken());

        /// <summary>本 Revit 程序的內部連線檔（同年份開兩個 Revit 也各自獨立）</summary>
        public static string InternalSessionPath => Path.Combine(Root, "McpInternal", "revit-" + Process.GetCurrentProcess().Id + ".json");

        public static void WriteInternalSession(int port)
        {
            Write(InternalSessionPath, new McpEndpoint { Port = port, Token = InternalToken, RevitVersion = RevitVersion });
        }

        public static void DeleteInternalSession()
        {
            try { if (File.Exists(InternalSessionPath)) File.Delete(InternalSessionPath); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        // ------------------------------------------------------------ 外部AI服務

        public static string ExternalConfigPath(string year) => Path.Combine(Root, "McpExternal", year, "external.json");

        /// <summary>讀取外部AI服務設定；不存在或權杖無效時建立（埠號 20800＋年份、新權杖）</summary>
        public static McpEndpoint LoadOrCreateExternal(string year)
        {
            var path = ExternalConfigPath(year);
            McpEndpoint endpoint = null;
            if (File.Exists(path))
            {
                try { endpoint = JsonConvert.DeserializeObject<McpEndpoint>(File.ReadAllText(path)); }
                catch (JsonException) { endpoint = null; }
            }
            bool changed = false;
            if (endpoint == null) { endpoint = new McpEndpoint(); changed = true; }
            if (endpoint.Port < 1024 || endpoint.Port > 65535) { endpoint.Port = ExternalPort(year); changed = true; }
            if (string.IsNullOrEmpty(endpoint.Token) || endpoint.Token.Length < 32) { endpoint.Token = SocketService.NewToken(); changed = true; }
            if (endpoint.RevitVersion != year) { endpoint.RevitVersion = year; changed = true; }
            if (changed) Write(path, endpoint);
            return endpoint;
        }

        /// <summary>外部AI服務預設埠號：20800＋年份後兩碼（2024→20824、2025→20825、2026→20826）</summary>
        public static int ExternalPort(string year) =>
            int.TryParse(year, out int y) && y >= 2000 && y < 2100 ? ExternalPortBase + (y - 2000) : ExternalPortBase;

        private static void Write(string path, McpEndpoint endpoint)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(temp, JsonConvert.SerializeObject(endpoint, Formatting.Indented), new UTF8Encoding(false));
            if (File.Exists(path)) File.Replace(temp, path, null);
            else File.Move(temp, path);
        }
    }
}
