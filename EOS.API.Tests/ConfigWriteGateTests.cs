using System.Text.Json;
using EOS.API.Features.Assistant.Actions;
using EOS.API.Features.Assistant.Config;
using EOS.API.Features.Assistant.Tools;
using EOS.API.Models;
using EOS.API.Security;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// 配置写能力的两道门与预演覆盖标注（不连库）：
/// <list type="number">
/// <item>**逐类准入**：四类配置各自独立启停，关闭即整类不可写（不是静默跳过）；</item>
/// <item>**权限门与既有写端点同一把**：字段/数据来源用字段维护门，按钮/效果用配置面门；</item>
/// <item>**不可预演的改动 100% 显式标注**：这是配置写在"改完会发生什么"上的全部诚实度。</item>
/// </list>
/// </summary>
public sealed class ConfigWriteGateTests
{
    [Theory]
    [InlineData(ConfigSurface.Fields)]
    [InlineData(ConfigSurface.DataSources)]
    [InlineData(ConfigSurface.Buttons)]
    [InlineData(ConfigSurface.Effects)]
    public async Task 该类未开放时整类不可写且说清原因(ConfigSurface surface)
    {
        var options = new AssistantConfigWriteOptions
        {
            Fields = surface != ConfigSurface.Fields,
            DataSources = surface != ConfigSurface.DataSources,
            Buttons = surface != ConfigSurface.Buttons,
            Effects = surface != ConfigSurface.Effects,
        };
        var service = CreateService(options, setup: true);
        var plan = await service.PlanAsync("U1", Request(surface), CancellationToken.None);

        Assert.Equal(ConfigWriteService.SurfaceDisabledCode, plan.BlockedCode);
        Assert.Contains("尚未开放", plan.BlockedMessage);
        Assert.Empty(plan.Items);
    }

    [Theory]
    [InlineData(ConfigSurface.Fields, ConfigWriteService.FieldAdminModuleId)]
    [InlineData(ConfigSurface.DataSources, ConfigWriteService.FieldAdminModuleId)]
    [InlineData(ConfigSurface.Buttons, ConfigWriteService.MenuAdminModuleId)]
    [InlineData(ConfigSurface.Effects, ConfigWriteService.MenuAdminModuleId)]
    public async Task 无该类配置写权限时fail_closed且不再往下走(ConfigSurface surface, int expectedModuleId)
    {
        var permissions = new RecordingPermissions(setup: false);
        var service = CreateService(AllEnabled(), setup: false, permissions);
        var plan = await service.PlanAsync("U1", Request(surface), CancellationToken.None);

        Assert.Equal(ConfigWriteService.ForbiddenCode, plan.BlockedCode);
        Assert.Empty(plan.Items);
        // 权限门问的是该类配置对应的模块号：字段/数据来源是字段维护门，按钮/效果是配置面门；
        // 且**只问一次**就返回——门不通过时不进入规划（规划要读源与目标的配置）。
        Assert.Equal(expectedModuleId, Assert.Single(permissions.QueriedModules));
    }

    [Fact]
    public async Task 未勾选任何改动项时拒绝应用()
    {
        var service = CreateService(AllEnabled(), setup: true);
        var result = await service.ApplyAsync(
            "U1", "IT", Request(ConfigSurface.Fields), [], KeySeed(), CancellationToken.None);
        Assert.Equal(ConfigWriteService.NoSelectionCode, result.BlockedCode);
        Assert.Empty(result.Items);
    }

