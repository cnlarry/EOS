using EOS.API.Data;
using EOS.API.Features.Assistant.Actions;
using EOS.API.Features.Assistant.Parameters;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// 助手参数**作用域覆盖**的离线门禁（ADR-030 §6.2、§9 断言 3 的规则部分）。
///
/// <para>
/// 这里盯的是三件事：分层优先级（用户 &gt; 模块 &gt; 全局）、"只能收紧不能放宽"的方向判据、
/// 以及"覆盖出现在没有声明过的层上"必须被拒绝。它们同时决定**保存时**与**生效时**的结果——
/// 两处共用同一份规则，所以这里红了就是真的错了，不是测试挑剔。
/// </para>
/// </summary>
public sealed class AssistantParameterScopeTests
{
    private static SystemParameterItem GlobalRow(string key, string? value = null, string? defaultValue = null)
    {
        var descriptor = AssistantParameterCatalog.Find(key)!;
        return new SystemParameterItem(
            descriptor.Key,
            value,
            descriptor.ValueType,
            defaultValue,
            descriptor.Group,
            AssistantParameterCatalog.FindGroup(descriptor.Group)!.Label,
            descriptor.Description,
            "immediate",
            AssistantParameterCatalog.SeqNoOf(descriptor),
            null,
            null,
            null,
            IsReferenced: true);
    }

    /// <summary>全局参数行的全集（取值列空 = 用 DEFAULT_VALUE / 代码默认值）。</summary>
    private static List<SystemParameterItem> Globals() =>
        [.. AssistantParameterCatalog.All.Select(item => GlobalRow(item.Key))];

    private static AssistantParameterScopeRow Scope(string type, string key, string paramKey, string? value) =>
        new(type, key, paramKey, value, "tester", DateTimeOffset.UtcNow);

    /// <summary>全局行（可按需覆盖取值）叠加作用域后的生效参数与问题清单。</summary>
    private static (AssistantPolicyValues Values, IReadOnlyList<string> Problems) Layer(
        IReadOnlyList<AssistantParameterScopeRow> scopes, params (string Key, string? Value)[] globals)
    {
        var rows = Globals();
        foreach (var (key, value) in globals)
        {
            var index = rows.FindIndex(row => string.Equals(row.Key, key, StringComparison.OrdinalIgnoreCase));
            rows[index] = rows[index] with { Value = value };
        }

        var (layered, problems) = AssistantParameterScopeRules.Layer(rows, scopes);
        return (AssistantParameterResolver.Interpret(layered), problems);
    }

    private static AssistantPolicyValues Effective(
        IReadOnlyList<AssistantParameterScopeRow> scopes, params (string Key, string? Value)[] globals) =>
        Layer(scopes, globals).Values;

    private static double EffectiveUserCap(IReadOnlyList<AssistantParameterScopeRow> scopes, params (string Key, string? Value)[] globals) =>
        Effective(scopes, globals).Cost.UserDailyCapYuan;

    // ===== 分层优先级 =====

    [Fact]
    public void User_Override_Wins_Over_Global()
    {
        // 全局没改过（默认 5），给某人放宽到 20
        Assert.Equal(20d, EffectiveUserCap([Scope(AssistantParameterScopeRules.User, "u1", "USER_DAILY_CAP_YUAN", "20")]));
    }

    [Fact]
    public void Global_Override_Still_Works()
    {
        // 全局改过（取值列非空），没有被作用域覆盖时按它跑
        Assert.Equal(3d, EffectiveUserCap([], ("USER_DAILY_CAP_YUAN", "3")));
    }

    // ===== 模块层：动作族是本目录里唯一声明了它的三个键 =====

    [Fact]
    public void Module_Override_Disables_The_Action_Family()
    {
        // "这个模块不许助手删除"——批 B 判据里那条端到端场景，现在有了一条真能生效的参数
        var values = Effective([Scope(AssistantParameterScopeRules.Module, "1401", "ACTION_DELETE", "0")]);

        Assert.False(values.Capability.IsActionEnabled(AssistantRecordActionNames.Delete));
        // 别的动作族不受影响
        Assert.True(values.Capability.IsActionEnabled(AssistantRecordActionNames.Insert));
        Assert.True(values.Capability.IsActionEnabled(AssistantRecordActionNames.Update));
    }

