using EOS.API.Features.Assistant.Actions;
using EOS.API.Features.Assistant.Config;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// 可代理动作注册表：六要素齐全、动作名与实现真源对齐、**注册表内不含职权类动作**。
///
/// <para>
/// 判别性：往注册表里加一条 approve 之类的动作，或用改名绕过，本用例必须变红
/// （脚本 `check-assistant-action-registry.ps1` 是同一判据的第二保险）。
/// </para>
/// </summary>
public sealed class AssistantActionRegistryTests
{
    [Fact]
    public void 注册表自检无问题()
    {
        Assert.Empty(AssistantActionRegistry.Validate());
    }

    [Fact]
    public void 每条动作的六要素与落点都非空()
    {
        Assert.NotEmpty(AssistantActionRegistry.All);
        foreach (var action in AssistantActionRegistry.All)
        {
            Assert.False(string.IsNullOrWhiteSpace(action.ActorSubject), $"{action.Name} 缺「谁」");
            Assert.False(string.IsNullOrWhiteSpace(action.Target), $"{action.Name} 缺「对什么」");
            Assert.False(string.IsNullOrWhiteSpace(action.Name), "动作名不得为空");
            Assert.False(string.IsNullOrWhiteSpace(action.Parameters), $"{action.Name} 缺「参数」");
            Assert.False(string.IsNullOrWhiteSpace(action.IdempotencyKey), $"{action.Name} 缺「幂等键」");
            Assert.False(string.IsNullOrWhiteSpace(action.AuditAction), $"{action.Name} 缺「审计」");
            Assert.False(string.IsNullOrWhiteSpace(action.Endpoint), $"{action.Name} 缺目标端点");
            Assert.False(string.IsNullOrWhiteSpace(action.Implementation), $"{action.Name} 缺实现位置");
        }
    }

    [Fact]
    public void 注册表覆盖已交付的动作且不多不少()
    {
        var expected = new[]
        {
            AssistantRecordActionNames.Insert,
            AssistantRecordActionNames.Update,
            AssistantRecordActionNames.Delete,
            ConfigSurfaceNames.ActionOf(ConfigSurface.Fields),
            ConfigSurfaceNames.ActionOf(ConfigSurface.DataSources),
            ConfigSurfaceNames.ActionOf(ConfigSurface.Buttons),
            ConfigSurfaceNames.ActionOf(ConfigSurface.Effects),
        };

        Assert.Equal(
            expected.OrderBy(name => name, StringComparer.Ordinal),
            AssistantActionRegistry.All.Select(action => action.Name).OrderBy(name => name, StringComparer.Ordinal));
    }

    [Fact]
    public void 注册表里没有职权类动作()
    {
        string[] verbs = ["approve", "deapprove", "endcase", "unendcase", "finish", "批核", "解批", "结案", "取消结案", "审批"];
        foreach (var action in AssistantActionRegistry.All)
        {
            foreach (var verb in verbs)
            {
                Assert.DoesNotContain(verb, action.Name, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain(verb, action.Endpoint, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain(verb, action.AuditAction, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    [Fact]
    public void 动作名可按名查到()
    {
        Assert.True(AssistantActionRegistry.TryGet("insert", out var insert));
        Assert.Equal("DocumentWorkbenchRepository.CreateRecordAsync", insert.Endpoint);
        Assert.True(AssistantActionRegistry.TryGet("CFG_EFFECT", out _));
        Assert.False(AssistantActionRegistry.TryGet("approve", out _));
    }
}
