// ============================================================================
//  Sinotech_MCP 自行開發命令集（SinotechExtensions）
//  開發者：Donald（donaldlu@mail.sinotech.com.tw）
//  Copyright © 2026 開發者保留所有權利。
//  本檔案不適用專案根目錄之 MIT 授權；未經開發者書面授權，
//  不得使用、複製、修改或散布。詳見 src/Sinotech.Revit.Extensions/LICENSE.md
// ============================================================================
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Sinotech.Revit.Extensions.Common;
using RevitMCPSDK.API.Interfaces;

namespace Sinotech.Revit.Extensions.AutoBuild.Services
{
    /// <summary>
    /// 自動接合（auto_join_elements）輸入參數；範圍擇一，不預設整個模型
    /// </summary>
    public class AutoJoinInfo
    {
        /// <summary>明確元素 ID（2～500 個），只處理集合內的配對</summary>
        public List<long> ElementIds { get; set; } = new List<long>();
        /// <summary>目前視圖的主體品類：columns、beams、walls、floors</summary>
        public string HostCategory { get; set; }
        /// <summary>目前視圖的目標品類（可多個）；只處理主體與目標之間（及同品類時彼此）的配對</summary>
        public List<string> TargetCategories { get; set; } = new List<string>();
        /// <summary>join（接合，預設）或 unjoin（取消幾何接合，不停用牆端接合）</summary>
        public string Action { get; set; } = "join";
        /// <summary>preserve（預設，保持既有順序）、floorCuts（樓板切割其他元素）、otherCutsFloor（其他元素切割樓板）</summary>
        public string FloorPriority { get; set; } = "preserve";
        /// <summary>候選範圍容差（mm，0～50，預設 5）；不會移動元素或填補空隙</summary>
        public double ToleranceMm { get; set; } = 5;
        /// <summary>候選配對上限（預設 1000，最大 2000）；超過時拒絕，不先處理一半</summary>
        public int MaxPairs { get; set; } = 1000;
    }

    public class AutoJoinResult
    {
        public string Action { get; set; }
        public int Elements { get; set; }
        public JoinReport Report { get; set; }
    }

    /// <summary>
    /// 自動接合／取消接合柱、梁、牆、板：每組獨立交易，失敗只復原該組；本次成功的操作合併為一筆 Undo
    /// </summary>
    public class AutoJoinElementsEventHandler : IExternalEventHandler, IWaitableExternalEventHandler
    {
        private const int MaxElements = 500;
        private static readonly Dictionary<string, BuiltInCategory[]> Categories = new Dictionary<string, BuiltInCategory[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["columns"] = new[] { BuiltInCategory.OST_StructuralColumns, BuiltInCategory.OST_Columns },
            ["beams"] = new[] { BuiltInCategory.OST_StructuralFraming },
            ["walls"] = new[] { BuiltInCategory.OST_Walls },
            ["floors"] = new[] { BuiltInCategory.OST_Floors }
        };
        private readonly ManualResetEvent _resetEvent = new ManualResetEvent(false);

        public AutoJoinInfo Info { get; private set; }
        public ExtensionResult<AutoJoinResult> Result { get; private set; }

        public void SetParameters(AutoJoinInfo info)
        {
            Info = info;
            _resetEvent.Reset();
        }

        public void Execute(UIApplication uiapp)
        {
            try
            {
                Result = Run(uiapp.ActiveUIDocument.Document);
            }
            catch (Exception ex)
            {
                Result = Fail("自動接合失敗: " + ex.Message);
            }
            finally
            {
                _resetEvent.Set();
            }
        }

