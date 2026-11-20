using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using Newtonsoft.Json.Linq;
using revit_mcp_plugin.Core;

namespace revit_mcp_plugin.ExternalAi
{
    /// <summary>
    /// 外部設定：把 Sinotech MCP 登錄到本機的 Codex、Claude Code、Claude Desktop。
    /// 無模式視窗，不阻擋 Revit；只修改所選用戶端的 sinotech_revit 項目，寫入前備份。
    /// 不會代為登入 AI、不會略過用戶端的工具授權提示，也不會開關外部AI服務。
    /// </summary>
    public sealed class ExternalAiSetupWindow : Window
    {
        private static ExternalAiSetupWindow current;
        private readonly Dictionary<ExternalAiClient, CheckBox> checks = new Dictionary<ExternalAiClient, CheckBox>();
        private readonly Dictionary<ExternalAiClient, TextBlock> states = new Dictionary<ExternalAiClient, TextBlock>();
        private readonly TextBlock service = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6) };
        private readonly TextBlock source = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DimGray, FontSize = 11.5 };
        private readonly TextBox log = new TextBox
        {
            IsReadOnly = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            MinHeight = 90, MaxHeight = 170, Margin = new Thickness(0, 8, 0, 0), Background = new SolidColorBrush(Color.FromRgb(250, 250, 250))
        };
        private readonly Button apply = Button("套用設定");
        private readonly Button remove = Button("移除設定");
        private readonly Button test = Button("測試 Revit 連線");
        private readonly Button copy = Button("複製手動設定");
        private ExternalAiEntry entry;

        /// <summary>開啟（或帶到最前面）設定視窗</summary>
        public static void ShowSingle(IntPtr owner, string reason = null)
        {
            if (current != null)
            {
                if (current.WindowState == WindowState.Minimized) current.WindowState = WindowState.Normal;
                current.Activate();
                if (reason != null) current.Write(reason);
                return;
            }
            current = new ExternalAiSetupWindow();
            if (owner != IntPtr.Zero) new WindowInteropHelper(current).Owner = owner;
            current.Closed += (_, __) => current = null;
            current.Show();
            if (reason != null) current.Write(reason);
        }

        private static string Year => McpEndpoints.RevitVersion;

        private static Button Button(string caption) => new Button { Content = caption, Padding = new Thickness(10, 5, 10, 5), Margin = new Thickness(0, 0, 6, 4), MinHeight = 30 };

        private ExternalAiSetupWindow()
        {
            Title = $"外部 AI 連線設定 — 中興 Sinotech MCP（Revit {Year}）";
            Width = 600; SizeToContent = SizeToContent.Height; MaxHeight = 820;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            ResizeMode = ResizeMode.CanResizeWithGrip;
            FontFamily = new FontFamily("Microsoft JhengHei UI"); FontSize = 13;
            Background = new SolidColorBrush(Color.FromRgb(246, 248, 251));

            var body = new StackPanel { Margin = new Thickness(16) };
            body.Children.Add(new TextBlock { Text = "外部 AI 連線設定", FontSize = 19, FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(Color.FromRgb(25, 56, 86)), Margin = new Thickness(0, 0, 0, 6) });
            body.Children.Add(new TextBlock
            {
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8),
                Text = $"讓本機的 Codex、Claude Code、Claude Desktop 透過 Sinotech MCP 查詢與修改 Revit {Year} 的模型。" +
                       $"勾選要使用的 AI 程式後按「套用設定」；只新增／更新 {ExternalAiEntry.NameFor(Year)} 項目（並移除舊版 {ExternalAiEntry.LegacyName}），" +
                       "其他設定保留，原檔會先備份。每個 Revit 年份各有一個項目與埠號，可同時設定、同時使用。"
            });
            body.Children.Add(service);

            var group = new GroupBox { Header = "AI 用戶端", Padding = new Thickness(8), Margin = new Thickness(0, 4, 0, 6) };
            var clients = new StackPanel();
            foreach (ExternalAiClient client in Enum.GetValues(typeof(ExternalAiClient)))
            {
                var check = new CheckBox { Content = ExternalAiClients.ClientName(client), FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 4, 0, 0) };
                var state = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(22, 2, 0, 4), FontSize = 11.5, Foreground = Brushes.DimGray };
                checks[client] = check; states[client] = state;
                clients.Children.Add(check); clients.Children.Add(state);
            }
            group.Content = clients;
            body.Children.Add(group);
            body.Children.Add(source);

            var actions = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
            actions.Children.Add(apply); actions.Children.Add(remove); actions.Children.Add(test); actions.Children.Add(copy);
            var close = Button("關閉"); close.Click += (_, __) => Close(); actions.Children.Add(close);
            body.Children.Add(actions);

            body.Children.Add(new TextBlock
            {
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0), FontSize = 12, Foreground = new SolidColorBrush(Color.FromRgb(70, 70, 70)),
                Text = "使用步驟：\n" +
                       "1. 先完全結束要設定的 AI 程式（Claude Desktop 也要從系統匣結束），再按「套用設定」。\n" +
                       "2. 在 Revit 點選「外部AI服務」使其為「開」，按「測試 Revit 連線」確認。AI助手是另一個獨立連線，開關 AI助手不影響外部 AI。\n" +
                       "3. 重新啟動 AI 程式並確認 MCP 已載入：Codex「設定 → MCP」、Claude Code 輸入 /mcp、Claude Desktop「Settings → Developer」。\n" +
                       $"4. 先輸入唯讀測試：「請用 {ExternalAiEntry.NameFor(Year)} 讀取目前 Revit 視圖資訊，先不要修改模型。」\n" +
                       $"注意：外部AI服務使用本機埠號 {McpEndpoints.ExternalPort(Year)}（20800＋年份）與本機權杖；同一年份同時只能有一個 Revit 開啟外部AI服務。" +
                       "AI 程式仍需各自登入並核准工具使用，模型修改可用 Revit 復原。"
            });
            body.Children.Add(log);
            Content = new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };

            apply.Click += (_, __) => Run(false);
            remove.Click += (_, __) => Run(true);
            test.Click += async (_, __) =>
            {
                test.IsEnabled = false;
                try { Write(await ExternalAiSource.TestConnection(Year)); }
                catch (Exception ex) { Write("連線測試失敗：" + ex.Message); }
                finally { test.IsEnabled = true; RefreshState(); }
            };
            copy.Click += (_, __) =>
            {
                if (entry == null) return;
                var json = new JObject { ["mcpServers"] = new JObject { [entry.Name] = entry.ToJson(false) } };
                try { Clipboard.SetText(json.ToString()); Write("已複製 JSON 設定，可手動合併到 AI 用戶端的 mcpServers。"); }
                catch (Exception ex) { Write("無法複製到剪貼簿：" + ex.Message); }
            };
            McpServiceController.ExternalChanged += OnExternalChanged;
            Closed += (_, __) => McpServiceController.ExternalChanged -= OnExternalChanged;
            Loaded += (_, __) => Initialize();
        }

        private void OnExternalChanged(bool _) => Dispatcher.BeginInvoke(new Action(RefreshState));

        private void Initialize()
        {
            try
            {
                entry = ExternalAiSource.Resolve(Year);
                source.Text = "MCP 啟動方式：\n" + entry.Command + "\n" + entry.Script + "\n連線設定（埠號與權杖，只存在本機）：\n" + entry.ConfigPath;
            }
            catch (Exception ex)
            {
                entry = null;
                source.Text = "找不到 MCP 執行環境：" + ex.Message;
                apply.IsEnabled = copy.IsEnabled = false;
            }
            foreach (var pair in checks)
            {
                // 預設勾選已安裝、但尚未正確設定的用戶端
                pair.Value.IsChecked = entry != null && ExternalAiClients.LooksInstalled(pair.Key) && !ExternalAiClients.IsConfigured(pair.Key, entry);
            }
            RefreshState();
        }

        private void RefreshState()
        {
            var external = SocketService.Instance.External;
            int port = external.IsRunning ? external.Port : McpEndpoints.LoadOrCreateExternal(Year).Port;
            service.Text = $"Revit {Year} 外部AI服務：{(external.IsRunning ? "開（監聽中）" : "關")}　本機埠號 127.0.0.1:{port}";
            foreach (var client in checks.Keys)
            {
                var paths = ExternalAiClients.ConfigPaths(client);
                string status;
                if (entry == null) status = "無法檢查";
                else if (ExternalAiClients.IsConfigured(client, entry)) status = "已設定 ✔";
                else if (ExternalAiClients.HasEntry(client, entry.Name)) status = $"已有 {entry.Name}，但內容與目前安裝不同（套用後更新）";
                else if (ExternalAiClients.HasEntry(client, ExternalAiEntry.LegacyName)) status = $"只有舊版 {ExternalAiEntry.LegacyName}（8080），請重新套用設定";
                else status = ExternalAiClients.LooksInstalled(client) ? "尚未設定" : "尚未設定（未偵測到此程式，仍可先寫入設定）";
                if (ExternalAiClients.IsRunning(client)) status += "；程式正在執行，套用前請完全結束";
                states[client].Text = status + "\n" + string.Join("\n", paths);
            }
        }

        private void Run(bool removing)
        {
            if (entry == null && !removing) return;
            var selected = checks.Where(p => p.Value.IsChecked == true).Select(p => p.Key).ToList();
            if (selected.Count == 0) { Write("請至少勾選一個 AI 用戶端。"); return; }
            try
            {
                var target = entry ?? new ExternalAiEntry { Name = ExternalAiEntry.NameFor(Year), Command = "", Script = "", ConfigPath = "" };
                var plan = ExternalAiClients.Plan(selected, target, removing);
                var changes = plan.Where(p => p.Changed).ToList();
                if (changes.Count == 0)
                {
                    Write(removing ? $"所選用戶端沒有 {target.Name} 設定，不需移除。" : "所選用戶端已是最新設定，不需變更。");
                    return;
                }
                var running = selected.Where(ExternalAiClients.IsRunning).Select(ExternalAiClients.ClientName).ToList();
                string question = (removing ? $"將從下列設定檔移除 {target.Name}（及舊版 {ExternalAiEntry.LegacyName}）：\n" : "將寫入下列設定檔（原檔會備份為 .bak）：\n") +
                    string.Join("\n", changes.Select(c => "• " + ExternalAiClients.ClientName(c.Client) + "：" + c.Path)) +
                    (running.Count == 0 ? "" : "\n\n" + string.Join("、", running) + " 正在執行，可能會覆寫設定；建議先完全結束再繼續。") +
                    "\n\n是否繼續？";
                if (MessageBox.Show(this, question, Title, MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
                ExternalAiClients.Apply(plan);
                foreach (var item in changes)
                    Write((removing ? "已移除：" : "已設定：") + ExternalAiClients.ClientName(item.Client) + "　" + item.Path +
                          (item.Backup == null ? "" : "\n　備份：" + item.Backup));
                Write(removing ? "完成。重新啟動 AI 程式後生效。" : "完成。請重新啟動 AI 程式，並在 Revit 開啟「外部AI服務」後使用。");
            }
            catch (Exception ex)
            {
                Write((removing ? "移除失敗：" : "套用失敗：") + ex.Message);
            }
            RefreshState();
        }

        private void Write(string text)
        {
            log.AppendText((log.Text.Length == 0 ? "" : "\n") + DateTime.Now.ToString("HH:mm:ss ") + text);
            log.ScrollToEnd();
        }

        /// <summary>是否有任一用戶端已正確設定（首次啟用外部AI服務時，沒有設定才自動開啟本視窗）</summary>
        public static bool AnyConfigured()
        {
            try
            {
                var entry = ExternalAiSource.Resolve(Year);
                return Enum.GetValues(typeof(ExternalAiClient)).Cast<ExternalAiClient>().Any(c => ExternalAiClients.IsConfigured(c, entry));
            }
            catch (Exception) { return true; } // 無法判斷時不主動打擾
        }
    }
}
