// ============================================================================
//  Sinotech_MCP 自行開發命令集（SinotechExtensions）
//  開發者：Donald（donaldlu@mail.sinotech.com.tw）
//  Copyright © 2026 開發者保留所有權利。
//  本檔案不適用專案根目錄之 MIT 授權；未經開發者書面授權，
//  不得使用、複製、修改或散布。詳見 src/Sinotech.Revit.Extensions/LICENSE.md
// ============================================================================
using Autodesk.Revit.DB;

namespace Sinotech.Revit.Extensions.AutoBuild
{
    /// <summary>
    /// 未指定牆類型時，依 CAD 牆厚選基本牆類型：
    /// 1. 優先使用同厚度（±1 mm）的既有類型；
    /// 2. 沒有時，從厚度最接近的「單層、非垂直複合」類型複製一個，厚度設為 CAD 牆厚（保留材料與層功能）；
    /// 3. 沒有可安全調整的單層類型時才用厚度最接近的類型，並計入厚度不符。
    /// 不修改既有類型；同一次執行相同厚度只複製一次。須在已開始的交易中呼叫。
    /// </summary>
    internal class WallTypeSelector
    {
        private const double SameTolerance = 1 / 304.8;
        private readonly Document _doc;
        private readonly List<WallType> _types;
        private readonly Dictionary<long, WallType> _cache = new Dictionary<long, WallType>();

        /// <summary>因 CAD 牆厚而新建的類型名稱</summary>
        public List<string> CreatedTypes { get; } = new List<string>();

        public WallTypeSelector(Document doc, IEnumerable<WallType> basicTypes)
        {
            _doc = doc;
            _types = basicTypes.Where(t => t.Kind == WallKind.Basic).OrderBy(t => t.Name).ToList();
        }

        /// <summary>
        /// 取得 CAD 牆厚（ft）對應的類型；mismatch 表示類型厚度與 CAD 牆厚相差超過 1 mm
        /// </summary>
        public WallType For(double thickness, out bool mismatch)
        {
            if (thickness <= 0)
            {
                // 無法判定牆厚（理論上雙線配對都會有厚度）：用專案預設牆類型，並回報不符
                mismatch = true;
                var defaultId = _doc.GetDefaultElementTypeId(ElementTypeGroup.WallType);
                return _types.FirstOrDefault(t => t.Id == defaultId) ?? _types.First();
            }
            long key = (long)Math.Round(thickness * 304.8);
            if (!_cache.TryGetValue(key, out var type))
            {
                type = Resolve(key / 304.8);
                _cache[key] = type;
            }
            mismatch = Math.Abs(type.Width - thickness) > SameTolerance && Math.Abs(type.Width - key / 304.8) > SameTolerance;
            return type;
        }

        private WallType Resolve(double thickness)
        {
            var same = _types.Where(t => Math.Abs(t.Width - thickness) <= SameTolerance).ToList();
            if (same.Count > 0)
            {
                // 同厚度有多個時，優先專案預設類型，其次單層類型
                var defaultId = _doc.GetDefaultElementTypeId(ElementTypeGroup.WallType);
                return same.FirstOrDefault(t => t.Id == defaultId) ?? same.FirstOrDefault(IsSingleLayer) ?? same[0];
            }
            var source = _types.Where(IsSingleLayer).OrderBy(t => Math.Abs(t.Width - thickness)).ThenBy(t => t.Name).FirstOrDefault();
            if (source != null)
            {
                var copy = Duplicate(source, thickness);
                if (copy != null) return copy;
            }
            return _types.OrderBy(t => Math.Abs(t.Width - thickness)).ThenBy(t => t.Name).First();
        }

        private WallType Duplicate(WallType source, double thickness)
        {
            string mm = Math.Round(thickness * 304.8).ToString("0");
            string name = $"Sinotech CAD {mm}mm - {source.Name}";
            var existing = new FilteredElementCollector(_doc).OfClass(typeof(WallType)).Cast<WallType>().FirstOrDefault(t => t.Name == name);
            if (existing != null && Math.Abs(existing.Width - thickness) <= SameTolerance)
            {
                _types.Add(existing);
                return existing;
            }
            if (existing != null) name += " " + Guid.NewGuid().ToString("N").Substring(0, 4);
            using (var step = new SubTransaction(_doc))
            {
                step.Start();
                try
                {
                    var copy = (WallType)source.Duplicate(name);
                    var structure = copy.GetCompoundStructure();
                    structure.SetLayerWidth(0, thickness);
                    copy.SetCompoundStructure(structure);
                    step.Commit();
                    _types.Add(copy);
                    CreatedTypes.Add(copy.Name);
                    return copy;
                }
                catch (Exception)
                {
                    step.RollBack();
                    return null;
                }
            }
        }

        // 可安全改厚度：單層、非垂直複合的基本牆
        private static bool IsSingleLayer(WallType type)
        {
            var structure = type.GetCompoundStructure();
            return structure != null && structure.LayerCount == 1 && !structure.IsVerticallyCompound;
        }
    }
}
