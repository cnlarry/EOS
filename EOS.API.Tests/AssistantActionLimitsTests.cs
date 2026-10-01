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
///
/// <para>
/// **阈值搬进 dbo.SYSSS（ADR-030 批 C）之后，配置节失去了读取方**，于是判定收紧为一条：
/// 那个节里出现**任何**键都是覆盖尝试。上一版放行四个已知阈值键——那是按"配置文件仍是来源之一"写的；
/// 现在放行它们等于告诉填表的人"配上了"，而实际一个字节都不会被读。
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
        // 与 Program.cs 同一套接线：**不 Bind**（阈值不来自配置）+ ValidateOnStart + 校验器。
        // 红线覆盖不是"记一条警告"，而是取到选项值的那一刻就抛——服务因此起不来。
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [$"{AssistantActionLimitsOptions.SectionName}:IdempotencyRequired"] = "false",
        }).Build();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddOptions<AssistantActionLimitsOptions>().ValidateOnStart();
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
    public void 配置节里的已知阈值键同样不生效()
    {
        // 这一条是本次收紧的核心：MaxRowsPerAction 曾经是"允许被配置文件覆盖"的键，
        // 而它的读取方（AssistantRecordActionArguments.MaxRows）现在走 SYSSS。
        // 放行它 = 让填表的人以为配上了，实际不会被读。
        var result = Validate(new Dictionary<string, string?>
        {
            [$"{AssistantActionLimitsOptions.SectionName}:MaxRowsPerAction"] = "10",
        });

        Assert.False(result.Succeeded);
        Assert.Contains(result.Failures!, failure => failure.Contains("不会生效", StringComparison.Ordinal));
        // 提示必须指向真正的落点，否则读者只知道"不能用"，不知道"该去哪"
        Assert.Contains(result.Failures!, failure => failure.Contains("3105", StringComparison.Ordinal));
    }

    [Fact]
    public void 拼错的键同样不生效()
    {
        var result = Validate(new Dictionary<string, string?>
        {
            [$"{AssistantActionLimitsOptions.SectionName}:MaxRowsPerPage"] = "10",
        });

        Assert.False(result.Succeeded);
        Assert.Contains(result.Failures!, failure => failure.Contains("不会生效", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void 阈值小于最小值即校验失败(int value)
    {
        // 取值范围校验现在校验的是**代码默认值**（属性初始值 = 库里没有行时的取值），
        // 所以这里直接构造选项对象：再走配置绑定反而会绕进"这个节不生效"那条规则里，
        // 让这条测试因为别的原因变绿。
        var result = new AssistantActionLimitsValidator(new ConfigurationBuilder().Build())
            .Validate(null, new AssistantActionLimitsOptions { MaxApprovalRequestRecords = value });

        Assert.False(result.Succeeded);
        Assert.Contains(result.Failures!, failure => failure.Contains("小于最小值", StringComparison.Ordinal));
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