    [Fact]
    public void Tighten_Prevents_A_Module_From_Reopening_A_Closed_Action()
    {
        // 全局已经把删除关掉了（取值列 = 0），模块层不能再把它打开——"关得掉、放不开"
        var (_, problems) = Layer(
            [Scope(AssistantParameterScopeRules.Module, "1401", "ACTION_DELETE", "1")],
            ("ACTION_DELETE", "0"));

        Assert.Contains(problems, problem => problem.Contains("不能把它打开", StringComparison.Ordinal));
    }

    [Fact]
    public void User_Layer_Is_Rejected_On_A_Module_Only_Parameter()
    {
        // ACTION_DELETE 只声明了模块层：用户层给它设值没有任何语义（个人不该有权放开模块级限制）
        var (_, problems) = AssistantParameterScopeRules.Layer(
            Globals(), [Scope(AssistantParameterScopeRules.User, "u1", "ACTION_DELETE", "0")]);

        Assert.Single(problems);
        Assert.Contains("没有声明可被用户覆盖", problems[0], StringComparison.Ordinal);
    }

    [Fact]
    public void Module_Layer_Is_Rejected_On_A_User_Only_Parameter()
    {
        // 反向也一样：日限额是按人说的，模块层给它设值同样没有语义
        var (_, problems) = AssistantParameterScopeRules.Layer(
            Globals(), [Scope(AssistantParameterScopeRules.Module, "1401", "USER_DAILY_CAP_YUAN", "8")]);

        Assert.Single(problems);
        Assert.Contains("没有声明可被模块覆盖", problems[0], StringComparison.Ordinal);
        Assert.Equal(
            5d,
            EffectiveUserCap([Scope(AssistantParameterScopeRules.Module, "1401", "USER_DAILY_CAP_YUAN", "8")]));
    }

    // ===== 机械键的映射与派生的提示词规则 =====

    [Fact]
    public void Action_Family_Keys_Map_To_The_Capability_Domain()
    {
        // 全局关掉删除：生效参数里的能力面必须反映它（键名是 ACTION_ + 动作名机械生成的）
        var values = Effective([], ("ACTION_DELETE", "0"));

        Assert.False(values.Capability.IsActionEnabled(AssistantRecordActionNames.Delete));
        Assert.True(values.Capability.IsActionEnabled(AssistantRecordActionNames.Insert));
    }

    [Fact]
    public void Metric_Rule_Requires_Both_Of_Its_Tools()
    {
        // 规则点名的工具必须全在：少一个就整条不注入（提示词里不能留一条做不到的硬要求）
        var capability = new AssistantCapabilityOptions();

        Assert.True(capability.AllowsRule("enum_metrics", "resolve_metric"));

        capability.DisabledTools.Add("resolve_metric");
        Assert.False(capability.AllowsRule("enum_metrics", "resolve_metric"));
        // 与它无关的规则照旧
        Assert.True(capability.AllowsRule("diagnose_record"));
    }

    [Fact]
    public void Blank_Scope_Value_Means_Not_Overridden()
    {
        // 空值 = 这一层不覆盖（与 3105 页面"恢复默认"同一口径），而不是"设成空"
        Assert.Equal(5d, EffectiveUserCap([Scope(AssistantParameterScopeRules.User, "u1", "USER_DAILY_CAP_YUAN", "  ")]));
    }

    // ===== 越界覆盖必须被拒绝并说明原因 =====

    [Fact]
    public void Non_Scopable_Key_Is_Rejected_And_Reported()
    {
        // 全局日上限声明为"不可作用域化"：谁想给某个人单独放宽都不行
        var (rows, problems) = AssistantParameterScopeRules.Layer(
            Globals(), [Scope(AssistantParameterScopeRules.User, "u1", "GLOBAL_DAILY_CAP_YUAN", "999")]);

        Assert.Single(problems);
        Assert.Contains("不可作用域化", problems[0], StringComparison.Ordinal);
        Assert.Equal(
            AssistantParameterResolver.Interpret(Globals()).Cost.GlobalDailyCapYuan,
            AssistantParameterResolver.Interpret(rows).Cost.GlobalDailyCapYuan);
    }

    [Fact]
    public void Layer_Not_Declared_By_The_Descriptor_Is_Rejected_And_Reported()
    {
        // USER_DAILY_CAP_YUAN 只声明了"按用户覆盖"，模块层的值没有任何语义
        var (_, problems) = AssistantParameterScopeRules.Layer(
            Globals(), [Scope(AssistantParameterScopeRules.Module, "1401", "USER_DAILY_CAP_YUAN", "8")]);

        Assert.Single(problems);
        Assert.Contains("没有声明可被模块覆盖", problems[0], StringComparison.Ordinal);
    }

