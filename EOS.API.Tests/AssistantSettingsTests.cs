using EOS.API.Features.Assistant.ModelAccess;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// 系统提示词里的**输出格式约定**是与界面渲染面的契约：
/// 助手抽屉按 Markdown（GFM 子集）渲染回答（`EOS.Web/src/features/assistant/markdown.tsx`），
/// 提示词一旦不再约定格式，模型会各写各的，界面上就只剩标记本身（"很原始"的成因之一）。
/// 这里把契约钉住：改回纯文本即变红；同时断言原有的安全约束没被格式约定挤掉。
/// </summary>
public sealed class AssistantSettingsTests
{
    [Fact]
    public void 默认系统提示词约定了Markdown输出格式()
    {
        var prompt = new AssistantSettings().SystemPrompt;

        Assert.Contains("Markdown", prompt, StringComparison.Ordinal);
        Assert.Contains("不要输出 HTML", prompt, StringComparison.Ordinal);

        // 原有约束：不得被格式约定覆盖或稀释
        Assert.Contains("不要编造", prompt, StringComparison.Ordinal);
        Assert.Contains("不确定", prompt, StringComparison.Ordinal);
    }
}