    /// <summary>
    /// 参数面只剩"对哪两个对象做比对"与"应用哪些项"：既没有幂等键，也没有要写入的值。
    /// 键由服务端从调用身份推导，值由服务端现场算——请求体里进不了任意值。
    /// </summary>
    [Fact]
    public void 工具参数里没有幂等键也没有要写入的值()
    {
        var schema = JsonDocument.Parse(AssistantConfigArguments.ParametersJson).RootElement
            .GetProperty("properties")
            .EnumerateObject().Select(property => property.Name).ToList();

        Assert.DoesNotContain("idempotency_key", schema, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("key", schema, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("values", schema, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(
            ["event", "items", "objects", "record_key", "source_module_id", "source_table", "surface", "target_module_id", "target_table"],
            schema.OrderBy(name => name, StringComparer.Ordinal).ToArray());
    }

    /// <summary>
    /// 幂等键由服务端按"调用身份 + 面 + 请求意图"推导：同一批同键，换一批或换调用身份则换键。
    /// 键里不含"要写入的值"——值变了必然来自新的一次意图，不会被误判成重放。
    /// </summary>
    [Fact]
    public void 幂等键由服务端按调用身份与请求意图推导()
    {
        var request = new ConfigCloneRequest(ConfigSurface.Fields, "T1", "T2", Objects: ["F"]);
        var other = new ConfigCloneRequest(ConfigSurface.Fields, "T1", "T3", Objects: ["F"]);
        var seed = AssistantActionKeySeed.FromToolCall(7, "tool:call-1");

        var key = ConfigWriteService.BatchKeyFor(seed, ConfigSurface.Fields, request, ["fields:T2.F"]);
        Assert.Equal(key, ConfigWriteService.BatchKeyFor(seed, ConfigSurface.Fields, request, ["fields:T2.F"]));
        // 换目标、换勾选项、换调用身份都换键。
        Assert.NotEqual(key, ConfigWriteService.BatchKeyFor(seed, ConfigSurface.Fields, other, ["fields:T3.F"]));
        Assert.NotEqual(key, ConfigWriteService.BatchKeyFor(seed, ConfigSurface.Fields, request, ["fields:T2.G"]));
        Assert.NotEqual(key, ConfigWriteService.BatchKeyFor(
            AssistantActionKeySeed.FromUserConfirm("confirm-1"), ConfigSurface.Fields, request, ["fields:T2.F"]));
        Assert.Equal(32, key.Length);
    }

    /// <summary>
    /// 不可预演的改动必须逐项标注：保存阶段的效果链、字段与数据来源的元数据改动都没有可跑的链路，
    /// 缺了这张标注，用户会把"没预演"当成"预演通过"。
    /// </summary>
    [Fact]
    public async Task 不可预演的改动逐项显式标注且覆盖率百分之百()
    {
        var plan = new ConfigChangePlan(
            ConfigSurface.Effects, "采购订单", "委外订单", null, null,
            [
                EffectItem("effect:SAVE@field-accumulate#1", "SAVE", previewable: false,
                    "无法预演：保存阶段的效果链不在预演覆盖内（预演只覆盖批核生效与解批，约占动作行的 82%），"
                    + "保存后的效果组合要到真实保存时才看得出。"),
                EffectItem("effect:APPROVE_EFFECT@set-state#1", "APPROVE_EFFECT", previewable: false,
                    "可预演：需要一张真实单据的主键才能跑预演；未提供单据时按无法预演处理。"),
                new ConfigChangeItem(
                    "effect:ENDCASE@completion-close#1", ConfigSurface.Effects, "ENDCASE@completion-close",
                    "结案（结案）", [new("ENABLED", "启用", "是", "是")], [],
                    Previewable: false,
                    PreviewNote: "无法预演：该事件不在预演覆盖内（预演只支持批核生效与解批）。",
                    Payload: new ModuleActionPayload(
                        new BusinessActionDto(1, "ENDCASE", "completion-close"), null)),
            ],
            ["备注：效果键与公式行一并照抄。"]);

        var dryRunner = new ConfigDryRunner(null!, null!, null!, NullLogger<ConfigDryRunner>.Instance);
        var report = await dryRunner.DryRunAsync(
            plan, new ConfigCloneRequest(ConfigSurface.Effects), null, "U1", CancellationToken.None);

        Assert.Equal(3, report.Steps.Count);
        Assert.Equal(0, report.PreviewableCount);
        Assert.Equal(3, report.NotPreviewableCount);
        Assert.All(report.Steps, step =>
        {
            Assert.False(step.Previewable);
            Assert.False(string.IsNullOrWhiteSpace(step.Note));
        });
        // SAVE 阶段与不在覆盖内的事件都要出现"无法预演"这四个字，不得含糊。
        Assert.Contains(report.Steps, step => step.Note.Contains("无法预演") && step.Note.Contains("保存阶段"));
        Assert.Contains(report.Steps, step => step.Note.Contains("无法预演") && step.Note.Contains("不在预演覆盖内"));
        Assert.Contains(report.Notes, note => note.Contains("无法预演"));
        // 覆盖率口径：每一处"没预演"都有 note，报告里按项计数如实给出。
        Assert.Equal(report.Steps.Count, report.PreviewableCount + report.NotPreviewableCount);
    }

    [Fact]
    public void 助手工具面含配置写三件且总数一致()
    {
        var names = IlCallGraph.ToolNames();

        Assert.Contains("clone_module_config", names);
        Assert.Contains("preview_config_change", names);
        Assert.Contains("apply_config_change", names);
        // 25 个原有工具 + 请求卡工具（prepare-only，不执行批核族）。
        Assert.Equal(26, names.Count);
    }

    // ===== 装配 =====

    private static AssistantConfigWriteOptions AllEnabled() => new()
    {
        Fields = true,
        DataSources = true,
        Buttons = true,
        Effects = true,
    };

    private static ConfigWriteService CreateService(
        AssistantConfigWriteOptions options, bool setup, RecordingPermissions? permissions = null)
        => new(
            Options.Create(options),
            permissions ?? new RecordingPermissions(setup),
            null!,
            null!,
            null!,
            null!,
            null!,
            null!,
            NullLogger<ConfigWriteService>.Instance);

    private static ConfigCloneRequest Request(ConfigSurface surface) => surface switch
    {
        ConfigSurface.Fields or ConfigSurface.DataSources => new ConfigCloneRequest(surface, "ZZ_SRC", "ZZ_DST"),
        _ => new ConfigCloneRequest(surface, SourceModuleId: 1, TargetModuleId: 2),
    };

    private static AssistantActionKeySeed KeySeed() => AssistantActionKeySeed.FromToolCall(1, "call-1");

    private static ConfigChangeItem EffectItem(string id, string eventCode, bool previewable, string note)
        => new(
            id, ConfigSurface.Effects, $"{eventCode}@key", eventCode, [new("ENABLED", "启用", "否", "是")], [],
            previewable, note,
            Payload: new ModuleActionPayload(new BusinessActionDto(1, eventCode, "key"), null));

    private static FieldAdminInput EmptyInput()
        => new("标签", "nvarchar", 100, "left", "center", null, true, false, true, false, false, false, false,
            null, null, null, null, null, null, false, false, null, [], true);

    /// <summary>记录被问到的模块号的权限桩：用于断言两类配置面问的是各自那把门。</summary>
    private sealed class RecordingPermissions(bool setup) : IPermissionService
    {
        public List<int> QueriedModules { get; } = [];

        public Task<ModulePermission> GetAsync(string userId, int moduleId, CancellationToken cancellationToken)
        {
            QueriedModules.Add(moduleId);
            return Task.FromResult(new ModulePermission(new ModuleRights(
                CanBrowse: true, CanViewCost: true, CanViewSecrecy: true, CanSetup: setup,
                DeniedMasterFields: new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                DeniedDetailFields: new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                CanAddNew: true, CanEdit: true, CanDelete: true,
                CanApprove: false, CanDeapprove: false, CanEndCase: false, CanUnEndCase: false,
                CanFileView: true, CanFileUpda: true, CanFileEdit: true, CanFileDele: true,
                DenyNewMasterFields: new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                DenyNewDetailFields: new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                DenyModiMasterFields: new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                DenyModiDetailFields: new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                DataFilter: "", ExecuteTag: "A", CanModuleConfig: true)));
        }

        public Task<ModulePermission> RequireAsync(
            string userId, int moduleId, PermissionAction action, CancellationToken cancellationToken)
            => GetAsync(userId, moduleId, cancellationToken);
    }
}
