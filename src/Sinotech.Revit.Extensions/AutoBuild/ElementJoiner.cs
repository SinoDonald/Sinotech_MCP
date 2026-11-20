// ============================================================================
//  Sinotech_MCP 自行開發命令集（SinotechExtensions）
//  開發者：Donald（donaldlu@mail.sinotech.com.tw）
//  Copyright © 2026 開發者保留所有權利。
//  本檔案不適用專案根目錄之 MIT 授權；未經開發者書面授權，
//  不得使用、複製、修改或散布。詳見 src/Sinotech.Revit.Extensions/LICENSE.md
// ============================================================================
using Autodesk.Revit.DB;
using Sinotech.Revit.Extensions.Common;

namespace Sinotech.Revit.Extensions.AutoBuild
{
    /// <summary>
    /// 接合結果：成功、原本已接合、失敗的配對數與問題位置（元素 ID、座標 mm）
    /// </summary>
    public class JoinReport
    {
        /// <summary>候選配對數（端點貼近或包圍盒重疊）</summary>
        public int CandidatePairs { get; set; }
        /// <summary>以牆端接合完成的牆配對</summary>
        public int WallEndJoinedPairs { get; set; }
        /// <summary>以幾何接合（或取消接合）完成的配對</summary>
        public int GeometryJoinedPairs { get; set; }
        public int AlreadyJoinedPairs { get; set; }
        public int FailedPairs { get; set; }
        /// <summary>因鄰近未指定的既有牆而保留不接合的牆端數</summary>
        public int EndsKeptDisallowed { get; set; }
        /// <summary>樓板切割順序已切換的配對數</summary>
        public int SwitchedOrders { get; set; }
        public List<JoinIssue> Issues { get; set; } = new List<JoinIssue>();
        public List<string> Notes { get; set; } = new List<string>();
    }

    public class JoinIssue
    {
        public string Stage { get; set; }
        public List<long> ElementIds { get; set; } = new List<long>();
        public string Reason { get; set; }
        /// <summary>問題位置（mm，X/Y/Z）</summary>
        public List<double> LocationMm { get; set; }
    }

    /// <summary>
    /// 自動接合：每一組在獨立交易中處理，失敗只復原該組，成功的組別保留。
    /// 牆與牆：先恢復牆端接合（只接到本次指定的牆，鄰近其他既有牆時保留不接合），
    /// 未形成接合時改試幾何接合；其他元素（柱、梁、板）使用幾何接合。
    /// 不移動、不刪除元素，也不修改原有元素的接合。須在交易外（可在 TransactionGroup 內）呼叫。
    /// </summary>
    internal class ElementJoiner
    {
        private const int MaxIssues = 30;
        private readonly Document _doc;
        private readonly double _tolerance;
        public JoinReport Report { get; } = new JoinReport();

        public ElementJoiner(Document doc, double tolerance)
        {
            _doc = doc; _tolerance = tolerance;
        }

        /// <summary>
        /// 開始交易後才設定失敗處理（Transaction.Start 會重設先前設定的 FailureHandlingOptions）
        /// </summary>
        internal static void Start(Transaction transaction, IFailuresPreprocessor preprocessor)
        {
            transaction.Start();
            var options = transaction.GetFailureHandlingOptions();
            options.SetFailuresPreprocessor(preprocessor);
            options.SetClearAfterRollback(true);
            transaction.SetFailureHandlingOptions(options);
        }

        // ------------------------------------------------------------------ 牆

