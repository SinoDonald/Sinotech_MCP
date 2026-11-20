using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace revit_mcp_plugin.ExternalAi
{
    /// <summary>外部 AI 用戶端種類</summary>
    public enum ExternalAiClient { Codex, ClaudeCode, ClaudeDesktop }

    /// <summary>
    /// 外部 MCP 伺服器項目（每個 Revit 年份一個，例如 sinotech_revit_2025）：用安裝目錄內附的 node.exe 啟動
    /// runtime/build/index.js（stdio），該程序依 SINOTECH_MCP_CONFIG 指向的本機設定檔取得外部AI服務的埠號與權杖再連到 Revit。
    /// 用戶端設定檔只寫入路徑，不寫入權杖、API Key 或登入資料。
    /// </summary>
    public sealed class ExternalAiEntry
    {
        /// <summary>1.2.x 以前的單一項目名稱（連 8080），套用或移除時一併清除</summary>
        public const string LegacyName = "sinotech_revit";
        public const string ConfigVariable = "SINOTECH_MCP_CONFIG";

        public static string NameFor(string year) => LegacyName + "_" + year;

        public string Name { get; set; }
        public string Command { get; set; }
        public string Script { get; set; }
        /// <summary>外部AI服務設定檔（埠號＋權杖）的路徑</summary>
        public string ConfigPath { get; set; }

        public JObject ToJson(bool claudeCode)
        {
            var entry = new JObject();
            if (claudeCode) entry["type"] = "stdio";
            entry["command"] = Command;
            entry["args"] = new JArray(Script);
            entry["env"] = new JObject { [ConfigVariable] = ConfigPath };
            return entry;
        }
    }

    /// <summary>一個要修改的用戶端設定檔</summary>
    public sealed class ExternalAiPlanItem
    {
        public ExternalAiClient Client { get; set; }
        public string Path { get; set; }
        public bool Exists { get; set; }
        public string Original { get; set; }
        public string Contents { get; set; }
        public bool Changed => !string.Equals(Original, Contents, StringComparison.Ordinal);
        public string Backup { get; set; }
    }

    /// <summary>
    /// 讀寫 Codex（config.toml）、Claude Code（.claude.json）、Claude Desktop（claude_desktop_config.json）的 MCP 設定：
    /// 只新增／取代／移除 sinotech_revit 項目，保留其他設定；寫入前備份、原子取代，任一檔失敗時復原本次已寫入的檔案。
    /// </summary>
    public static class ExternalAiClients
    {
        public static string ClientName(ExternalAiClient client) =>
            client == ExternalAiClient.Codex ? "Codex" : client == ExternalAiClient.ClaudeCode ? "Claude Code" : "Claude Desktop";

        // ------------------------------------------------------------------ 設定檔位置

        public static List<string> ConfigPaths(ExternalAiClient client)
        {
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            switch (client)
            {
                case ExternalAiClient.Codex:
                    var codexHome = Environment.GetEnvironmentVariable("CODEX_HOME");
                    return new List<string> { Path.Combine(string.IsNullOrWhiteSpace(codexHome) ? Path.Combine(home, ".codex") : codexHome, "config.toml") };
                case ExternalAiClient.ClaudeCode:
                    var claudeHome = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
                    return new List<string> { Path.Combine(string.IsNullOrWhiteSpace(claudeHome) ? home : claudeHome, ".claude.json") };
                default:
                    return ClaudeDesktopPaths();
            }
        }

        // Claude Desktop：傳統 %APPDATA%\Claude、新版 %LOCALAPPDATA%\Claude-Data\claude*、Microsoft Store 版的 LocalCache
        private static List<string> ClaudeDesktopPaths()
        {
            string roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var directories = new List<string>();
            try
            {
                var profiles = Path.Combine(local, "Claude-Data");
                if (Directory.Exists(profiles))
                    directories.AddRange(Directory.GetDirectories(profiles).Where(d => Regex.IsMatch(Path.GetFileName(d), "^claude(?:[_-]|$)", RegexOptions.IgnoreCase)));
                var packages = Path.Combine(local, "Packages");
                if (Directory.Exists(packages))
                    foreach (var package in Directory.GetDirectories(packages, "Claude_*"))
                    {
                        var directory = Path.Combine(package, "LocalCache", "Roaming", "Claude");
                        if (Directory.Exists(directory) && Directory.GetFiles(directory, ".user-data-left*").Length == 0) directories.Add(directory);
                    }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            var legacy = Path.Combine(roaming, "Claude");
            if (Directory.Exists(legacy) || directories.Count == 0) directories.Add(legacy);
            return directories.Select(d => Path.GetFullPath(Path.Combine(d, "claude_desktop_config.json")))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        /// <summary>用戶端程式是否安裝（只用於提示，不影響是否可寫入設定）</summary>
        public static bool LooksInstalled(ExternalAiClient client)
        {
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            switch (client)
            {
                case ExternalAiClient.Codex:
                    return Directory.Exists(Path.GetDirectoryName(ConfigPaths(client)[0])) || OnPath("codex.exe") || OnPath("codex.cmd") ||
                        Directory.Exists(Path.Combine(local, "OpenAI", "Codex"));
                case ExternalAiClient.ClaudeCode:
                    return File.Exists(ConfigPaths(client)[0]) || OnPath("claude.exe") || OnPath("claude.cmd") ||
                        File.Exists(Path.Combine(home, ".local", "bin", "claude.exe"));
                default:
                    return ConfigPaths(client).Any(p => Directory.Exists(Path.GetDirectoryName(p))) ||
                        Directory.Exists(Path.Combine(local, "AnthropicClaude"));
            }
        }

        private static bool OnPath(string file)
        {
            foreach (var entry in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
            {
                try { if (File.Exists(Path.Combine(entry.Trim('"'), file))) return true; }
                catch (ArgumentException) { }
            }
            return false;
        }

        /// <summary>用戶端是否正在執行（套用前提醒完全結束，避免用戶端覆寫設定）</summary>
        public static bool IsRunning(ExternalAiClient client)
        {
            string[] names = client == ExternalAiClient.Codex ? new[] { "codex", "Codex" }
                : client == ExternalAiClient.ClaudeCode ? new string[0] : new[] { "claude", "Claude" };
            try { return names.Any(n => Process.GetProcessesByName(n).Length > 0); }
            catch (InvalidOperationException) { return false; }
        }

        // ------------------------------------------------------------------ 目前狀態

        /// <summary>設定檔中已有與 entry 完全相同的項目，且沒有舊版 sinotech_revit 項目</summary>
        public static bool IsConfigured(ExternalAiClient client, ExternalAiEntry entry)
        {
            var paths = ConfigPaths(client).Where(File.Exists).ToList();
            if (paths.Count == 0) return false;
            try
            {
                return paths.All(path =>
                {
                    var item = BuildItem(client, path, entry, false);
                    return !item.Changed;
                });
            }
            catch (Exception) { return false; }
        }

        /// <summary>設定檔中有指定名稱的項目（不論內容是否相同）</summary>
        public static bool HasEntry(ExternalAiClient client, string name)
        {
            foreach (var path in ConfigPaths(client).Where(File.Exists))
            {
                try
                {
                    var text = ReadText(path);
                    if (client == ExternalAiClient.Codex ? Regex.IsMatch(text, CodexTablePattern(name)) :
                        ParseObject(text)["mcpServers"]?[name] != null) return true;
                }
                catch (Exception) { }
            }
            return false;
        }

        // ------------------------------------------------------------------ 產生修改計畫

        public static List<ExternalAiPlanItem> Plan(IEnumerable<ExternalAiClient> clients, ExternalAiEntry entry, bool remove)
        {
            var plan = new List<ExternalAiPlanItem>();
            foreach (var client in clients.Distinct())
                foreach (var path in ConfigPaths(client))
                {
                    if (!remove || File.Exists(path)) plan.Add(BuildItem(client, path, entry, remove));
                }
            return plan;
        }

        private static ExternalAiPlanItem BuildItem(ExternalAiClient client, string path, ExternalAiEntry entry, bool remove)
        {
            bool exists = File.Exists(path);
            if (exists && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("設定檔是符號連結，請人工確認實體位置：" + path);
            string original = exists ? ReadText(path) : "";
            string contents = client == ExternalAiClient.Codex
                ? UpdateCodex(original, entry, remove)
                : UpdateJson(original, entry, remove, client == ExternalAiClient.ClaudeCode, path);
            return new ExternalAiPlanItem { Client = client, Path = path, Exists = exists, Original = original, Contents = contents };
        }

        private static string ReadText(string path) => File.ReadAllText(path, new UTF8Encoding(false, true));

        // [mcp_servers.<名稱>] 與其子表格（例如 [mcp_servers.<名稱>.env]）；名稱需完全相符，sinotech_revit 不會比對到 sinotech_revit_2025
        private static string CodexTablePattern(string name)
        {
            string n = Regex.Escape(name);
            return @"(?ms)^[ \t]*\[\s*mcp_servers\s*\.\s*(?:" + n + "|\"" + n + "\"|'" + n +
                   @"')\s*(?:\.[^\]\r\n]+)?\][^\r\n]*\r?\n.*?(?=^[ \t]*\[|\z)";
        }

        /// <summary>
        /// 只改寫 Codex TOML 中自己的 [mcp_servers.sinotech_revit_&lt;年份&gt;] 表格（含 env 子表格），並移除舊版 [mcp_servers.sinotech_revit]，
        /// 其他內容逐字保留；遇到多行字串、行內 MCP 表格等無法安全判斷的格式時拒絕，不冒險改寫
        /// </summary>
        public static string UpdateCodex(string text, ExternalAiEntry entry, bool remove)
        {
            text = text ?? "";
            if (text.Contains("\"\"\"") || text.Contains("'''"))
                throw new InvalidOperationException("Codex config.toml 含多行字串，請先人工檢查；未修改任何設定。");
            var names = new[] { entry.Name, ExternalAiEntry.LegacyName };
            foreach (var n in names.Select(Regex.Escape))
                if (Regex.IsMatch(text, @"(?m)^\s*(?:mcp_servers\s*=|mcp_servers\s*\.\s*""?" + n + @"""?\s*=)"))
                    throw new InvalidOperationException("Codex config.toml 使用行內 MCP 表格格式，請先人工檢查；未修改任何設定。");
            if (Regex.IsMatch(text, @"(?m)^\s*\[\[?\s*[""']mcp_servers[""']"))
                throw new InvalidOperationException("Codex config.toml 使用特殊 MCP 表格格式，請先人工檢查；未修改任何設定。");
            string normalized = text.Length == 0 ? "" : text.TrimEnd() + "\r\n";
            bool found = names.Any(n => Regex.IsMatch(normalized, CodexTablePattern(n)));
            string without = names.Aggregate(normalized, (current, n) => Regex.Replace(current, CodexTablePattern(n), ""));
            if (remove) return found ? without.TrimEnd() + (without.Trim().Length == 0 ? "" : "\r\n") : text;
            string block = "[mcp_servers." + entry.Name + "]\r\n" +
                "command = " + TomlString(entry.Command) + "\r\n" +
                "args = [" + TomlString(entry.Script) + "]\r\n" +
                "enabled = true\r\n" +
                "startup_timeout_sec = 30\r\n" +
                "tool_timeout_sec = 240\r\n\r\n" +
                "[mcp_servers." + entry.Name + ".env]\r\n" +
                ExternalAiEntry.ConfigVariable + " = " + TomlString(entry.ConfigPath) + "\r\n";
            string head = without.TrimEnd();
            string updated = (head.Length == 0 ? "" : head + "\r\n\r\n") + block;
            // 內容相同時回傳原文，避免無意義的改寫與備份
            return string.Equals(updated.TrimEnd(), text.TrimEnd(), StringComparison.Ordinal) ? text : updated;
        }

        // 不把日期字串轉成 DateTime，避免改寫用戶端設定中的時間戳記格式
        private static JObject ParseObject(string text)
        {
            using (var reader = new JsonTextReader(new StringReader(text)) { DateParseHandling = DateParseHandling.None, FloatParseHandling = FloatParseHandling.Decimal })
            {
                var token = JToken.ReadFrom(reader);
                if (reader.Read() && reader.TokenType != JsonToken.Comment) throw new JsonReaderException("JSON 結尾有多餘內容");
                return token as JObject ?? throw new JsonReaderException("JSON 最外層必須是物件");
            }
        }

        // JSON 字串的跳脫規則與 TOML 基本字串相容（反斜線、引號、控制字元）
        private static string TomlString(string value) => JsonConvert.ToString(value);

        /// <summary>
        /// 修改 JSON 設定的 mcpServers.sinotech_revit_&lt;年份&gt;，並移除舊版 mcpServers.sinotech_revit；
        /// 其他項目原樣保留（未變更時回傳原文）
        /// </summary>
        public static string UpdateJson(string text, ExternalAiEntry entry, bool remove, bool claudeCode, string path = "")
        {
            JObject config;
            if (string.IsNullOrWhiteSpace(text)) config = new JObject();
            else
            {
                try { config = ParseObject(text); }
                catch (JsonException) { throw new InvalidOperationException("JSON 格式無效，請先修正或還原設定檔；未修改任何設定：" + path); }
            }
            var servers = config["mcpServers"];
            if (servers != null && servers.Type != JTokenType.Object)
                throw new InvalidOperationException("mcpServers 不是物件，未修改：" + path);
            var existing = servers as JObject;
            bool legacy = existing?[ExternalAiEntry.LegacyName] != null;
            if (remove)
            {
                if (existing == null || (existing[entry.Name] == null && !legacy)) return text;
                existing.Remove(entry.Name);
                existing.Remove(ExternalAiEntry.LegacyName);
                return config.ToString(Formatting.Indented) + "\r\n";
            }
            var wanted = entry.ToJson(claudeCode);
            if (existing != null && !legacy && JToken.DeepEquals(existing[entry.Name], wanted)) return text;
            if (existing == null) { existing = new JObject(); config["mcpServers"] = existing; }
            existing.Remove(ExternalAiEntry.LegacyName);
            existing[entry.Name] = wanted;
            return config.ToString(Formatting.Indented) + "\r\n";
        }

        // ------------------------------------------------------------------ 套用

        /// <summary>
        /// 依計畫寫入：檢查檔案在產生計畫後未被改動，先備份再原子取代；任一檔失敗時復原本次已寫入的檔案
        /// </summary>
        public static void Apply(IList<ExternalAiPlanItem> plan)
        {
            var completed = new List<ExternalAiPlanItem>();
            string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            try
            {
                foreach (var item in plan.Where(i => i.Changed))
                {
                    bool existsNow = File.Exists(item.Path);
                    if (existsNow != item.Exists || existsNow && ReadText(item.Path) != item.Original)
                        throw new InvalidOperationException("設定檔在檢查後被其他程式修改，請先完全結束該 AI 程式再重試：" + item.Path);
                    Directory.CreateDirectory(Path.GetDirectoryName(item.Path));
                    string temp = Path.Combine(Path.GetDirectoryName(item.Path), ".sinotech-" + Guid.NewGuid().ToString("N") + ".tmp");
                    try
                    {
                        File.WriteAllText(temp, item.Contents, new UTF8Encoding(false));
                        if (item.Exists)
                        {
                            item.Backup = item.Path + ".before-sinotech-" + stamp + ".bak";
                            File.Replace(temp, item.Path, item.Backup);
                        }
                        else File.Move(temp, item.Path);
                        completed.Add(item);
                    }
                    finally { if (File.Exists(temp)) File.Delete(temp); }
                    if (ReadText(item.Path) != item.Contents)
                        throw new InvalidOperationException("寫入後讀回內容不一致，請先完全結束該 AI 程式再重試：" + item.Path);
                }
            }
            catch (Exception error)
            {
                var problems = new List<string>();
                for (int i = completed.Count - 1; i >= 0; i--)
                {
                    var item = completed[i];
                    try
                    {
                        if (item.Exists) File.Copy(item.Backup, item.Path, true);
                        else File.Delete(item.Path);
                    }
                    catch (Exception rollback) { problems.Add(item.Path + "：" + rollback.Message); }
                }
                throw new InvalidOperationException(error.Message + (problems.Count == 0
                    ? "\n本次已寫入的設定已復原。"
                    : "\n下列設定未能自動復原，請用同資料夾的 .bak 備份還原：\n" + string.Join("\n", problems)));
            }
            // 全部成功後才清理：每個設定檔只保留本次的備份，刪除更早的 .before-sinotech-*.bak，避免越積越多
            foreach (var item in completed) PruneBackups(item.Path, item.Backup);
        }

        /// <summary>刪除設定檔旁由本工具產生的舊備份，只保留 keep（可為 null 表示全部刪除）</summary>
        public static void PruneBackups(string path, string keep)
        {
            try
            {
                var directory = Path.GetDirectoryName(path);
                if (!Directory.Exists(directory)) return;
                foreach (var backup in Directory.GetFiles(directory, Path.GetFileName(path) + ".before-sinotech-*.bak"))
                    if (keep == null || !string.Equals(Path.GetFullPath(backup), Path.GetFullPath(keep), StringComparison.OrdinalIgnoreCase))
                        File.Delete(backup);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { } // 清理失敗不影響已完成的設定
        }
    }
}
