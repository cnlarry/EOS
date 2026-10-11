using System.Reflection;
using EOS.API.Controllers;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// 三个配置面**各自判自己的模块号**（ADR-033 §11 明文要求："只有 230103 权限的人进不去 230104，
/// 这是拆分的收益，要有用例钉住"）。
///
/// <para>
/// 拆分的收益全落在四行常量上：每个面在自己的控制器里声明权限锚点，读 `CanBrowse`、写 `CanSetup`
/// 都挂它。一旦有人把它改回"一个总开关"（或复制粘贴时用错号），隔离就**静默**消失——页面照常能用，
/// 只是越权不再被挡。故这里用反射把这些锚点钉死：值与归属都必须精确。
/// </para>
/// <para>
/// 客户端一侧的守卫（`RequirePermission permission={moduleReadPermission(…)}`）由
/// `EOS.Web/src/app/workspaceRoutes.test.ts` 的"三个配置面各自挂自己的读权限门"钉住。
/// 这条用例与它是一对：**服务端才是权威**，前端那道门只是不给入口。
/// </para>
/// </summary>
public sealed class ConfigFacePermissionAnchorsTests
{
    private static int Anchor(Type controller, string fieldName)
    {
        var field = controller.GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new Xunit.Sdk.XunitException($"{controller.Name} 里找不到 {fieldName}——权限锚点被改名/移走了，请同步本用例。");
        return (int)field.GetRawConstantValue()!;
    }

    [Theory]
    [InlineData(typeof(ModuleGroupAdminController), "ModuleGroupsModuleId", 230102)]
    [InlineData(typeof(ModuleButtonAdminController), "ModuleButtonsModuleId", 230103)]
    [InlineData(typeof(ModuleBusinessConfigController), "BehaviorModuleId", 230104)]
    // 显式发布留在模块定义面（230101）：它是模块级事实，不属于任何一个行为配置面
    [InlineData(typeof(ModuleBusinessConfigController), "MenuAdminModuleId", 230101)]
    public void 每个配置面的读写门挂在自己的模块号上(Type controller, string fieldName, int expected)
    {
        Assert.Equal(expected, Anchor(controller, fieldName));
    }

    [Fact]
    public void 四个锚点互不相同_不存在共用的总开关()
    {
        int[] anchors =
        [
            Anchor(typeof(ModuleGroupAdminController), "ModuleGroupsModuleId"),
            Anchor(typeof(ModuleButtonAdminController), "ModuleButtonsModuleId"),
            Anchor(typeof(ModuleBusinessConfigController), "BehaviorModuleId"),
            Anchor(typeof(ModuleBusinessConfigController), "MenuAdminModuleId"),
        ];

        Assert.Equal(anchors.Length, anchors.Distinct().Count());
        Assert.DoesNotContain(anchors, anchor => anchor is 23 or 2301);
    }
}
