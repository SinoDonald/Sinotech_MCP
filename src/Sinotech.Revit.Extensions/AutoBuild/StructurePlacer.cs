// ============================================================================
//  Sinotech_MCP 自行開發命令集（SinotechExtensions）
//  開發者：Donald（donaldlu@mail.sinotech.com.tw）
//  Copyright © 2026 開發者保留所有權利。
//  本檔案不適用專案根目錄之 MIT 授權；未經開發者書面授權，
//  不得使用、複製、修改或散布。詳見 src/Sinotech.Revit.Extensions/LICENSE.md
// ============================================================================
using System.IO;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;

namespace Sinotech.Revit.Extensions.AutoBuild
{
    /// <summary>
    /// 放置結構柱與梁：選用專案中尺寸相符的類型（混凝土矩形柱 b×h、圓柱直徑、矩形梁 b×h），沒有就複製一個；
    /// 專案中沒有對應族群時，從 Revit 內建族庫載入。須在已開始的交易中呼叫
    /// </summary>
    internal class StructurePlacer
    {
        private const double Tolerance = 5 / 304.8;
        private readonly Document _doc;
        private readonly List<string> _notes;
        public Dictionary<string, int> TypeCounts { get; } = new Dictionary<string, int>();
        public List<string> CreatedTypes { get; } = new List<string>();
        public int Failed { get; private set; }
        public List<string> Errors { get; } = new List<string>();

        private readonly ColumnTypeChoice _columnChoice;

        public StructurePlacer(Document doc, List<string> notes, ColumnTypeChoice columnChoice = null)
        {
            _doc = doc; _notes = notes; _columnChoice = columnChoice;
        }

        public List<FamilyInstance> PlaceColumns(IEnumerable<StructureDetector.Column> columns, Level bottom, Level top)
        {
            var placed = new List<FamilyInstance>();
            var columnList = columns.ToList();
            var symbols = Symbols(BuiltInCategory.OST_StructuralColumns);
            List<FamilySymbol> rectangular = null, round = null;
            var choice = _columnChoice;
            if (choice != null)
            {
                // 指定類型：同族群的類型為候選，指定類型排第一（複製時以它為來源）
                var chosenFamily = new List<FamilySymbol> { choice.Symbol };
                chosenFamily.AddRange(symbols.Where(s => s.Id != choice.Symbol.Id && s.Family.Id == choice.Symbol.Family.Id));
                if (choice.Round) round = chosenFamily; else rectangular = chosenFamily;
                string mode = choice.MatchCadSize ? "依 CAD 尺寸選用或複製同族群類型" : "全部使用此類型、不依 CAD 尺寸調整";
                _notes.Add($"柱使用指定類型「{choice.Symbol.FamilyName}：{choice.Symbol.Name}」（{mode}）。");
                if (choice.MatchCadSize && columnList.Any(c => c.Round != choice.Round))
                    _notes.Add(choice.Round ? "指定的是圓柱類型，矩形柱改用自動選型。" : "指定的是矩形柱類型，圓柱改用自動選型。");
            }
            if (rectangular == null && columnList.Any(c => !c.Round && (choice == null || choice.MatchCadSize)))
                rectangular = Family(symbols, BuiltInCategory.OST_StructuralColumns, false, new[] { "*Concrete-Rectangular-Column*.rfa", "*矩形*柱*.rfa" });
            if (round == null && columnList.Any(c => c.Round && (choice == null || choice.MatchCadSize)))
                round = Family(symbols, BuiltInCategory.OST_StructuralColumns, true, new[] { "*Concrete-Round-Column*.rfa", "*圓形*柱*.rfa" });
            foreach (var column in columnList)
            {
                FamilySymbol symbol;
                if (choice != null && !choice.MatchCadSize) symbol = choice.Symbol;
                else
                {
                    var family = (column.Round ? round : rectangular) ?? new List<FamilySymbol>();
                    if (family.Count == 0) { Failed++; continue; }
                    try { symbol = SymbolFor(family, column.B, column.Round ? column.B : column.H, column.Round ? "D" : ""); }
                    catch (Exception ex)
                    {
                        Failed++;
                        if (Errors.Count < 3) Errors.Add("柱類型：" + ex.Message);
                        continue;
                    }
                }
                using (var step = new SubTransaction(_doc))
                {
                    step.Start();
                    try
                    {
                        if (!symbol.IsActive) symbol.Activate();
                        var point = new XYZ(column.Center.X, column.Center.Y, bottom.Elevation);
                        var instance = _doc.Create.NewFamilyInstance(point, symbol, bottom, StructuralType.Column);
                        instance.get_Parameter(BuiltInParameter.FAMILY_BASE_LEVEL_PARAM)?.Set(bottom.Id);
                        instance.get_Parameter(BuiltInParameter.FAMILY_TOP_LEVEL_PARAM)?.Set(top.Id);
                        instance.get_Parameter(BuiltInParameter.FAMILY_BASE_LEVEL_OFFSET_PARAM)?.Set(0);
                        instance.get_Parameter(BuiltInParameter.FAMILY_TOP_LEVEL_OFFSET_PARAM)?.Set(0);
                        // 結構柱預設不是房間邊界；設為邊界，樓板才能沿柱與牆圍出房間
                        var bounding = instance.get_Parameter(BuiltInParameter.WALL_ATTR_ROOM_BOUNDING);
                        if (bounding != null && !bounding.IsReadOnly) bounding.Set(1);
                        double angle = Math.Atan2(column.Direction.Y, column.Direction.X);
                        if (!column.Round && Math.Abs(angle) > 1e-6)
                            ElementTransformUtils.RotateElement(_doc, instance.Id, Line.CreateBound(point, point + XYZ.BasisZ), angle);
                        step.Commit();
                        placed.Add(instance);
                        Count(symbol);
                    }
                    catch (Exception ex)
                    {
                        step.RollBack(); Failed++;
                        if (Errors.Count < 3) Errors.Add("柱：" + ex.Message);
                    }
                }
            }
            return placed;
        }

