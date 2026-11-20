// ============================================================================
//  Sinotech_MCP 自行開發命令集（SinotechExtensions）
//  開發者：Donald（donaldlu@mail.sinotech.com.tw）
//  Copyright © 2026 開發者保留所有權利。
//  本檔案不適用專案根目錄之 MIT 授權；未經開發者書面授權，
//  不得使用、複製、修改或散布。詳見 src/Sinotech.Revit.Extensions/LICENSE.md
// ============================================================================
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Sinotech.Revit.Extensions.AutoBuild.Models;
using Sinotech.Revit.Extensions.Common;
using Sinotech.Revit.Extensions.AutoBuild;
using RevitMCPSDK.API.Interfaces;

namespace Sinotech.Revit.Extensions.AutoBuild.Services
{
    /// <summary>
    /// 自動建牆：讀取連結 CAD 牆線圖層的平行雙線，求中心線後建立基本牆（不要求牆輪廓封閉）
    /// 預設只建牆；門、窗、柱、樓板須明確要求（PlaceDoors、PlaceWindows、PlaceColumns、PlaceFloors）
    /// </summary>
    public class CreateWallsFromCadEventHandler : IExternalEventHandler, IWaitableExternalEventHandler
    {
        private readonly ManualResetEvent _resetEvent = new ManualResetEvent(false);

        public CadWallCreationInfo Info { get; private set; }

        public ExtensionResult<CadWallCreationResult> Result { get; private set; }

        public void SetParameters(CadWallCreationInfo info)
        {
            Info = info;
            _resetEvent.Reset();
        }

        public void Execute(UIApplication uiapp)
        {
            try
            {
                Result = CreateWalls(uiapp);
            }
            catch (Exception ex)
            {
                Result = Fail("自動建牆失敗: " + ex.Message);
            }
            finally
            {
                _resetEvent.Set();
            }
        }

