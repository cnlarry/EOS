using System.Reflection;
using EOS.API.Data;
using EOS.API.Data.Workbench;
using EOS.API.Features.Assistant.Actions;
using EOS.API.Features.Assistant.Tools;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// **批核族在助手侧结构上不可达**：批核 / 解批 / 结案 / 取消结案是职权行使，不可代签，
/// 因此它们不是"约定不要调用"，而是**类型面与调用面上都不存在**。本用例从三处断言：
///
/// <list type="number">
/// <item>动作枚举只有新增 / 修改 / 删除，"批核"这类动作**表达不出来**；</item>
/// <item>助手工具注册面里没有任何批核族工具名；</item>
/// <item>助手动作层的方法体里**实际调不到**审批与结案入口——从 IL 的调用点上断言，
/// 看见的不是"没写"，而是"调不到"。</item>
/// </list>
///
/// 判别性：把任一分支接进助手动作面（例如动作层改调 <c>WorkbenchApprovalService</c>、
/// 或给动作枚举加一个批核成员），本用例必须变红。
/// </summary>
public sealed class NoApprovalEndpointCallTests
{
    /// <summary>批核族动作名（英文与中文各一份，用于名字面与描述面的断言）。</summary>
    private static readonly string[] ApprovalActionVerbs =
        ["approve", "deapprove", "endcase", "unendcase", "finish", "批核", "解批", "结案", "取消结案", "审批"];

    /// <summary>助手动作层允许触达的仓储方法：只有既有写管线的三个（按名排序，便于与实测集合对拍）。</summary>
    private static readonly string[] AllowedRepositoryMethods =
        ["CreateRecordAsync", "DeleteRecordAsync", "UpdateRecordAsync"];

    [Fact]
    public void 动作枚举表达不出批核族动作()
    {
        var kinds = Enum.GetNames<AssistantRecordActionKind>();
        Assert.Equal(new[] { "Insert", "Update", "Delete" }, kinds);
        // 反向对照：合法动作都能解析出来（判别性来自上面那条"只有三个"）
        foreach (var name in new[] { "insert", "update", "delete" })
        {
            Assert.True(AssistantRecordActionNames.TryParse(name, out _), name);
        }
        Assert.False(AssistantRecordActionNames.TryParse("approve", out _));
    }

    [Fact]
    public void 工具注册面里没有批核族工具()
    {
        var toolTypes = typeof(IAssistantTool).Assembly.GetTypes()
            .Where(type => typeof(IAssistantTool).IsAssignableFrom(type)
                && !type.IsAbstract && !type.IsInterface)
            .ToList();
        Assert.NotEmpty(toolTypes);

        // 工具名是能力清单：出现一个 approve_record 之类的名字，就是批核族进了助手动作面。
        // 名字取自类型名、工具声明的名字常量与可构造实例三者之并集——少读一处才可能漏判。
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var type in toolTypes)
        {
            names.Add(type.Name);
            foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Static)
                .Where(field => field.IsLiteral && field.FieldType == typeof(string)))
            {
                if (field.GetRawConstantValue() is string value)
                {
                    names.Add(value);
                }
            }
            if (TryCreateTool(type) is { } instance)
            {
                names.Add(instance.Name);
            }
        }

        foreach (var name in names)
        {
            foreach (var verb in ApprovalActionVerbs)
            {
                Assert.DoesNotContain(verb, name, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    [Fact]
    public void 助手动作层调不到审批与结案入口()
    {
        var reachable = ActionLayerTypes()
            .SelectMany(IlCallGraph.CalledMethods)
            .ToList();

        Assert.Contains(reachable, method =>
            method.DeclaringType == typeof(DocumentWorkbenchRepository)
            && method.Name == nameof(DocumentWorkbenchRepository.CreateRecordAsync));

        // 只在既有写管线的三个方法上落库——多一个写成别的方法就是绕开了它们
        var repositoryMethods = reachable
            .Where(method => method.DeclaringType == typeof(DocumentWorkbenchRepository))
            .Select(method => method.Name)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();
        Assert.Equal(AllowedRepositoryMethods, repositoryMethods);

        // 审批与结案入口：动作层连"能调到"都不成立
        Assert.DoesNotContain(reachable, method =>
            method.DeclaringType == typeof(WorkbenchApprovalService)
            || method.DeclaringType == typeof(WorkflowEngine)
            || (method.DeclaringType == typeof(DocumentWorkbenchRepository)
                && method.Name is nameof(DocumentWorkbenchRepository.WorkflowAsync)
                    or nameof(DocumentWorkbenchRepository.FinishAsync)));
    }

    // ===== 反射工具 =====

    private static IReadOnlyList<Type> ActionLayerTypes() =>
    [
        .. typeof(AssistantActionGate).Assembly.GetTypes()
            .Where(type => type.Namespace == typeof(AssistantActionGate).Namespace)
            .SelectMany(IlCallGraph.WithNestedTypes),
        .. IlCallGraph.WithNestedTypes(typeof(PreviewRecordActionTool)),
        .. IlCallGraph.WithNestedTypes(typeof(ApplyRecordActionTool)),
        // 请求卡的工具只"准备请求"：它同样被这句话管住——准备请求不等于取得执行能力。
        .. IlCallGraph.WithNestedTypes(typeof(PreviewBatchDecisionTool)),
    ];

    /// <summary>
    /// 请求卡的逐行预判是**只读**：它复用策略层的批核族授权入口与流向判定，
    /// 但不落库、不调审批服务、也不新增批量执行入口。
    /// </summary>
    [Fact]
    public void 请求卡的预判只读()
    {
        var service = IlCallGraph.WithNestedTypes(typeof(AssistantApprovalRequestService))
            .SelectMany(IlCallGraph.CalledMethods)
            .ToList();

        Assert.Contains(service, method =>
            method.DeclaringType == typeof(WorkbenchAccessPolicy)
            && method.Name == nameof(WorkbenchAccessPolicy.AuthorizeWorkflowAsync));
        Assert.DoesNotContain(service, method => method.DeclaringType == typeof(DocumentWorkbenchRepository));
        Assert.DoesNotContain(service, method =>
            method.DeclaringType == typeof(WorkbenchApprovalService)
            || method.DeclaringType == typeof(WorkflowEngine));
    }

    /// <summary>工具实例：只需读取名称，构造依赖（网关、权限服务）与本断言无关。</summary>
    private static IAssistantTool? TryCreateTool(Type type)
    {
        var constructor = type.GetConstructors().OrderBy(item => item.GetParameters().Length).FirstOrDefault();
        if (constructor is null)
        {
            return null;
        }
        var arguments = constructor.GetParameters()
            .Select(parameter => parameter.ParameterType.IsValueType
                ? Activator.CreateInstance(parameter.ParameterType)
                : null)
            .ToArray();
        try
        {
            return constructor.Invoke(arguments) as IAssistantTool;
        }
        catch (Exception)
        {
            // 构造不了的实现无从读取名字，跳过（名字面的断言仍由其余实现承担）
            return null;
        }
    }
}