        /// <summary>
        /// 放置梁（梁頂齊樓層）；鋼梁須專案中已有名稱含相同斷面（例如 500x200）的結構構架類型，否則略過
        /// </summary>
        public int PlaceBeams(IEnumerable<StructureDetector.Beam> beams, Level level, double defaultDepthRatio, out int skippedSteel)
        {
            skippedSteel = 0;
            int placed = 0;
            var symbols = Symbols(BuiltInCategory.OST_StructuralFraming);
            var concrete = Family(symbols, BuiltInCategory.OST_StructuralFraming, false, new[] { "*Concrete-Rectangular Beam*.rfa", "*Concrete-Rectangular-Beam*.rfa", "*矩形*梁*.rfa" });
            foreach (var beam in beams)
            {
                FamilySymbol symbol;
                if (beam.Steel)
                {
                    string size = $"{Math.Round(beam.Depth * 304.8)}X{Math.Round(beam.LabelWidth * 304.8)}";
                    symbol = symbols.FirstOrDefault(s => (s.FamilyName + s.Name).ToUpperInvariant().Replace(" ", "").Replace("×", "X").Contains(size));
                    if (symbol == null) { skippedSteel++; continue; }
                }
                else
                {
                    if (concrete.Count == 0) { Failed++; continue; }
                    double width = beam.LabelWidth > 0 ? beam.LabelWidth : beam.Width;
                    // 沒有標註時梁深取梁寬的倍數，四捨五入到 5 cm
                    double depth = beam.Depth > 0 ? beam.Depth : Math.Round(beam.Width * defaultDepthRatio * 30.48 / 5) * 5 / 30.48;
                    symbol = SymbolFor(concrete, width, depth, "");
                }
                using (var step = new SubTransaction(_doc))
                {
                    step.Start();
                    try
                    {
                        if (!symbol.IsActive) symbol.Activate();
                        var curve = Line.CreateBound(new XYZ(beam.Axis.A.X, beam.Axis.A.Y, level.Elevation), new XYZ(beam.Axis.B.X, beam.Axis.B.Y, level.Elevation));
                        var instance = _doc.Create.NewFamilyInstance(curve, symbol, level, StructuralType.Beam);
                        step.Commit();
                        placed++;
                        Count(symbol);
                    }
                    catch (Exception ex)
                    {
                        step.RollBack(); Failed++;
                        if (Errors.Count < 3) Errors.Add("梁：" + ex.Message);
                    }
                }
            }
            return placed;
        }

        // 找有 b、h（或直徑）參數的族群；專案中沒有就從 Revit 族庫載入
        private List<FamilySymbol> Family(List<FamilySymbol> symbols, BuiltInCategory category, bool round, string[] patterns)
        {
            Func<FamilySymbol, bool> fits = s => round
                ? s.FamilyName.IndexOf("Round", StringComparison.OrdinalIgnoreCase) >= 0 || s.FamilyName.Contains("圓")
                : SizeParameter(s, "b") != null && SizeParameter(s, "h") != null &&
                  s.FamilyName.IndexOf("Round", StringComparison.OrdinalIgnoreCase) < 0 && !s.FamilyName.Contains("圓");
            var found = symbols.Where(fits).ToList();
            if (found.Count > 0) return PreferConcrete(found);
            foreach (var path in LibraryFiles(patterns))
            {
                try
                {
                    if (!_doc.LoadFamily(path, out Family family) || family == null) continue;
                    var loaded = family.GetFamilySymbolIds().Select(id => _doc.GetElement(id)).OfType<FamilySymbol>().Where(fits).ToList();
                    if (loaded.Count == 0) continue;
                    _notes.Add($"已從 Revit 族庫載入「{family.Name}」。");
                    symbols.AddRange(loaded);
                    return loaded;
                }
                catch (Exception) { }
            }
            string kind = category == BuiltInCategory.OST_StructuralColumns ? (round ? "圓形結構柱" : "矩形結構柱") : "矩形混凝土梁";
            _notes.Add($"專案中沒有{kind}族群，也無法從 Revit 族庫載入，請先載入。");
            return found;
        }