        private ExtensionResult<CadWallCreationResult> CreateWalls(UIApplication uiapp)
        {
            Document doc = uiapp.ActiveUIDocument.Document;
            var levels = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().OrderBy(l => l.Elevation).ToList();
            // 省略樓層時：基準為目前平面視圖的樓層，頂部為基準的上一層
            CadLayerReader.DefaultLevels(doc, out Level defaultBase, out _);
            Level bottom = string.IsNullOrWhiteSpace(Info.BaseLevel) ? defaultBase : levels.FirstOrDefault(l => l.Name == Info.BaseLevel);
            Level top = !string.IsNullOrWhiteSpace(Info.TopLevel) ? levels.FirstOrDefault(l => l.Name == Info.TopLevel) :
                bottom == null ? null : levels.FirstOrDefault(l => l.Elevation > bottom.Elevation + 1e-6);
            if (bottom == null || top == null || top.Elevation <= bottom.Elevation)
                return Fail("請指定有效的基準／頂部樓層，且頂部樓層須高於基準樓層（省略時使用目前平面視圖的樓層與其上一層）。");
            // 未指定牆類型時，每道牆依 CAD 牆厚選用最接近的基本牆類型
            var basicTypes = new FilteredElementCollector(doc).OfClass(typeof(WallType)).Cast<WallType>()
                .Where(t => t.Kind == WallKind.Basic).ToList();
            bool autoType = string.IsNullOrWhiteSpace(Info.WallTypeName) && !Info.WallTypeId.HasValue;
            WallType fixedType = autoType ? null : FindWallType(doc);
            if (autoType ? basicTypes.Count == 0 : fixedType == null || fixedType.Kind != WallKind.Basic)
                return Fail("找不到指定的基本牆類型，請先用 get_cad_layers 查詢可用的牆類型。");

            var reader = CadInputs.Read(doc, Info.ImportIds);
            if (reader.Imports.Count == 0)
                return Fail("目前視圖沒有連結的 CAD，請切換到含有 DWG 連結的平面視圖。");
            double maxThickness = Info.MaxThickness / 304.8;
            bool autoLayers = Info.Layers == null || Info.Layers.Count == 0;
            // 省略圖層時：依雙線特徵判定的牆線圖層，加上全部開口圖層
            var selected = new HashSet<string>(autoLayers
                ? reader.AnalyzeLayers(maxThickness).Where(a => a.Recommended).Select(a => a.Layer)
                : Info.Layers);
            if (autoLayers && selected.Count == 0)
                return Fail("無法自動判定牆線圖層（沒有圖層具有足夠的平行雙線）。請用 get_cad_layers 查看各圖層統計後指定 layers。");
            if (autoLayers) selected.UnionWith(reader.Layers.Where(WallRegionGeometry.IsOpeningLayer));
            var missing = selected.Where(l => !reader.Layers.Contains(l)).ToList();
            if (missing.Count == selected.Count)
                return Fail("連結 CAD 中找不到指定的圖層：" + string.Join("、", missing));

            double tolerance = 1 / 304.8; // 1 mm
            double minimumLength = Math.Max(uiapp.Application.ShortCurveTolerance, tolerance);
            var stats = new CadWallCreationResult
            {
                UnsupportedCurves = reader.UnsupportedCurves.Where(p => selected.Contains(p.Key)).Sum(p => p.Value),
                UsedLayers = selected.Where(reader.Layers.Contains).OrderBy(l => l).ToList(),
                LayersAutoDetected = autoLayers,
                BaseLevel = bottom.Name,
                TopLevel = top.Name
            };
            // 中心線連同其 CAD 牆厚
            var centerLines = new List<Tuple<WallRegionGeometry.Segment, double>>();
            // 開口圖層已補齊的牆缺口（放置門窗用）
            var gaps = new List<WallRegionGeometry.Gap>();
            // 不同的 DWG 連結分開求牆，避免互相干擾。
            foreach (var import in reader.Lines.Where(l => selected.Contains(l.Layer)).GroupBy(l => l.ImportId))
            {
                var input = new List<KeyValuePair<string, WallRegionGeometry.Segment>>();
                var openings = new List<WallRegionGeometry.Segment>();
                foreach (var line in import)
                {
                    if (Math.Abs(line.Start.Z - line.End.Z) > tolerance) { stats.NonPlanarLines++; continue; }
                    var segment = new WallRegionGeometry.Segment(new WallRegionGeometry.Point(line.Start.X, line.Start.Y),
                        new WallRegionGeometry.Point(line.End.X, line.End.Y));
                    if (WallRegionGeometry.IsOpeningLayer(line.Layer)) openings.Add(segment);
                    else input.Add(new KeyValuePair<string, WallRegionGeometry.Segment>(line.Layer, segment));
                }
                var built = WallPairBuilder.Build(input, maxThickness, tolerance, openings);
                centerLines.AddRange(built.Walls.Where(w => w.Axis.Length > minimumLength).Select(w => Tuple.Create(w.Axis, w.Thickness)));
                gaps.AddRange(built.Gaps);
                stats.JoinedOpenings += built.Gaps.Count;
                stats.SkippedPatterns += built.Patterns;
                stats.SkippedSymbols += built.Symbols;
                stats.SkippedIsolated += built.Isolated;
            }
            // 門窗：依牆中心線的缺口與全部圖層的線、圓弧判斷（不看圖層名稱），門窗缺口補成連續牆
            var openingList = new List<OpeningDetector.Opening>();
            // 全部圖層的水平直線（門窗、柱、樓板判斷用）
            var layerLines = CadInputs.LayerLines(reader);
            var allLines = layerLines.Select(l => l.Value).ToList();
            // 門、窗分別開關；舊版 PlaceOpenings 代表兩者都放
            bool placeDoors = Info.PlaceDoors || Info.PlaceOpenings, placeWindows = Info.PlaceWindows || Info.PlaceOpenings;
            Func<OpeningDetector.Kind, bool> wanted = k => k == OpeningDetector.Kind.Window ? placeWindows : k != OpeningDetector.Kind.Unknown && placeDoors;
            if ((placeDoors || placeWindows) && centerLines.Count > 0)
            {
                var detected = OpeningDetector.Detect(centerLines, gaps, allLines, CadInputs.Arcs(reader), tolerance, 400 / 304.8, 3600 / 304.8, wanted);
                // 只放要求的種類；無法判斷的缺口只回報
                openingList = detected.Openings.Where(o => o.Kind == OpeningDetector.Kind.Unknown || wanted(o.Kind)).ToList();
                centerLines = detected.CenterLines.Where(c => c.Item1.Length > minimumLength).ToList();
            }
            stats.CenterLines = centerLines.Count;
            // 結構柱：封閉小矩形或圓、尺寸重複且貼著牆的圖層（或指定的圖層）
            var columns = new List<StructureDetector.Column>();
            if (Info.PlaceColumns && centerLines.Count > 0)
            {
                columns = CadInputs.Columns(reader, layerLines, Info.ColumnLayers, centerLines.Select(c => c.Item1).ToList(), out var columnLayers);
                stats.ColumnLayers = columnLayers;
            }
            if (centerLines.Count == 0)
                return new ExtensionResult<CadWallCreationResult>
                {
                    Success = false,
                    Message = (autoLayers ? "自動判定的圖層（" + string.Join("、", stats.UsedLayers) + "）" : "所選圖層") +
                        $"未找到可建牆的平行雙線（間距 50～{Info.MaxThickness} mm、直線）。\n" +
                        Summary(stats),
                    Response = stats
                };

            var stories = Info.ByLevel ? levels.Where(l => l.Elevation >= bottom.Elevation && l.Elevation <= top.Elevation)
                .GroupBy(l => l.Elevation).Select(g => g.First()).ToList() : new List<Level> { bottom, top };
            stories[0] = bottom; stories[stories.Count - 1] = top;

            // 指定柱類型時先確認類型存在，找不到就在建模前回報，不建任何元素
            var columnChoice = columns.Count > 0 ? ColumnTypeChoice.From(doc, Info.ColumnTypeId, Info.ColumnTypeName, Info.MatchCadSize) : null;
            var created = new List<Wall>();
            var createdIds = new HashSet<long>();
            using (var transaction = new Transaction(doc, "自動建牆" + (placeDoors ? "、門" : "") + (placeWindows ? "、窗" : "") + (Info.PlaceColumns ? "、柱" : "")))
            {
                // Transaction.Start 會重設失敗處理設定，必須先 Start 再設定：
                // 刪除警告（避免對話框卡住 MCP 呼叫）；新牆的「無法保持接合」只套用解除接合
                var guard = new CreationFailureGuard(createdIds);
                transaction.Start();
                var failureOptions = transaction.GetFailureHandlingOptions();
                failureOptions.SetFailuresPreprocessor(guard);
                transaction.SetFailureHandlingOptions(failureOptions);
                var selector = autoType ? new WallTypeSelector(doc, basicTypes) : null;
                // 基準樓層的牆與其中心線，門窗放在這些牆上
                var hosts = new List<Tuple<WallRegionGeometry.Segment, Wall>>();
                for (int i = 0; i < stories.Count - 1; i++)
                    foreach (var c in centerLines)
                    {
                        var s = c.Item1;
                        bool mismatch = false;
                        WallType type = fixedType ?? selector.For(c.Item2, out mismatch);
                        using (var step = new SubTransaction(doc))
                        {
                            step.Start();
                            try
                            {
                                Line axis = Line.CreateBound(new XYZ(s.A.X, s.A.Y, stories[i].Elevation), new XYZ(s.B.X, s.B.Y, stories[i].Elevation));
                                Wall wall = Wall.Create(doc, axis, type.Id, stories[i].Id,
                                    stories[i + 1].Elevation - stories[i].Elevation, 0, false, false);
                                // 兩階段：建立時先停用兩端接合，整批牆提交後再逐組接合，避免接合錯誤讓整批建牆回滾
                                WallUtils.DisallowWallJoinAtEnd(wall, 0);
                                WallUtils.DisallowWallJoinAtEnd(wall, 1);
                                wall.get_Parameter(BuiltInParameter.WALL_KEY_REF_PARAM).Set(0);
                                wall.get_Parameter(BuiltInParameter.WALL_HEIGHT_TYPE).Set(stories[i + 1].Id);
                                step.Commit();
                                created.Add(wall);
                                createdIds.Add(wall.Id.ToLong());
                                if (i == 0) hosts.Add(Tuple.Create(s, wall));
                                stats.WallTypeCounts[type.Name] = (stats.WallTypeCounts.TryGetValue(type.Name, out int n) ? n : 0) + 1;
                                if (mismatch) stats.ThicknessMismatches++;
                            }
                            catch (Exception ex)
                            {
                                step.RollBack(); stats.Failed++;
                                if (stats.Errors.Count < 3) stats.Errors.Add(ex.Message);
                            }
                        }
                    }
                if (selector != null) stats.CreatedWallTypes.AddRange(selector.CreatedTypes);
                if (openingList.Count > 0)
                {
                    doc.Regenerate();
                    new OpeningPlacer(doc, bottom, stats).Place(openingList, hosts, Info.WindowSillHeight);
                }
                if (columns.Count > 0)
                {
                    var placer = new StructurePlacer(doc, stats.StructureNotes, columnChoice);
                    var placedColumns = placer.PlaceColumns(columns, bottom, top);
                    stats.ColumnsPlaced = placedColumns.Count;
                    foreach (var p in placer.TypeCounts) stats.StructureTypeCounts[p.Key] = p.Value;
                    stats.CreatedStructureTypes.AddRange(placer.CreatedTypes);
                    stats.Errors.AddRange(placer.Errors.Take(Math.Max(0, 5 - stats.Errors.Count)));
                    if (placer.Failed > 0) stats.StructureNotes.Add($"柱放置失敗 {placer.Failed} 處。");
                }
                if (transaction.Commit() != TransactionStatus.Committed)
                    return Fail("建牆交易未成功提交，請檢查 Revit 的錯誤訊息。");
                stats.SuppressedWarnings = guard.Warnings;
                stats.RecoveredJoinElementIds = guard.Detached;
                stats.WallIds = created.Where(w => w.IsValidObject).Select(w => w.Id.ToLong()).ToList();
                stats.CreatedCount = stats.WallIds.Count;
            }

            // 牆已提交（一筆 Undo）；之後的接合在另一個交易群組（另一筆 Undo），失敗只復原該組，不影響已建立的牆
            try
            {
                var walls = created.Where(w => w.IsValidObject).ToList();
                if (Info.AutoJoin && walls.Count > 1)
                {
                    var joiner = new ElementJoiner(doc, 5 / 304.8);
                    using (var group = new TransactionGroup(doc, "自動接合牆"))
                    {
                        group.Start();
                        joiner.JoinWalls(walls, 4000);
                        group.Assimilate();
                    }
                    stats.AutoJoin = joiner.Report;
                }
                stats.JoinsDeferred = !Info.AutoJoin || (stats.AutoJoin != null && (stats.AutoJoin.FailedPairs > 0 || stats.AutoJoin.Notes.Count > 0));
                // 樓板依牆柱圍出的房間，牆接合後再建
                if (Info.PlaceFloors && stats.CreatedCount > 0) BuildFloors(doc, bottom, allLines, reader.Bounds, stats);
            }
            catch (Exception ex)
            {
                stats.PostProcessingError = ex.Message;
            }

            string message = $"已生成 {stats.CreatedCount} 道牆（{bottom.Name} → {top.Name}；" +
                string.Join("、", stats.WallTypeCounts.Select(p => $"{p.Key} {p.Value} 道")) + "）。\n" +
                $"使用圖層{(autoLayers ? "（自動判定）" : "")}：{string.Join("、", stats.UsedLayers)}\n" + Summary(stats);
            if (reader.Bounds != null) message += "\n（只處理目前視圖裁剪範圍內的 CAD 圖形）";
            if (placeDoors || placeWindows)
            {
                message += "\n" + (placeDoors ? $"門：單開門 {stats.DoorsPlaced}、雙開門 {stats.DoubleDoorsPlaced}、推拉門 {stats.SlidingDoorsPlaced}" : "") +
                    (placeDoors && placeWindows ? "；" : "") + (placeWindows ? $"窗 {stats.WindowsPlaced}" : "") +
                    (stats.OpeningTypeCounts.Count == 0 ? "" : "（" + string.Join("、", stats.OpeningTypeCounts.Select(p => $"{p.Key} {p.Value}")) + "）");
                if (stats.CreatedOpeningTypes.Count > 0) message += "\n為符合 CAD 開口寬度新建類型：" + string.Join("、", stats.CreatedOpeningTypes);
                if (stats.SlidingDoorsPlaced > 0 && !stats.OpeningTypeCounts.Keys.Any(k => k.IndexOf("推拉", StringComparison.Ordinal) >= 0 || k.IndexOf("Sliding", StringComparison.OrdinalIgnoreCase) >= 0))
                    message += "\n專案沒有推拉門族群，推拉門以一般門放置，請替換族群。";
                if (stats.UnknownOpenings.Count > 0) message += $"\n無法判斷門窗的牆缺口 {stats.UnknownOpenings.Count} 處（未放置）：" + string.Join("；", stats.UnknownOpenings.Take(10));
                if (stats.OpeningFailures > 0) message += $"\n門窗放置失敗 {stats.OpeningFailures} 處（找不到宿主牆或門窗族群）。";
                if (stats.OrientationNote != null) message += "\n" + stats.OrientationNote;
                if (Info.ByLevel && stories.Count > 2) message += "\n門窗只放在基準樓層的牆上。";
            }
            if (Info.PlaceColumns)
                message += stats.ColumnLayers.Count == 0 ? "\n結構柱：找不到柱圖層，未放置（可指定 columnLayers）。" :
                    $"\n結構柱 {stats.ColumnsPlaced} 支（圖層：{string.Join("、", stats.ColumnLayers)}）";
            if (Info.PlaceFloors && stats.FloorType != null)
            {
                message += $"\n樓板 {stats.FloorsCreated} 塊（{stats.FloorType}，每個房間一塊、邊界在牆柱內緣）";
                if (stats.FloorsSkippedOutside > 0) message += $"；不完全在裁剪範圍內的房間 {stats.FloorsSkippedOutside} 個未建板";
                if (stats.FloorsSkippedShafts + stats.FloorsSkippedStairs > 0)
                    message += $"；略過電梯井／管道間 {stats.FloorsSkippedShafts}、樓梯 {stats.FloorsSkippedStairs} 處";
                if (stats.FloorFailures > 0) message += $"；失敗 {stats.FloorFailures} 處";
            }
            if (stats.StructureTypeCounts.Count > 0) message += "\n柱類型：" + string.Join("、", stats.StructureTypeCounts.Select(p => $"{p.Key} {p.Value}"));
            if (stats.CreatedStructureTypes.Count > 0) message += "\n新建類型：" + string.Join("、", stats.CreatedStructureTypes);
            foreach (var note in stats.StructureNotes) message += "\n" + note;
            if (stats.CreatedWallTypes.Count > 0) message += "\n依 CAD 牆厚新建牆類型：" + string.Join("、", stats.CreatedWallTypes);
            if (stats.ThicknessMismatches > 0)
                message += $"\n有 {stats.ThicknessMismatches} 道牆找不到厚度相符（±1 mm）且可安全複製的單層牆類型，已使用最接近的類型，請檢查。";
            if (stats.RecoveredJoinElementIds.Count > 0)
                message += $"\n「無法保持接合」已對新牆解除接合 {stats.RecoveredJoinElementIds.Count} 組（接角可能有接縫，請檢查）。";
            message += "\n" + JoinSummary(stats);
            if (stats.PostProcessingError != null)
                message += "\n牆已建立並提交，但後續處理中止：" + stats.PostProcessingError + "。請勿重新建立同一批牆。";
            if (missing.Count > 0) message += "\n找不到的圖層：" + string.Join("、", missing);
            return new ExtensionResult<CadWallCreationResult> { Success = stats.CreatedCount > 0, Message = message, Response = stats };
        }

