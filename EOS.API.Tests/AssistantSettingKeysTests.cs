using EOS.API.Data;
using EOS.API.Features.Assistant.ModelAccess;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// 助手设置参数表的解析与默认值（**不需要数据库**）。
///
/// <para>
/// 这里的核心是"缺行 = 代码默认值"与"坏值要被报告而不是静默忽略"。前者决定"恢复默认"是否真的能恢复，
/// 后者决定"界面显示 5 元、实际按别的值跑"这种事会不会发生而无人知晓。
/// </para>
/// </summary>
public sealed class AssistantSettingKeysTests
{
    private static AssistantSettingRow Row(string key, string? value, string type = "string") =>
        new(key, value, type, null, DateTimeOffset.UtcNow, "test");

    /// <summary>把参数表真正影响的字段拼成指纹：用于断言"这个键确实改动了什么"。</summary>
    private static string Fingerprint(AssistantSettings settings) => string.Join('|',
        settings.SystemPrompt,
        settings.EnableAutoDistill,
        settings.Cost.GlobalDailyCapYuan,
        settings.Cost.UserDailyCapYuan,
        settings.Cost.MaxConsecutiveFailures,
        settings.Cost.CooldownSeconds,
        settings.Cost.ReserveMicroYuanPerRequest,
        settings.Cost.InputPerMillionYuan,
        settings.Cost.OutputPerMillionYuan);

    [Fact]
    public void Missing_Rows_Fall_Back_To_Code_Defaults()
    {
        var (settings, problems) = AssistantSettingKeys.Apply([]);

        Assert.Empty(problems);
        // 一个键都没配过时，拿到的必须与 new AssistantSettings() 完全一致——
        // 这就是"恢复默认"的语义（删掉行即回到这里）
        Assert.Equal(Fingerprint(new AssistantSettings()), Fingerprint(settings));
    }

    [Fact]
    public void Overrides_Are_Applied()
    {
        var (settings, problems) = AssistantSettingKeys.Apply(
        [
            Row(AssistantSettingKeys.SystemPrompt, "你是测试提示词。"),
            Row(AssistantSettingKeys.UserDailyCapYuan, "8.5", "decimal"),
            Row(AssistantSettingKeys.EnableAutoDistill, "false", "bool"),
            Row(AssistantSettingKeys.MaxConsecutiveFailures, "9", "int"),
        ]);

        Assert.Empty(problems);
        Assert.Equal("你是测试提示词。", settings.SystemPrompt);
        Assert.Equal(8.5, settings.Cost.UserDailyCapYuan);
        Assert.False(settings.EnableAutoDistill);
        Assert.Equal(9, settings.Cost.MaxConsecutiveFailures);
    }

    /// <summary>
    /// **每一个参数键都必须真的改动点什么**。
    ///
    /// <para>
    /// 新增一个设置项却忘了在 <c>TryApply</c> 里接上，界面会照常显示、照常保存，而它**永远不生效**——
    /// 正是"配了不消费"最容易发生的地方。这条用"必须为每个键准备一个探针值"把它挡住：
    /// 加了描述符而不加探针，下面的数量断言先失败。
    /// </para>
    /// </summary>
    [Fact]
    public void Every_Declared_Key_Actually_Changes_Something()
    {
        var probes = new Dictionary<string, (string Value, string Type)>
        {
            [AssistantSettingKeys.SystemPrompt] = ("PROBE-PROMPT", "string"),
            [AssistantSettingKeys.EnableAutoDistill] = ("false", "bool"),
            [AssistantSettingKeys.GlobalDailyCapYuan] = ("88.5", "decimal"),
            [AssistantSettingKeys.UserDailyCapYuan] = ("7.25", "decimal"),
            [AssistantSettingKeys.MaxConsecutiveFailures] = ("9", "int"),
            [AssistantSettingKeys.CooldownSeconds] = ("123", "int"),
            [AssistantSettingKeys.ReserveMicroYuanPerRequest] = ("77777", "long"),
            [AssistantSettingKeys.InputPerMillionYuan] = ("3.5", "decimal"),
            [AssistantSettingKeys.OutputPerMillionYuan] = ("11.5", "decimal"),
        };

        Assert.Equal(
            AssistantSettingKeys.All.Select(item => item.Key).OrderBy(item => item),
            probes.Keys.OrderBy(item => item));

        var defaults = Fingerprint(new AssistantSettings());
        foreach (var descriptor in AssistantSettingKeys.All)
        {
            var probe = probes[descriptor.Key];
            var (settings, problems) = AssistantSettingKeys.Apply(
                [Row(descriptor.Key, probe.Value, probe.Type)]);

            Assert.Empty(problems);
            Assert.NotEqual(defaults, Fingerprint(settings));
        }
    }

