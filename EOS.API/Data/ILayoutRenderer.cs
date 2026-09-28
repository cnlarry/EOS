using EOS.API.Models;

namespace EOS.API.Data;

/// <summary>
/// 打印布局渲染器接口（P6 抽象层， 渲染器定案 QuestPDF 解释层）。
/// 内置版式与客户定制统一走 layout.json 解释层。
/// 接口隔离渲染器实现，使设计器可替换渲染后端而不影响取数层。
/// </summary>
public interface ILayoutRenderer
{
    /// <summary>按 layout.json 布局描述渲染为 PDF 字节流（单据型版式 kind=document）。</summary>
    byte[] Render(PrintData data, string layoutJson, LayoutRenderContext? context = null);

    /// <summary>
    /// 报表清单渲染（列表型版式 kind=list）：数据经 <see cref="LayoutRenderContext.Report"/> 传入。
    /// 与单据渲染共用同一套元素管道——报表打印从此也走格式资产，而不是一条只认 PDF 的旁路。
    /// </summary>
    byte[] RenderReportList(PrintData data, string layoutJson, LayoutRenderContext context);
}