        private WallType FindWallType(Document doc)
        {
            if (Info.WallTypeId.HasValue)
            {
                return doc.GetElement(ElementIdHelper.FromLong(Info.WallTypeId.Value)) as WallType;
            }
            return new FilteredElementCollector(doc).OfClass(typeof(WallType)).Cast<WallType>()
                .FirstOrDefault(t => t.Name == Info.WallTypeName);
        }

        private void BuildFloors(Document doc, Level bottom, List<WallRegionGeometry.Segment> allLines, CadLayerReader.Region bounds, CadWallCreationResult stats)
        {
            var floorType = CadInputs.FloorType(doc, Info.FloorTypeName);
            if (floorType == null) { stats.StructureNotes.Add("找不到樓板類型，未建立樓板。"); return; }
            stats.FloorType = floorType.Name;
            using (var transaction = new Transaction(doc, "自動建板"))
            {
                transaction.Start();
                WarningSwallower.Attach(transaction, new WarningSwallower());
                var builder = new FloorBuilder();
                builder.Build(doc, bottom, floorType, allLines, bounds);
                stats.FloorsCreated = builder.Created;
                stats.FloorsSkippedShafts = builder.SkippedShafts;
                stats.FloorsSkippedStairs = builder.SkippedStairs;
                stats.FloorFailures = builder.Failed;
                stats.FloorsSkippedOutside = builder.SkippedOutside;
                stats.Errors.AddRange(builder.Errors.Take(Math.Max(0, 5 - stats.Errors.Count)));
                if (transaction.Commit() != TransactionStatus.Committed)
                {
                    stats.FloorsCreated = 0;
                    stats.StructureNotes.Add("建板交易未成功提交，未建立樓板（牆已保留）。");
                }
            }
        }

