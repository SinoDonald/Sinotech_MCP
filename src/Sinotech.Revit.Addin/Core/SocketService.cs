using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RevitMCPSDK.API.Models.JsonRPC;
using RevitMCPSDK.API.Interfaces;
using revit_mcp_plugin.Configuration;
using revit_mcp_plugin.Utils;

namespace revit_mcp_plugin.Core
{
    /// <summary>
    /// MCP 命令主機：載入命令並執行 JSON-RPC 要求。對外提供兩個互相獨立的本機（127.0.0.1）監聽器：
    /// <list type="bullet">
    /// <item>Internal：AI助手專用，每次啟動 Revit 向系統取得空閒埠號與新的權杖，只寫入本機工作階段檔交給 AI助手的背景程式</item>
    /// <item>External：外部 Codex／Claude 專用，固定埠號（預設 20800＋Revit 年份）與保存在本機的權杖，只在「外部AI服務」開啟時監聽</item>
    /// </list>
    /// 每個要求都必須帶有該監聽器的權杖，否則拒絕執行。
    /// </summary>
    public class SocketService
    {
        private static SocketService _instance;
        private readonly ICommandRegistry _commandRegistry;
        private readonly ILogger _logger;
        private UIApplication _uiApp;

        public static SocketService Instance => _instance ?? (_instance = new SocketService());

        private SocketService()
        {
            _commandRegistry = new RevitCommandRegistry();
            _logger = new Logger();
            Internal = new McpListener("AI助手", this);
            External = new McpListener("外部AI服務", this);
        }

        /// <summary>AI助手專用監聽器（動態埠號）</summary>
        public McpListener Internal { get; }

        /// <summary>外部 AI 用戶端專用監聽器（固定埠號）</summary>
        public McpListener External { get; }

        // 命令只在 Revit 執行期間載入一次；之後開關 AI助手／外部AI服務只會啟動／停止監聽
        // Commands are loaded once per Revit session; the listeners only start/stop.
        public bool IsInitialized { get; private set; }

        internal ILogger Logger => _logger;

        public void Initialize(UIApplication uiApp)
        {
            if (IsInitialized) return;
            _uiApp = uiApp;
            ExternalEventManager.Instance.Initialize(uiApp, _logger);
            var versionAdapter = new RevitMCPSDK.API.Utils.RevitVersionAdapter(_uiApp.Application);
            _logger.Info("当前 Revit 版本: {0}\nCurrent Revit version: {0}", versionAdapter.GetRevitVersion());

            ConfigurationManager configManager = new ConfigurationManager(_logger);
            configManager.LoadConfiguration();
            CommandManager commandManager = new CommandManager(_commandRegistry, _logger, configManager, _uiApp);
            commandManager.LoadCommands();

            IsInitialized = true;
            _logger.Info("MCP commands loaded.");
        }

        public void StopAll()
        {
            Internal.Stop();
            External.Stop();
        }

        /// <summary>處理一筆 JSON-RPC 要求；權杖不符時不執行任何命令</summary>
        internal string ProcessJsonRPCRequest(string requestJson, string expectedToken)
        {
            JsonRPCRequest request;
            try
            {
                var raw = JObject.Parse(requestJson);
                if (!TokenEquals((string)raw["token"], expectedToken))
                    return CreateErrorResponse((string)raw["id"], -32001,
                        "Unauthorized：連線權杖不符。請在 Revit「外部設定」重新套用設定，並重新啟動 AI 程式。");
                request = raw.ToObject<JsonRPCRequest>();
                if (request == null || !request.IsValid())
                    return CreateErrorResponse(null, JsonRPCErrorCodes.InvalidRequest, "Invalid JSON-RPC request");
                if (!_commandRegistry.TryGetCommand(request.Method, out var command))
                    return CreateErrorResponse(request.Id, JsonRPCErrorCodes.MethodNotFound, $"Method '{request.Method}' not found");
                try
                {
                    object result = command.Execute(request.GetParamsObject(), request.Id);
                    return CreateSuccessResponse(request.Id, result);
                }
                catch (Exception ex)
                {
                    return CreateErrorResponse(request.Id, JsonRPCErrorCodes.InternalError, ex.Message);
                }
            }
            catch (JsonException)
            {
                return CreateErrorResponse(null, JsonRPCErrorCodes.ParseError, "Invalid JSON");
            }
            catch (Exception ex)
            {
                return CreateErrorResponse(null, JsonRPCErrorCodes.InternalError, $"Internal error: {ex.Message}");
            }
        }