        private ExtensionResult<AutoJoinResult> Run(Document doc)
        {
            string action = string.IsNullOrWhiteSpace(Info.Action) ? "join" : Info.Action.Trim().ToLowerInvariant();
            if (action != "join" && action != "unjoin") return Fail("action 只能是 join 或 unjoin。");
            bool join = action == "join";
            string priority = string.IsNullOrWhiteSpace(Info.FloorPriority) ? "preserve" : Info.FloorPriority;
            if (priority != "preserve" && priority != "floorCuts" && priority != "otherCutsFloor")
                return Fail("floorPriority 只能是 preserve、floorCuts 或 otherCutsFloor。");
            if (Info.ToleranceMm < 0 || Info.ToleranceMm > 50) return Fail("toleranceMm 須介於 0～50。");
            int maxPairs = Info.MaxPairs <= 0 ? 1000 : Info.MaxPairs;
            if (maxPairs > 2000) return Fail("maxPairs 上限為 2000。");

            bool byIds = Info.ElementIds != null && Info.ElementIds.Count > 0;
            bool byCategory = !string.IsNullOrWhiteSpace(Info.HostCategory);
            if (byIds == byCategory) return Fail("請擇一指定範圍：elementIds（2～500 個元素 ID），或 hostCategory＋targetCategories（目前視圖）。");

            List<Element> elements;
            Func<Element, Element, bool> pairFilter = null;
            if (byIds)
            {
                var ids = Info.ElementIds.Distinct().ToList();
                if (ids.Count < 2 || ids.Count > MaxElements) return Fail($"elementIds 須為 2～{MaxElements} 個不重複的元素 ID。");
                elements = new List<Element>();
                var invalid = new List<long>();
                foreach (var id in ids)
                {
                    var element = doc.GetElement(ElementIdHelper.FromLong(id));
                    if (element == null || CategoryKey(element) == null) invalid.Add(id);
                    else elements.Add(element);
                }
                if (invalid.Count > 0) return Fail("下列 ID 不存在或不是柱、梁、牆、板：" + string.Join("、", invalid.Take(20)));
            }
            else
            {
                var targets = (Info.TargetCategories ?? new List<string>()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                if (!Categories.ContainsKey(Info.HostCategory) || targets.Count == 0 || targets.Any(t => !Categories.ContainsKey(t)))
                    return Fail("hostCategory、targetCategories 的值只能是 columns、beams、walls、floors，且須指定至少一個目標品類。");
                var wanted = new HashSet<string>(targets, StringComparer.OrdinalIgnoreCase) { Info.HostCategory };
                elements = wanted.SelectMany(key => new FilteredElementCollector(doc, doc.ActiveView.Id)
                        .WherePasses(new ElementMulticategoryFilter(Categories[key])).WhereElementIsNotElementType().ToElements())
                    .GroupBy(e => e.Id).Select(g => g.First()).ToList();
                if (elements.Count > MaxElements)
                    return Fail($"目前視圖相關元素 {elements.Count} 個，超過上限 {MaxElements}，未修改模型；請縮小視圖範圍或改用 elementIds。");
                string host = Info.HostCategory.ToLowerInvariant();
                var targetSet = new HashSet<string>(targets.Select(t => t.ToLowerInvariant()));
                pairFilter = (a, b) =>
                {
                    string x = CategoryKey(a), y = CategoryKey(b);
                    return x == host && targetSet.Contains(y) || y == host && targetSet.Contains(x);
                };
            }

            var joiner = new ElementJoiner(doc, Info.ToleranceMm / 304.8);
            using (var group = new TransactionGroup(doc, join ? "自動接合" : "取消接合"))
            {
                group.Start();
                // 牆與牆：接合時使用牆端接合（失敗再幾何接合）；其餘配對使用幾何接合
                var walls = elements.OfType<Wall>().ToList();
                bool wallPairs = walls.Count > 1 && (pairFilter == null || pairFilter(walls[0], walls[1]));
                if (join && wallPairs) joiner.JoinWalls(walls, maxPairs);
                joiner.JoinElements(elements, join, priority, maxPairs,
                    (a, b) => (pairFilter == null || pairFilter(a, b)) && !(join && wallPairs && a is Wall && b is Wall));
                group.Assimilate();
            }

            var r = joiner.Report;
            string verb = join ? "接合" : "取消接合";
            string message = $"{verb}完成：候選配對 {r.CandidatePairs} 組；" +
                (join ? $"牆端接合 {r.WallEndJoinedPairs} 組、幾何接合 {r.GeometryJoinedPairs} 組、原本已接合 {r.AlreadyJoinedPairs} 組"
                      : $"已取消 {r.GeometryJoinedPairs} 組、原本未接合 {r.AlreadyJoinedPairs} 組") +
                $"、失敗 {r.FailedPairs} 組。" +
                (r.SwitchedOrders > 0 ? $"\n已切換樓板切割順序 {r.SwitchedOrders} 組。" : "") +
                (r.EndsKeptDisallowed > 0 ? $"\n{r.EndsKeptDisallowed} 個牆端鄰近未指定的牆，保留不接合。" : "") +
                "\n候選以包圍盒重疊判斷，不代表一定有實體交集；不會移動或刪除元素，成功的操作合併為一筆 Undo，模型不會自動儲存。";
            foreach (var note in r.Notes) message += "\n" + note;
            foreach (var issue in r.Issues.Take(5))
                message += $"\n  {issue.Stage}失敗：元素 {string.Join("、", issue.ElementIds)}" +
                    (issue.LocationMm == null ? "" : $"（約 X={issue.LocationMm[0]}, Y={issue.LocationMm[1]}, Z={issue.LocationMm[2]} mm）") + "：" + issue.Reason;
            return new ExtensionResult<AutoJoinResult>
            {
                Success = r.Notes.Count == 0 && r.FailedPairs == 0,
                Message = message,
                Response = new AutoJoinResult { Action = join ? "join" : "unjoin", Elements = elements.Count, Report = r }
            };
        }

        private static string CategoryKey(Element element)
        {
            if (element?.Category == null) return null;
            long id = element.Category.Id.ToLong();
            foreach (var pair in Categories)
                if (pair.Value.Any(c => (long)c == id)) return pair.Key;
            return null;
        }

        private static ExtensionResult<AutoJoinResult> Fail(string message)
        {
            return new ExtensionResult<AutoJoinResult> { Success = false, Message = message };
        }

        public bool WaitForCompletion(int timeoutMilliseconds = 10000)
        {
            _resetEvent.Reset();
            return _resetEvent.WaitOne(timeoutMilliseconds);
        }

        public string GetName()
        {
            return "自動接合";
        }
    }
}