        /// <summary>
        /// 接合指定的牆（只處理集合內的配對）
        /// </summary>
        public void JoinWalls(IList<Wall> walls, int maxPairs)
        {
            walls = walls.Where(w => w != null && w.IsValidObject && w.Location is LocationCurve lc && lc.Curve is Line).ToList();
            if (walls.Count < 2) return;
            var ids = new HashSet<long>(walls.Select(w => w.Id.ToLong()));
            var pairs = WallPairs(walls);
            Report.CandidatePairs += pairs.Count;
            if (pairs.Count > maxPairs)
            {
                Report.Notes.Add($"候選牆配對 {pairs.Count} 組超過上限 {maxPairs}，未接合；請分批處理。");
                return;
            }

            // 原本已在牆端接合的配對（既有牆）只計數，不重複處理
            var before = new HashSet<Tuple<Wall, Wall>>(pairs.Where(p => EndJoined(p.Item1, p.Item2) || EndJoined(p.Item2, p.Item1)));

            // 1. 恢復牆端接合：先整批嘗試，失敗再逐端處理
            if (!AllowEnds(walls, ids, walls.SelectMany(w => new[] { Tuple.Create(w, 0), Tuple.Create(w, 1) }).ToList()))
                foreach (var wall in walls)
                    for (int end = 0; end < 2; end++)
                        if (!WallUtils.IsWallJoinAllowedAtEnd(wall, end) &&
                            !AllowEnds(walls, ids, new List<Tuple<Wall, int>> { Tuple.Create(wall, end) }))
                            AddIssue("牆端接合", new[] { wall.Id }, "Revit 無法接合此牆端，已保留不接合", EndPoint(wall, end));

            // 2. 牆端沒有接上的配對改試幾何接合
            foreach (var pair in pairs)
            {
                Wall a = pair.Item1, b = pair.Item2;
                if (before.Contains(pair)) { Report.AlreadyJoinedPairs++; continue; }
                if (EndJoined(a, b) || EndJoined(b, a)) { Report.WallEndJoinedPairs++; continue; }
                if (JoinGeometryUtils.AreElementsJoined(_doc, a, b)) { Report.AlreadyJoinedPairs++; continue; }
                if (Geometry(a, b, true, null)) Report.GeometryJoinedPairs++;
                else Report.FailedPairs++;
            }
        }

        // 端點貼近另一道牆中心線（含牆厚）、且高度範圍重疊的牆配對
        private List<Tuple<Wall, Wall>> WallPairs(IList<Wall> walls)
        {
            var data = walls.Select(w =>
            {
                var line = (Line)((LocationCurve)w.Location).Curve;
                var box = w.get_BoundingBox(null);
                return new { Wall = w, A = line.GetEndPoint(0), B = line.GetEndPoint(1), w.Width, Box = box };
            }).ToList();
            var pairs = new List<Tuple<Wall, Wall>>();
            for (int i = 0; i < data.Count; i++)
                for (int j = i + 1; j < data.Count; j++)
                {
                    var p = data[i]; var q = data[j];
                    if (p.Box == null || q.Box == null) continue;
                    if (Math.Min(p.Box.Max.Z, q.Box.Max.Z) - Math.Max(p.Box.Min.Z, q.Box.Min.Z) <= _tolerance) continue;
                    double reach = (p.Width + q.Width) / 2 + _tolerance;
                    if (p.Box.Min.X > q.Box.Max.X + reach || q.Box.Min.X > p.Box.Max.X + reach ||
                        p.Box.Min.Y > q.Box.Max.Y + reach || q.Box.Min.Y > p.Box.Max.Y + reach) continue;
                    if (Distance2D(p.A, q.A, q.B) <= reach || Distance2D(p.B, q.A, q.B) <= reach ||
                        Distance2D(q.A, p.A, p.B) <= reach || Distance2D(q.B, p.A, p.B) <= reach)
                        pairs.Add(Tuple.Create(p.Wall, q.Wall));
                }
            return pairs;
        }

