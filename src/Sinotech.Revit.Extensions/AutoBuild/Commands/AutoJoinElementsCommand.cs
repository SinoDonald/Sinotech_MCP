// ============================================================================
//  Sinotech_MCP 自行開發命令集（SinotechExtensions）
//  開發者：Donald（donaldlu@mail.sinotech.com.tw）
//  Copyright © 2026 開發者保留所有權利。
//  本檔案不適用專案根目錄之 MIT 授權；未經開發者書面授權，
//  不得使用、複製、修改或散布。詳見 src/Sinotech.Revit.Extensions/LICENSE.md
// ============================================================================
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPSDK.API.Base;

using Sinotech.Revit.Extensions.AutoBuild.Services;

namespace Sinotech.Revit.Extensions.AutoBuild.Commands
{
    /// <summary>
    /// 自動接合：接合或取消接合柱、梁、牆、板（每組獨立交易）
    /// </summary>
    public class AutoJoinElementsCommand : ExternalEventCommandBase
    {
        private static readonly object _executionLock = new object();
        private AutoJoinElementsEventHandler _handler => (AutoJoinElementsEventHandler)Handler;

        public override string CommandName => "auto_join_elements";

        public AutoJoinElementsCommand(UIApplication uiApp)
            : base(new AutoJoinElementsEventHandler(), uiApp)
        {
        }

        public override object Execute(JObject parameters, string requestId)
        {
            lock (_executionLock)
            {
                try
                {
                    var info = parameters?.ToObject<AutoJoinInfo>() ?? new AutoJoinInfo();
                    _handler.SetParameters(info);

                    if (RaiseAndWaitForCompletion(110000)) // 逾時 110 秒（MCP 服務端 2 分鐘逾時）
                    {
                        return _handler.Result;
                    }
                    else
                    {
                        throw new TimeoutException("自動接合逾時（Revit 可能仍在處理，請稍後查看模型）");
                    }
                }
                catch (Exception ex)
                {
                    throw new Exception($"自動接合失敗: {ex.Message}");
                }
            }
        }
    }
}