        private string JoinSummary(CadWallCreationResult s)
        {
            if (!Info.AutoJoin) return "牆接合：依要求未接合（兩端停用接合），之後可用 auto_join_elements 接合這些牆。";
            if (s.AutoJoin == null) return "牆接合：沒有需要接合的牆。";
            var r = s.AutoJoin;
            string text = $"牆接合：牆端接合 {r.WallEndJoinedPairs} 組、幾何接合 {r.GeometryJoinedPairs} 組、失敗 {r.FailedPairs} 組" +
                (r.EndsKeptDisallowed > 0 ? $"；{r.EndsKeptDisallowed} 個牆端鄰近其他既有牆，保留不接合" : "");
            foreach (var note in r.Notes) text += "\n" + note;
            foreach (var issue in r.Issues.Take(5))
                text += $"\n  {issue.Stage}失敗：元素 {string.Join("、", issue.ElementIds)}" +
                    (issue.LocationMm == null ? "" : $"（約 X={issue.LocationMm[0]}, Y={issue.LocationMm[1]} mm）") + "：" + issue.Reason;
            return text;
        }

        private static string Summary(CadWallCreationResult s)
        {
            return $"開口圖層補齊的門窗缺口 {s.JoinedOpenings} 處；排除的雙線：樓梯／填充線 {s.SkippedPatterns}、開口符號框 {s.SkippedSymbols}、" +
                $"柱／設備等獨立小框或短線 {s.SkippedIsolated}；非水平線段 {s.NonPlanarLines}；不支援的曲線（弧形牆） {s.UnsupportedCurves}；建牆失敗 {s.Failed}" +
                (s.Errors.Count == 0 ? "" : "\n" + string.Join("\n", s.Errors));
        }

        private static ExtensionResult<CadWallCreationResult> Fail(string message)
        {
            return new ExtensionResult<CadWallCreationResult> { Success = false, Message = message };
        }

        public bool WaitForCompletion(int timeoutMilliseconds = 10000)
        {
            _resetEvent.Reset();
            return _resetEvent.WaitOne(timeoutMilliseconds);
        }

        public string GetName()
        {
            return "自動建牆";
        }
    }
}
