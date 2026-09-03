using EOS.API.Models;

namespace EOS.API.Data;

/// <summary>
/// 打印布局渲染器接口（P6 抽象层，§9.7.1 渲染器定案 QuestPDF 解释层）。
/// 内置版式与客户定制统一走 layout.json 解释层。
/// 接口隔离渲染器实现，使设计器可替换渲染后端而不影响取数层。
/// </summary>
public interface ILayoutRenderer
{
    /// <summary>按 layout.json 布局描述渲染为 PDF 字节流。</summary>
    byte[] Render(PrintData data, string layoutJson, LayoutRenderContext? context = null);
}
