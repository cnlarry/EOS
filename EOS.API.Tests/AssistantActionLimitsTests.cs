using EOS.API.Features.Assistant.Actions;
using EOS.API.Features.Assistant.Config;
using EOS.API.Features.Assistant.Governance;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// 阈值单一事实源与红线 fail-fast。
///
/// <para>
/// 两条都断在"能不能被绕过"上：红线的名字一旦出现在配置里就启动失败（哪怕写的是"看起来是开"的值），
/// 阈值则只允许在 <see cref="AssistantActionLimits"/> 里声明一次，业务代码引用常量而不是复制数值。
/// </para>
/// </summary>
public sealed class AssistantActionLimitsTests
{
    [Fact]
    public void 阈值默认值来自单一事实源()
    {
        var options = new AssistantActionLimitsOptions();
        Assert.Equal(AssistantActionLimits.MaxRowsPerAction, options.MaxRowsPerAction);
        Assert.Equal(AssistantActionLimits.MaxApprovalRequestRecords, options.MaxApprovalRequestRecords);
        Assert.Equal(AssistantActionLimits.MaxAuditResourceKeys, options.MaxAuditResourceKeys);
        Assert.Equal(AssistantActionLimits.MaxConfigCloneObjects, options.MaxConfigCloneObjects);
    }

    [Fact]
    public void 业务代码引用常量而不是复制数值()
    {
        Assert.Equal(AssistantActionLimits.MaxRowsPerAction, AssistantRecordActionArguments.MaxRows);
        Assert.Equal(AssistantActionLimits.MaxConfigCloneObjects, ConfigClonePlanner.MaxObjects);
    }

    [Fact]
    public void 红线恒为开且没有可关的取值()
    {
        // 红线是 const：类型面上就不存在"配成关"的成员，配置层只能"试图覆盖"，那会被启动期校验拦下。
        Assert.True(AssistantActionLimits.DryRunRequired);
        Assert.True(AssistantActionLimits.IdempotencyRequired);
        Assert.Equal(0, AssistantActionLimits.MaxUnauthorizedActions);
        Assert.Equal(0, AssistantActionLimits.ApprovalFamilyActionCount);
        Assert.DoesNotContain(
            typeof(AssistantActionLimitsOptions).GetProperties(),
            property => property.Name is "DryRunRequired" or "IdempotencyRequired"
                or "MaxUnauthorizedActions" or "ApprovalFamilyActionCount");
    }

    [Fact]
    public void 审计快照带上生效阈值()
    {
        var snapshot = new AssistantActionLimitsOptions { MaxRowsPerAction = 12 }.Snapshot();
        Assert.Contains("行数上限=12", snapshot, StringComparison.Ordinal);
        Assert.Contains("预演=开", snapshot, StringComparison.Ordinal);
        Assert.Contains("幂等=开", snapshot, StringComparison.Ordinal);
        Assert.Contains("越权上限=0", snapshot, StringComparison.Ordinal);
        Assert.Contains("批核族条目=0", snapshot, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("DryRunRequired", "false")]
    [InlineData("DryRunEnabled", "false")]
    [InlineData("IdempotencyRequired", "false")]
    [InlineData("IdempotencyEnabled", "0")]
    [InlineData("MaxUnauthorizedActions", "5")]
    [InlineData("ApprovalFamilyActionCount", "1")]
    public void 试图覆盖红线即校验失败(string key, string value)
    {
        var result = Validate(new Dictionary<string, string?>
        {
            [$"{AssistantActionLimitsOptions.SectionName}:{key}"] = value,
        });
        Assert.False(result.Succeeded);
        Assert.Contains(result.Failures!, failure => failure.Contains("红线", StringComparison.Ordinal));
    }

    [Fact]
    public void 配置层尝试覆盖红线时容器启动即失败()
    {
        // 与 Program.cs 同一套接线：Bind + ValidateOnStart + 校验器。
        // 红线覆盖不是"记一条警告"，而是取到选项值的那一刻就抛——服务因此起不来。
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [$"{AssistantActionLimitsOptions.SectionName}:IdempotencyRequired"] = "false",
        }).Build();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddOptions<AssistantActionLimitsOptions>()
            .Bind(configuration.GetSection(AssistantActionLimitsOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<
            Microsoft.Extensions.Options.IValidateOptions<AssistantActionLimitsOptions>,
            AssistantActionLimitsValidator>();
        using var provider = services.BuildServiceProvider();

        var failure = Assert.Throws<Microsoft.Extensions.Options.OptionsValidationException>(
            () => provider
                .GetRequiredService<Microsoft.Extensions.Options.IOptions<AssistantActionLimitsOptions>>()
                .Value);

        Assert.Contains("红线", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void 未登记的阈值键即校验失败()
    {
        var result = Validate(new Dictionary<string, string?>
        {
            [$"{AssistantActionLimitsOptions.SectionName}:MaxRowsPerPage"] = "10",
        });
        Assert.False(result.Succeeded);
        Assert.Contains(result.Failures!, failure => failure.Contains("未登记", StringComparison.Ordinal));
    }

    [Fact]
    public void 已登记的阈值可以被覆盖()
    {
        var result = Validate(new Dictionary<string, string?>
        {
            [$"{AssistantActionLimitsOptions.SectionName}:MaxRowsPerAction"] = "10",
        });
        Assert.True(result.Succeeded);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void 阈值小于最小值即校验失败(int value)
    {
        var result = Validate(new Dictionary<string, string?>
        {
            [$"{AssistantActionLimitsOptions.SectionName}:MaxApprovalRequestRecords"] = value.ToString(),
        });
        Assert.False(result.Succeeded);
    }

    private static Microsoft.Extensions.Options.ValidateOptionsResult Validate(
        IReadOnlyDictionary<string, string?> settings)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var options = configuration.GetSection(AssistantActionLimitsOptions.SectionName)
            .Get<AssistantActionLimitsOptions>() ?? new AssistantActionLimitsOptions();
        return new AssistantActionLimitsValidator(configuration).Validate(null, options);
    }
}