        // 恢復指定牆端的接合；接到集合外的牆時該端改回不接合。交易失敗回傳 false（已復原）
        private bool AllowEnds(IList<Wall> walls, HashSet<long> ids, List<Tuple<Wall, int>> ends)
        {
            var targets = ends.Where(e => !WallUtils.IsWallJoinAllowedAtEnd(e.Item1, e.Item2)).ToList();
            if (targets.Count == 0) return true;
            using (var transaction = new Transaction(_doc, "牆端接合"))
            {
                var guard = new JoinFailureGuard();
                Start(transaction, guard);
                try
                {
                    foreach (var end in targets) WallUtils.AllowWallJoinAtEnd(end.Item1, end.Item2);
                    _doc.Regenerate();
                    int kept = 0;
                    foreach (var end in targets)
                        if (JoinedAt(end.Item1, end.Item2).Any(id => !ids.Contains(id)))
                        {
                            WallUtils.DisallowWallJoinAtEnd(end.Item1, end.Item2);
                            kept++;
                        }
                    if (transaction.Commit() != TransactionStatus.Committed) return false;
                    Report.EndsKeptDisallowed += kept;
                    return true;
                }
                catch (Exception)
                {
                    if (transaction.HasStarted() && !transaction.HasEnded()) transaction.RollBack();
                    return false;
                }
            }
        }

        private static IEnumerable<long> JoinedAt(Wall wall, int end)
        {
            var array = ((LocationCurve)wall.Location).get_ElementsAtJoin(end);
            foreach (Element element in array)
                if (element != null && element.Id != wall.Id) yield return element.Id.ToLong();
        }

        private static bool EndJoined(Wall wall, Wall other)
        {
            long id = other.Id.ToLong();
            return WallUtils.IsWallJoinAllowedAtEnd(wall, 0) && JoinedAt(wall, 0).Contains(id) ||
                   WallUtils.IsWallJoinAllowedAtEnd(wall, 1) && JoinedAt(wall, 1).Contains(id);
        }

        private static XYZ EndPoint(Wall wall, int end) => ((LocationCurve)wall.Location).Curve.GetEndPoint(end);

        // ------------------------------------------------------------------ 一般元素

        /// <summary>
        /// 幾何接合（join=true）或取消幾何接合（join=false）指定的元素配對
        /// </summary>
        public void JoinElements(IList<Element> elements, bool join, string floorPriority, int maxPairs, Func<Element, Element, bool> filter = null)
        {
            var pairs = BoxPairs(elements).Where(p => filter == null || filter(p.Item1, p.Item2)).ToList();
            Report.CandidatePairs += pairs.Count;
            if (pairs.Count > maxPairs)
            {
                Report.Notes.Add($"候選配對 {pairs.Count} 組超過上限 {maxPairs}，未處理；請縮小範圍。");
                return;
            }
            foreach (var pair in pairs)
            {
                Element a = pair.Item1, b = pair.Item2;
                bool joined = JoinGeometryUtils.AreElementsJoined(_doc, a, b);
                if (!join)
                {
                    if (!joined) { Report.AlreadyJoinedPairs++; continue; }
                    if (Unjoin(a, b)) Report.GeometryJoinedPairs++; else Report.FailedPairs++;
                    continue;
                }
                if (joined)
                {
                    Report.AlreadyJoinedPairs++;
                    ApplyFloorPriority(a, b, floorPriority);
                    continue;
                }
                if (Geometry(a, b, false, floorPriority)) Report.GeometryJoinedPairs++;
                else Report.FailedPairs++;
            }
        }

        // 包圍盒（含容差）重疊的配對，去重
        private List<Tuple<Element, Element>> BoxPairs(IList<Element> elements)
        {
            var boxes = elements.Select(e => Tuple.Create(e, e.get_BoundingBox(null))).Where(t => t.Item2 != null).ToList();
            var pairs = new List<Tuple<Element, Element>>();
            for (int i = 0; i < boxes.Count; i++)
                for (int j = i + 1; j < boxes.Count; j++)
                {
                    BoundingBoxXYZ p = boxes[i].Item2, q = boxes[j].Item2;
                    if (p.Min.X > q.Max.X + _tolerance || q.Min.X > p.Max.X + _tolerance ||
                        p.Min.Y > q.Max.Y + _tolerance || q.Min.Y > p.Max.Y + _tolerance ||
                        p.Min.Z > q.Max.Z + _tolerance || q.Min.Z > p.Max.Z + _tolerance) continue;
                    pairs.Add(Tuple.Create(boxes[i].Item1, boxes[j].Item1));
                }
            return pairs;
        }