        private static List<FamilySymbol> PreferConcrete(List<FamilySymbol> symbols)
        {
            var concrete = symbols.Where(s => s.FamilyName.IndexOf("Concrete", StringComparison.OrdinalIgnoreCase) >= 0 || s.FamilyName.Contains("混凝土")).ToList();
            return concrete.Count > 0 ? concrete : symbols;
        }

        private IEnumerable<string> LibraryFiles(string[] patterns)
        {
            var roots = _doc.Application.GetLibraryPaths().Values.Where(Directory.Exists).ToList();
            foreach (var root in roots)
                foreach (var pattern in patterns)
                {
                    string[] files;
                    try { files = Directory.GetFiles(root, pattern, SearchOption.AllDirectories); }
                    catch (Exception) { continue; }
                    foreach (var file in files.OrderBy(f => f.Length)) yield return file;
                }
        }

        // 選尺寸相同（±5 mm）的類型，沒有就複製一個命名為「寬x深cm」（圓柱為「D直徑cm」）
        private FamilySymbol SymbolFor(List<FamilySymbol> family, double b, double h, string prefix)
        {
            bool round = prefix == "D";
            Func<FamilySymbol, string, double> size = (s, n) => SizeParameter(s, n)?.AsDouble() ?? -1;
            Func<FamilySymbol, bool> same = s => round
                ? Math.Abs(Math.Max(size(s, "b"), size(s, "d")) - b) <= Tolerance
                : Math.Abs(size(s, "b") - b) <= Tolerance && Math.Abs(size(s, "h") - h) <= Tolerance;
            var match = family.FirstOrDefault(same);
            if (match != null) return match;
            var basis = family[0];
            bool sizable = round ? (SizeParameter(basis, "b") ?? SizeParameter(basis, "d")) != null
                : SizeParameter(basis, "b") != null && SizeParameter(basis, "h") != null;
            if (!sizable)
            {
                // 指定的類型沒有可調整的尺寸參數：直接使用，不複製
                string note = $"「{basis.FamilyName}：{basis.Name}」沒有可調整的寬度／深度（或直徑）類型參數，柱直接使用此類型，尺寸可能與 CAD 不同。";
                if (!_notes.Contains(note)) _notes.Add(note);
                return basis;
            }
            string name = round ? $"D{Math.Round(b * 30.48)}cm" : $"{Math.Round(b * 30.48)}x{Math.Round(h * 30.48)}cm";
            var existing = basis.Family.GetFamilySymbolIds().Select(id => _doc.GetElement(id)).OfType<FamilySymbol>().FirstOrDefault(s => s.Name == name);
            if (existing != null) return existing;
            var copy = (FamilySymbol)basis.Duplicate(name);
            if (round) (SizeParameter(copy, "b") ?? SizeParameter(copy, "d"))?.Set(b);
            else { SizeParameter(copy, "b")?.Set(b); SizeParameter(copy, "h")?.Set(h); }
            family.Add(copy);
            CreatedTypes.Add($"{copy.FamilyName}：{copy.Name}");
            return copy;
        }

        // 族群的尺寸參數：b／h（Revit 混凝土族群），或寬度／深度／直徑等別名
        private static Parameter SizeParameter(FamilySymbol symbol, string name)
        {
            string[] names = name == "b" ? new[] { "b", "B", "Width", "寬度", "寬", "柱寬", "梁寬", "樑寬" }
                : name == "h" ? new[] { "h", "H", "Depth", "深度", "深", "柱深", "梁深", "樑深", "高度" }
                : new[] { "d", "D", "Diameter", "直徑" };
            foreach (var n in names)
            {
                var p = symbol.LookupParameter(n);
                if (p != null && p.StorageType == StorageType.Double && !p.IsReadOnly) return p;
            }
            return null;
        }

        private List<FamilySymbol> Symbols(BuiltInCategory category)
        {
            return new FilteredElementCollector(_doc).OfClass(typeof(FamilySymbol)).OfCategory(category).Cast<FamilySymbol>().ToList();
        }

        private void Count(FamilySymbol symbol)
        {
            string key = $"{symbol.FamilyName}：{symbol.Name}";
            TypeCounts[key] = (TypeCounts.TryGetValue(key, out int n) ? n : 0) + 1;
        }
    }
}
