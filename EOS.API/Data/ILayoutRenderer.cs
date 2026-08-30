using EOS.API.Models;

namespace EOS.API.Data;

/// <summary>
/// 打印布局渲染器接口（P6 抽象层，§9.7.1 渲染器定案 QuestPDF 解释层）。
/// L0/L1/L2 内置版式直接走 QuestPDF C# 代码（DocumentPdfService）；
/// L3 可视化设计器通过本接口消费 layout.json 渲染。
/// 接口隔离渲染器实现，使未来 L3 设计器可替换渲染后端而不影响取数层。
/// </summary>
public interface ILayoutRenderer
{
    /// <summary>按 layout.json 布局描述渲染为 PDF 字节流。</summary>
    byte[] Render(PrintData data, string layoutJson);
}

/// <summary>
/// QuestPDF 实现的布局渲染器（P6 默认实现）。
/// 解析 layout.json → 逐元素调用 QuestPDF API 绘制。
/// 当前只支持 L0/L1/L2 内置版式，L3 可视化设计器扩展此实现。
/// </summary>
public sealed class QuestPdfLayoutRenderer : ILayoutRenderer
{
    public byte[] Render(PrintData data, string layoutJson)
    {
        // P6 阶段：layout.json 由内置版式生成，当前仍委托 DocumentPdfService 渲染
        // L3 设计器上线后，改为解析 JSON 布局描述的逐元素渲染路径
        throw new NotImplementedException("L3 可视化设计器渲染器，待独立 ADR 实现。");
    }
}