using EOS.API.Controllers;
using EOS.API.Data;
using EOS.API.Data.DocumentActions;
using EOS.API.Features.Assistant.Config;
using EOS.API.Features.Assistant.Tools;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// **权限授予类配置在助手能力面里调不到**：改一个字段的显示名是维护数据，改一个人能批什么单是分配权力。
/// 后者不可代劳，因此它不是"约定不要调用"，而是**调用面上不存在**——本用例从三处断言这一点：
///
/// <list type="number">
/// <item>助手配置写面的调用点里没有任何职权类（权限、用户、菜单）的任何方法；</item>
/// <item>它落库只落在既有两个配置仓储的允许方法上，多一个就是在造第二处写路径；</item>
/// <item>按钮的克隆不碰按钮授权（"谁能用这个按钮"是分权，不随配置迁移）。</item>
/// </list>
///
/// 判别性：把任一职权类接进配置写面（例如让克隆顺手复制按钮授权），本用例必须变红。
/// </summary>
public sealed class NoPrivilegeConfigCallTests
{
    /// <summary>职权类：它们的任何方法都不该出现在助手配置写面里（按类型名匹配，改名也拦得住）。</summary>
    private static readonly string[] PrivilegeTypes =
    [
        "RightsAdminController", "RightsAdminRepository",
        "UserAdminController", "UserAdminRepository",
        // 菜单树不属本批的四类配置面；点错路径最容易从这里溜进来。
        "MenuAdminController", "MenuAdminRepository",
    ];

    /// <summary>配置写面允许触达字段维护仓储的方法（读 + 一个带幂等的写；按名排序便于对拍）。</summary>
    private static readonly string[] AllowedFieldRepositoryMethods =
        ["GetFieldsAsync", "GetMetadataAsync", "GetModulesAsync", "UpdateIdempotentAsync"];

    /// <summary>配置写面允许触达模块业务配置仓储的方法：读既有配置 + 整模块保存。</summary>
    private static readonly string[] AllowedConfigRepositoryMethods = ["GetAsync", "SaveAsync"];

    [Fact]
    public void 配置写面调不到任何职权类()
    {
        var reachable = Reachable();
        // 正向锚点：确实解析到了调用点——否则下面的"调不到"会永远为真（断言失效）。
        Assert.Contains(reachable, method => method.DeclaringType == typeof(FieldAdminRepository));
        Assert.Contains(reachable, method => method.DeclaringType == typeof(ModuleBusinessConfigRepository));

        foreach (var type in PrivilegeTypes)
        {
            Assert.DoesNotContain(reachable, method => method.DeclaringType?.Name == type);
        }
    }

    [Fact]
    public void 配置写面只在既有两个仓储的允许方法上落库()
    {
        var reachable = Reachable();

        var fields = reachable.Where(method => method.DeclaringType == typeof(FieldAdminRepository))
            .Select(method => method.Name).Distinct().OrderBy(name => name, StringComparer.Ordinal).ToList();
        Assert.Equal(AllowedFieldRepositoryMethods, fields);

        var config = reachable.Where(method => method.DeclaringType == typeof(ModuleBusinessConfigRepository))
            .Select(method => method.Name).Distinct().OrderBy(name => name, StringComparer.Ordinal).ToList();
        Assert.Equal(AllowedConfigRepositoryMethods, config);
    }

    [Fact]
    public void 按钮的克隆不碰按钮授权()
    {
        var reachable = Reachable();
        // 授权镜子（每个按钮当前多少用户/组可用）与授权写路径都不在配置写面里。
        Assert.DoesNotContain(reachable, method => method.DeclaringType == typeof(DocumentActionAuthorization));
        Assert.DoesNotContain(reachable, method =>
            method.DeclaringType?.Name is "RightsAdminRepository" or "UserAdminRepository"
                or "DocumentActionAuthorizationRepository");
    }

    [Fact]
    public void 配置写工具名里没有职权类动词()
    {
        string[] privilegeVerbs = ["rights", "permission", "role", "grant", "user", "menu", "权限", "授权"];
        var configTools = IlCallGraph.ToolNames()
            .Where(name => name.Contains("config", StringComparison.OrdinalIgnoreCase)).ToList();
        Assert.NotEmpty(configTools);

        foreach (var name in configTools)
        {
            foreach (var verb in privilegeVerbs)
            {
                Assert.DoesNotContain(verb, name, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    /// <summary>
    /// 配置写面 = 写服务与三个工具 + 对照卡端点（不把只读的解释/发现不一致算进来：
    /// 那是另一条只读路径，混在一起会掩盖写面的边界）。
    /// </summary>
    private static List<System.Reflection.MethodBase> Reachable() =>
    [
        .. ConfigWriteTypes().SelectMany(IlCallGraph.CalledMethods),
    ];

    private static IEnumerable<Type> ConfigWriteTypes() =>
    [
        .. IlCallGraph.WithNestedTypes(typeof(ConfigWriteService)),
        .. IlCallGraph.WithNestedTypes(typeof(ConfigClonePlanner)),
        .. IlCallGraph.WithNestedTypes(typeof(ConfigDryRunner)),
        .. IlCallGraph.WithNestedTypes(typeof(AssistantConfigWriteOptions)),
        .. IlCallGraph.WithNestedTypes(typeof(AssistantConfigArguments)),
        .. IlCallGraph.WithNestedTypes(typeof(ConfigWriteDtos)),
        .. IlCallGraph.WithNestedTypes(typeof(ConfigWriteToolBase)),
        .. IlCallGraph.WithNestedTypes(typeof(ConfigWriteText)),
        .. IlCallGraph.WithNestedTypes(typeof(CloneModuleConfigTool)),
        .. IlCallGraph.WithNestedTypes(typeof(PreviewConfigChangeTool)),
        .. IlCallGraph.WithNestedTypes(typeof(ApplyConfigChangeTool)),
        .. IlCallGraph.WithNestedTypes(typeof(AssistantConfigController)),
    ];
}
