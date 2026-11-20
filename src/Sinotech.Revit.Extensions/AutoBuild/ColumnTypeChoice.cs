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
    /// 使用者指定的結構柱類型。
    /// MatchCadSize=true（預設）：以該類型的族群為準，選 CAD 尺寸相同的類型，沒有就以指定類型複製出 CAD 尺寸；
    /// MatchCadSize=false：全部柱直接使用指定類型，不依 CAD 尺寸調整。
    /// 未指定時（null）維持自動選型：專案中尺寸相同的混凝土柱類型，沒有就複製，沒有族群時從 Revit 族庫載入。
    /// </summary>
    internal class ColumnTypeChoice
    {
        public FamilySymbol Symbol { get; private set; }
        public bool MatchCadSize { get; private set; }
        /// <summary>指定類型是圓柱族群</summary>
        public bool Round => IsRound(Symbol);

        /// <summary>
        /// 依 ElementId 或名稱（類型名稱，或「族群：類型」）找結構柱類型；都沒指定時回傳 null。
        /// 找不到或名稱對應多個類型時丟出含可選清單的例外，不猜測
        /// </summary>
        public static ColumnTypeChoice From(Document doc, long? typeId, string typeName, bool matchCadSize)
        {
            if (!typeId.HasValue && string.IsNullOrWhiteSpace(typeName)) return null;
            var types = Loaded(doc);
            FamilySymbol symbol;
            if (typeId.HasValue)
            {
                symbol = types.FirstOrDefault(s => s.Id.ToLong() == typeId.Value);
                if (symbol == null) throw new InvalidOperationException("指定的 columnTypeId 不是專案中的結構柱類型。" + Choices(types));
            }
            else
            {
                string name = typeName.Trim().Replace(":", "：");
                var exact = types.Where(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(s.FamilyName + "：" + s.Name, name, StringComparison.OrdinalIgnoreCase)).ToList();
                var matches = exact.Count > 0 ? exact : types.Where(s =>
                    (s.FamilyName + "：" + s.Name).IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
                if (matches.Count == 0) throw new InvalidOperationException($"找不到名稱為「{typeName}」的結構柱類型。" + Choices(types));
                if (matches.Count > 1) throw new InvalidOperationException($"「{typeName}」對應多個結構柱類型，請改用 columnTypeId 指定。" + Choices(matches));
                symbol = matches[0];
            }
            return new ColumnTypeChoice { Symbol = symbol, MatchCadSize = matchCadSize };
        }

        internal static List<FamilySymbol> Loaded(Document doc) => new FilteredElementCollector(doc)
            .OfClass(typeof(FamilySymbol)).OfCategory(BuiltInCategory.OST_StructuralColumns).Cast<FamilySymbol>()
            .OrderBy(s => s.FamilyName).ThenBy(s => s.Name).ToList();

        internal static bool IsRound(FamilySymbol symbol) =>
            symbol.FamilyName.IndexOf("Round", StringComparison.OrdinalIgnoreCase) >= 0 || symbol.FamilyName.Contains("圓");

        private static string Choices(IEnumerable<FamilySymbol> types)
        {
            var list = types.Take(40).Select(s => $"{s.Id.ToLong()} = {s.FamilyName}：{s.Name}").ToList();
            return list.Count == 0 ? " 專案中沒有已載入的結構柱類型。" : " 可選結構柱類型：" + string.Join("；", list);
        }
    }
}