        private bool Geometry(Element a, Element b, bool walls, string floorPriority)
        {
            using (var transaction = new Transaction(_doc, "幾何接合"))
            {
                var guard = new JoinFailureGuard();
                Start(transaction, guard);
                string reason = null;
                try
                {
                    JoinGeometryUtils.JoinGeometry(_doc, a, b);
                    if (!walls) SwitchForFloor(a, b, floorPriority);
                    if (transaction.Commit() == TransactionStatus.Committed) return true;
                    reason = guard.FirstError ?? "Revit 無法接合，已復原此組";
                }
                catch (Exception ex)
                {
                    if (transaction.HasStarted() && !transaction.HasEnded()) transaction.RollBack();
                    reason = ex.Message;
                }
                AddIssue(walls ? "牆幾何接合" : "幾何接合", new[] { a.Id, b.Id }, reason, Center(a, b));
                return false;
            }
        }

        private bool Unjoin(Element a, Element b)
        {
            using (var transaction = new Transaction(_doc, "取消接合"))
            {
                var guard = new JoinFailureGuard();
                Start(transaction, guard);
                try
                {
                    JoinGeometryUtils.UnjoinGeometry(_doc, a, b);
                    if (transaction.Commit() == TransactionStatus.Committed) return true;
                    AddIssue("取消接合", new[] { a.Id, b.Id }, guard.FirstError ?? "Revit 無法取消接合，已復原此組", Center(a, b));
                }
                catch (Exception ex)
                {
                    if (transaction.HasStarted() && !transaction.HasEnded()) transaction.RollBack();
                    AddIssue("取消接合", new[] { a.Id, b.Id }, ex.Message, Center(a, b));
                }
                return false;
            }
        }

        // 已接合的配對只在明確要求時切換樓板切割順序（各自獨立交易）
        private void ApplyFloorPriority(Element a, Element b, string floorPriority)
        {
            if (!NeedsSwitch(a, b, floorPriority)) return;
            using (var transaction = new Transaction(_doc, "切換接合順序"))
            {
                Start(transaction, new JoinFailureGuard());
                try
                {
                    SwitchForFloor(a, b, floorPriority);
                    if (transaction.Commit() != TransactionStatus.Committed)
                        AddIssue("切換接合順序", new[] { a.Id, b.Id }, "Revit 無法切換切割順序，已復原", Center(a, b));
                }
                catch (Exception ex)
                {
                    if (transaction.HasStarted() && !transaction.HasEnded()) transaction.RollBack();
                    AddIssue("切換接合順序", new[] { a.Id, b.Id }, ex.Message, Center(a, b));
                }
            }
        }

        private bool NeedsSwitch(Element a, Element b, string floorPriority)
        {
            if (string.IsNullOrEmpty(floorPriority) || floorPriority == "preserve") return false;
            bool aFloor = IsFloor(a), bFloor = IsFloor(b);
            if (aFloor == bFloor) return false;
            Element floor = aFloor ? a : b, other = aFloor ? b : a;
            bool floorCuts = JoinGeometryUtils.IsCuttingElementInJoin(_doc, floor, other);
            return floorPriority == "floorCuts" ? !floorCuts : floorCuts;
        }

        private void SwitchForFloor(Element a, Element b, string floorPriority)
        {
            if (!NeedsSwitch(a, b, floorPriority)) return;
            JoinGeometryUtils.SwitchJoinOrder(_doc, a, b);
            Report.SwitchedOrders++;
        }

        private static bool IsFloor(Element e) => e.Category != null && e.Category.Id.ToLong() == (long)BuiltInCategory.OST_Floors;

        private static XYZ Center(Element a, Element b)
        {
            var p = a.get_BoundingBox(null); var q = b.get_BoundingBox(null);
            if (p == null || q == null) return null;
            var min = new XYZ(Math.Max(p.Min.X, q.Min.X), Math.Max(p.Min.Y, q.Min.Y), Math.Max(p.Min.Z, q.Min.Z));
            var max = new XYZ(Math.Min(p.Max.X, q.Max.X), Math.Min(p.Max.Y, q.Max.Y), Math.Min(p.Max.Z, q.Max.Z));
            return (min + max) / 2;
        }