    [Fact]
    public void Blank_Value_Means_Not_Set()
    {
        // 界面上清空输入 = 恢复默认，所以空串不该被当成"值"去解析（否则 SystemPrompt 会被清成空）
        var (settings, problems) = AssistantSettingKeys.Apply(
        [
            Row(AssistantSettingKeys.SystemPrompt, "   "),
            Row(AssistantSettingKeys.UserDailyCapYuan, string.Empty, "decimal"),
        ]);

        Assert.Empty(problems);
        Assert.Equal(new AssistantSettings().SystemPrompt, settings.SystemPrompt);
        Assert.Equal(new AssistantSettings().Cost.UserDailyCapYuan, settings.Cost.UserDailyCapYuan);
    }

    [Theory]
    [InlineData(AssistantSettingKeys.UserDailyCapYuan, "abc")]
    [InlineData(AssistantSettingKeys.UserDailyCapYuan, "0")]
    [InlineData(AssistantSettingKeys.UserDailyCapYuan, "-3")]
    [InlineData(AssistantSettingKeys.GlobalDailyCapYuan, "0")]
    [InlineData(AssistantSettingKeys.InputPerMillionYuan, "0")]
    [InlineData(AssistantSettingKeys.MaxConsecutiveFailures, "0")]
    [InlineData(AssistantSettingKeys.MaxConsecutiveFailures, "9999")]
    [InlineData(AssistantSettingKeys.CooldownSeconds, "abc")]
    [InlineData(AssistantSettingKeys.EnableAutoDistill, "yes")]
    public void Bad_Values_Are_Reported_And_Keep_The_Default(string key, string value)
    {
        var type = AssistantSettingKeys.Find(key)!.ValueType;
        var (settings, problems) = AssistantSettingKeys.Apply([Row(key, value, type)]);

        // 必须**报告**：静默忽略会让"界面显示 5 元、实际按默认值跑"无从追查
        Assert.NotEmpty(problems);
        Assert.Contains(AssistantSettingKeys.Find(key)!.DisplayName, problems[0]);
        // 同时必须**保住默认值**，不能落成 0 或空
        Assert.Equal(Fingerprint(new AssistantSettings()), Fingerprint(settings));
    }

    [Fact]
    public void Unknown_Keys_Are_Reported_But_Do_Not_Break_The_Rest()
    {
        // 参数下线后库里可能留着旧键：提示它、跳过它，但别的键照常生效
        var (settings, problems) = AssistantSettingKeys.Apply(
        [
            Row("SomeRetiredKey", "1"),
            Row(AssistantSettingKeys.UserDailyCapYuan, "6", "decimal"),
        ]);

        Assert.Single(problems);
        Assert.Contains("SomeRetiredKey", problems[0]);
        Assert.Equal(6, settings.Cost.UserDailyCapYuan);
    }

    [Fact]
    public void Descriptor_Defaults_Come_From_Code()
    {
        // 界面上的"默认值"提示必须取自代码默认值，否则会与真实行为漂移
        var defaults = new AssistantSettings();
        Assert.Equal(defaults.SystemPrompt,
            AssistantSettingKeys.Find(AssistantSettingKeys.SystemPrompt)!.DefaultText);
        Assert.Equal(defaults.Cost.UserDailyCapYuan.ToString("0.####"),
            AssistantSettingKeys.Find(AssistantSettingKeys.UserDailyCapYuan)!.DefaultText);
        Assert.All(AssistantSettingKeys.All, item =>
        {
            Assert.False(string.IsNullOrWhiteSpace(item.DisplayName));
            Assert.False(string.IsNullOrWhiteSpace(item.Description));
            Assert.Contains(item.ValueType, new[] { "string", "bool", "int", "decimal", "long" });
        });
    }
}