        // 固定時間比較，避免以回應時間猜測權杖
        private static bool TokenEquals(string actual, string expected)
        {
            if (string.IsNullOrEmpty(actual) || string.IsNullOrEmpty(expected)) return false;
            var a = Encoding.UTF8.GetBytes(actual);
            var b = Encoding.UTF8.GetBytes(expected);
            int diff = a.Length ^ b.Length;
            for (int i = 0; i < Math.Min(a.Length, b.Length); i++) diff |= a[i] ^ b[i];
            return diff == 0;
        }

        internal static string NewToken()
        {
            var bytes = new byte[32];
            using (var random = RandomNumberGenerator.Create()) random.GetBytes(bytes);
            return Convert.ToBase64String(bytes);
        }

        private static string CreateSuccessResponse(string id, object result)
        {
            return new JsonRPCSuccessResponse { Id = id, Result = result is JToken jToken ? jToken : JToken.FromObject(result) }.ToJson();
        }

        private static string CreateErrorResponse(string id, int code, string message)
        {
            return new JsonRPCErrorResponse { Id = id, Error = new JsonRPCError { Code = code, Message = message } }.ToJson();
        }
    }

    /// <summary>
    /// 一個只接受本機連線的 MCP 監聽器（AI助手或外部AI服務各一個，互不影響）
    /// </summary>
    public sealed class McpListener
    {
        private readonly object _gate = new object();
        private readonly SocketService _host;
        private TcpListener _listener;
        private Thread _thread;
        private volatile bool _running;

        internal McpListener(string name, SocketService host)
        {
            Name = name;
            _host = host;
        }

        public string Name { get; }
        public bool IsRunning => _running;
        /// <summary>實際監聽中的埠號（未啟動時為上次使用的埠號或 0）</summary>
        public int Port { get; private set; }
        public string Token { get; private set; }

        /// <summary>
        /// 啟動監聽；port=0 表示由系統指定空閒埠號。失敗（埠號被占用等）時丟出例外並維持停止狀態
        /// </summary>
        public void Start(int port, string token)
        {
            lock (_gate)
            {
                if (_running) return;
                if (string.IsNullOrEmpty(token)) throw new ArgumentException("缺少連線權杖。", nameof(token));
                var listener = new TcpListener(IPAddress.Loopback, port);
                try { listener.Start(); }
                catch (SocketException ex)
                {
                    _host.Logger.Error("{0} 無法監聽埠號 {1}: {2}", Name, port, ex.Message);
                    throw new InvalidOperationException($"本機埠號 {port} 已被其他程式或另一個 Revit 使用。", ex);
                }
                _listener = listener;
                Port = ((IPEndPoint)listener.LocalEndpoint).Port;
                Token = token;
                _running = true;
                _thread = new Thread(Listen) { IsBackground = true, Name = "Sinotech MCP " + Name };
                _thread.Start();
                _host.Logger.Info("{0} listening on 127.0.0.1:{1}", Name, Port);
            }
        }

        public void Stop()
        {
            lock (_gate)
            {
                if (!_running) return;
                _running = false;
                try { _listener?.Stop(); } catch (SocketException) { }
                _listener = null;
                if (_thread != null && _thread.IsAlive && _thread != Thread.CurrentThread) _thread.Join(1000);
                _thread = null;
                _host.Logger.Info("{0} stopped", Name);
            }
        }

        private void Listen()
        {
            var listener = _listener;
            try
            {
                while (_running)
                {
                    var client = listener.AcceptTcpClient();
                    new Thread(Handle) { IsBackground = true }.Start(client);
                }
            }
            catch (SocketException) { }
            catch (ObjectDisposedException) { }
            catch (InvalidOperationException) { }
        }

        private void Handle(object state)
        {
            var tcpClient = (TcpClient)state;
            try
            {
                var stream = tcpClient.GetStream();
                var buffer = new byte[8192];
                while (_running && tcpClient.Connected)
                {
                    int bytesRead;
                    try { bytesRead = stream.Read(buffer, 0, buffer.Length); }
                    catch (IOException) { break; }
                    if (bytesRead == 0) break;
                    string message = Encoding.UTF8.GetString(buffer, 0, bytesRead);
                    // 停止後才收到的要求不執行，避免關閉服務後仍修改模型
                    string response = _running ? _host.ProcessJsonRPCRequest(message, Token)
                        : "{\"jsonrpc\":\"2.0\",\"id\":null,\"error\":{\"code\":-32002,\"message\":\"" + Name + "已關閉\"}}";
                    var data = Encoding.UTF8.GetBytes(response);
                    stream.Write(data, 0, data.Length);
                }
            }
            catch (Exception) { }
            finally { tcpClient.Close(); }
        }
    }
}