    [Fact]
    public void Unknown_Key_In_Scope_Table_Is_Reported()
    {
        var (_, problems) = AssistantParameterScopeRules.Layer(
            Globals(), [Scope(AssistantParameterScopeRules.User, "u1", "SOME_RETIRED_KEY", "1")]);

        Assert.Single(problems);
        Assert.Contains("SOME_RETIRED_KEY", problems[0], StringComparison.Ordinal);
    }

    [Fact]
    public void Bad_Value_In_Scope_Table_Is_Reported_And_Keeps_The_Upper_Value()
    {
        // 0 会让限额形同虚设，所以数值下限在目录里就卡住了
        var (_, problems) = AssistantParameterScopeRules.Layer(
            Globals(), [Scope(AssistantParameterScopeRules.User, "u1", "USER_DAILY_CAP_YUAN", "0")]);

        Assert.Single(problems);
        Assert.Equal(5d, EffectiveUserCap([Scope(AssistantParameterScopeRules.User, "u1", "USER_DAILY_CAP_YUAN", "0")]));
    }

    // ===== 收紧方向（用合成的收紧型参数，不受当前目录里有哪些条目影响）=====

    private static AssistantParameterDescriptor TightenBit() => new(
        "SYNTHETIC_TOOL_SWITCH",
        "合成开关",
        "CAPABILITY",
        "bit",
        null,
        "合成：只能关不能开",
        "1",
        AssistantParameterScopePolicy.Tighten,
        ["SomeConsumer"],
        Layers: AssistantParameterScopeLayers.Module);

    private static AssistantParameterDescriptor TightenInt() => new(
        "SYNTHETIC_TOOL_LIMIT",
        "合成上限",
        "TOOL_LIMIT",
        "int",
        "行",
        "合成：只能更小",
        "10",
        AssistantParameterScopePolicy.Tighten,
        ["SomeConsumer"],
        Layers: AssistantParameterScopeLayers.Module);

    [Fact]
    public void Tighten_Bit_Can_Only_Be_Turned_Off()
    {
        var descriptor = TightenBit();

        Assert.False(
            AssistantParameterScopeRules.IsTightenAllowed(descriptor, "0", "1", out var problem),
            "上层已关，下层不能打开");
        Assert.Contains("不能把它打开", problem, StringComparison.Ordinal);

        Assert.True(AssistantParameterScopeRules.IsTightenAllowed(descriptor, "1", "0", out _));
        Assert.True(AssistantParameterScopeRules.IsTightenAllowed(descriptor, "1", "1", out _));
    }

    [Fact]
    public void Tighten_Number_Can_Only_Go_Lower()
    {
        var descriptor = TightenInt();

        Assert.False(AssistantParameterScopeRules.IsTightenAllowed(descriptor, "5", "8", out var problem));
        Assert.Contains("只能取更小的值", problem, StringComparison.Ordinal);

        Assert.True(AssistantParameterScopeRules.IsTightenAllowed(descriptor, "5", "5", out _));
        Assert.True(AssistantParameterScopeRules.IsTightenAllowed(descriptor, "5", "1", out _));
    }

    [Fact]
    public void Override_Policy_Accepts_Both_Directions()
    {
        var descriptor = AssistantParameterCatalog.Find("USER_DAILY_CAP_YUAN")!;

        Assert.True(AssistantParameterScopeRules.IsTightenAllowed(descriptor, "5", "20", out _));
        Assert.True(AssistantParameterScopeRules.IsTightenAllowed(descriptor, "5", "1", out _));
    }

    // ===== 目录自身的作用域声明 =====

    [Fact]
    public void Catalog_Scope_Declarations_Are_Consistent()
    {
        // 声明了层就得有策略（反之亦然）；收紧型只能声明一层——否则"比谁更严"没有唯一答案
        Assert.Empty(AssistantParameterScopeRules.ValidateCatalog());
    }

    [Fact]
    public void Every_Scopable_Parameter_Declares_At_Least_One_Layer()
    {
        Assert.All(AssistantParameterCatalog.All, item =>
        {
            if (item.ScopePolicy == AssistantParameterScopePolicy.None) return;

            Assert.NotEqual(AssistantParameterScopeLayers.None, item.Layers);
            Assert.Contains(
                item.Layers,
                new[]
                {
                    AssistantParameterScopeLayers.Module,
                    AssistantParameterScopeLayers.User,
                });
        });
    }
}