        private void AddIssue(string stage, IEnumerable<ElementId> ids, string reason, XYZ point)
        {
            if (Report.Issues.Count >= MaxIssues) return;
            Report.Issues.Add(new JoinIssue
            {
                Stage = stage,
                ElementIds = ids.Select(i => i.ToLong()).ToList(),
                Reason = reason,
                LocationMm = point == null ? null : new List<double> { Math.Round(point.X * 304.8), Math.Round(point.Y * 304.8), Math.Round(point.Z * 304.8) }
            });
        }

        private static double Distance2D(XYZ p, XYZ a, XYZ b)
        {
            double dx = b.X - a.X, dy = b.Y - a.Y, length = dx * dx + dy * dy;
            double t = length < 1e-12 ? 0 : Math.Max(0, Math.Min(1, ((p.X - a.X) * dx + (p.Y - a.Y) * dy) / length));
            double x = a.X + t * dx - p.X, y = a.Y + t * dy - p.Y;
            return Math.Sqrt(x * x + y * y);
        }
    }

    /// <summary>
    /// 接合交易的失敗處理：刪除警告；有錯誤時整筆（只有這一組）復原，不跳對話框
    /// </summary>
    internal class JoinFailureGuard : IFailuresPreprocessor
    {
        public string FirstError { get; private set; }

        public FailureProcessingResult PreprocessFailures(FailuresAccessor failuresAccessor)
        {
            bool error = false;
            foreach (var failure in failuresAccessor.GetFailureMessages())
            {
                if (failure.GetSeverity() == FailureSeverity.Warning) { failuresAccessor.DeleteWarning(failure); continue; }
                error = true;
                if (FirstError == null) FirstError = failure.GetDescriptionText();
            }
            return error ? FailureProcessingResult.ProceedWithRollBack : FailureProcessingResult.Continue;
        }
    }

    /// <summary>
    /// 建牆交易的失敗處理：刪除警告；「無法保持接合」（CannotKeepJoined）只對本次新建的元素、
    /// 且 Revit 提供「解除接合」時套用解除接合，同一組元素只嘗試一次；其他錯誤維持原本處理
    /// </summary>
    internal class CreationFailureGuard : IFailuresPreprocessor
    {
        private readonly HashSet<long> _created;
        private readonly HashSet<string> _tried = new HashSet<string>();
        public int Warnings { get; private set; }
        /// <summary>已套用解除接合的新元素 ID 組</summary>
        public List<List<long>> Detached { get; } = new List<List<long>>();

        public CreationFailureGuard(HashSet<long> created)
        {
            _created = created;
        }

        public FailureProcessingResult PreprocessFailures(FailuresAccessor failuresAccessor)
        {
            bool resolved = false;
            foreach (var failure in failuresAccessor.GetFailureMessages())
            {
                if (failure.GetSeverity() == FailureSeverity.Warning)
                {
                    failuresAccessor.DeleteWarning(failure);
                    Warnings++;
                    continue;
                }
                if (failure.GetFailureDefinitionId() != BuiltInFailures.JoinElementsFailures.CannotKeepJoined) continue;
                var ids = failure.GetFailingElementIds().Select(i => i.ToLong()).OrderBy(i => i).ToList();
                string key = string.Join(",", ids);
                if (ids.Count == 0 || !ids.All(_created.Contains) || !_tried.Add(key)) continue;
                if (!failure.HasResolutionOfType(FailureResolutionType.DetachElements)) continue;
                failure.SetCurrentResolutionType(FailureResolutionType.DetachElements);
                failuresAccessor.ResolveFailure(failure);
                Detached.Add(ids);
                resolved = true;
            }
            return resolved ? FailureProcessingResult.ProceedWithCommit : FailureProcessingResult.Continue;
        }
    }
}
