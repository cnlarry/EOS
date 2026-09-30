using System.Reflection;
using System.Text;
using EOS.API.Data;
using EOS.API.Data.Effects;
using EOS.API.Data.ValidationRules;
using EOS.API.Features.Assistant.Tools;
using EOS.API.Models;
using EOS.API.Security;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.Extensions.Options;

namespace EOS.API.Features.Assistant.Catalog;

/// <summary>机制主题闭集：助手回答"系统是怎么运作的"时的九个入口。</summary>
public enum CapabilityTopic
{
    Workbench,
    Form,
    Fields,
    Chooser,
    Report,
    Module,
    Effect,
    Validation,
    Endpoint,
}

/// <summary>目录产出的一条可核对标识；<see cref="Kind"/> 决定它该去哪张表/哪个注册表复核。</summary>
public enum CapabilityFactKind
{
    Module,
    Table,
    EffectKey,
    ValidationKey,
    SourceKey,
    ReportId,
    Route,
}

public sealed record CapabilityFact(CapabilityFactKind Kind, string Value);

/// <summary>一条机制解释：人话（<see cref="Text"/>）+ 它引用的标识（<see cref="Facts"/>，供一致性核对）。</summary>
public sealed record CapabilityExplanation(
    CapabilityTopic Topic, string Text, IReadOnlyList<CapabilityFact> Facts);

/// <summary>
/// 系统能力目录：回答"系统是怎么运作的"。**机制的真值在元数据与代码注册表里**，
/// 因此本类只做**读取与装配**——不落一份机制 Markdown/JSON 当第二真源，
/// 每条解释里的标识符都来自既有表、既有注册表或程序集反射，并可逐条复核
/// （<see cref="CapabilityFact"/> 就是为“目录与元数据一致”的门禁准备的）。
/// </summary>
public sealed class SystemCapabilityCatalog(
    IAssistantSchemaGateway schema,
    IOptions<UnifiedFormEditorSettings> formSettings)
{
    /// <summary>解释里逐条列出的标识上限（超限如实说明，不静默截断）。</summary>
    private const int MaxListedFacts = 60;

    private const int MaxRoutesListed = 40;

    public static readonly IReadOnlyList<CapabilityTopic> Topics =
    [
        CapabilityTopic.Workbench,
        CapabilityTopic.Form,
        CapabilityTopic.Fields,
        CapabilityTopic.Chooser,
        CapabilityTopic.Report,
        CapabilityTopic.Module,
        CapabilityTopic.Effect,
        CapabilityTopic.Validation,
        CapabilityTopic.Endpoint,
    ];

    /// <summary>
    /// 配置域主题：效果链 / 校验规则 / 端点清单属"怎么配、系统怎么接的"，
    /// 与配置面同档（模块 2302 的 <c>CanSetup</c>）。其余主题只讲机制，登录即可问。
    /// </summary>
    public static bool RequiresSetup(CapabilityTopic topic) =>
        topic is CapabilityTopic.Effect or CapabilityTopic.Validation or CapabilityTopic.Endpoint;

    /// <summary>主题名解析（大小写不敏感）；未知主题返回 false，由调用方如实拒绝。</summary>
    public static bool TryParseTopic(string? value, out CapabilityTopic topic)
    {
        topic = default;
        var text = (value ?? string.Empty).Trim();
        if (text.Length == 0) return false;
        foreach (var candidate in Topics)
        {
            if (!candidate.ToString().Equals(text, StringComparison.OrdinalIgnoreCase)) continue;
            topic = candidate;
            return true;
        }

        return false;
    }

    public static string TopicList() => string.Join(" / ", Topics.Select(t => t.ToString().ToLowerInvariant()));

    public async Task<CapabilityExplanation> DescribeAsync(CapabilityTopic topic, CancellationToken token) => topic switch
    {
        CapabilityTopic.Workbench => BuildWorkbench(),
        CapabilityTopic.Form => BuildForm(),
        CapabilityTopic.Fields => await BuildFieldsAsync(token),
        CapabilityTopic.Chooser => BuildChooser(),
        CapabilityTopic.Report => BuildReport(),
        CapabilityTopic.Module => await BuildModuleAsync(token),
        CapabilityTopic.Effect => BuildEffect(),
        CapabilityTopic.Validation => BuildValidation(),
        CapabilityTopic.Endpoint => BuildEndpoint(),
        _ => throw new ArgumentOutOfRangeException(nameof(topic), topic, null),
    };

    private CapabilityExplanation BuildWorkbench()
    {
        var sb = new StringBuilder();
        sb.AppendLine("统一工作台（列表）：界面形态由库内元数据驱动，改元数据即改界面。");
        sb.AppendLine("- 数据通路：列表数据经 API 的工作台端点按当前用户取数，EXEC_TAG / DATA_FILTER / 模块 FILTER / 字段隐藏全部生效；前端不直连库。");
        sb.AppendLine("- 承载页契约：列表 `/workbench/{moduleId}`；统一表单 `/workbench/{moduleId}/{new|edit|view}`；非工作台承载页（报表 / 搜索中心 / 明细查询）由模块的 M_URL 决定。");
        sb.AppendLine("- 列表能力：首列选择、表头排序、列宽拖拽、复制、键盘导航、吸顶表头由统一电子表格提供；列宽只在本页本地持久化。");
        sb.AppendLine("- 定义来源：已发布定义快照（按当前配置重建并比对），快照落后于配置即报落后。");
        sb.AppendLine("- 结构：模块（MODULES）→ 表（TABLES）→ 字段（FIELDS），三者是同一条元数据链，不存在第二份界面定义。");
        sb.Append("真值来源：EOS.API/Data/WorkbenchDefinitionBuilder.cs、WorkbenchDefinitionProvider.cs、DocumentWorkbenchRepository.cs。");
        return new(CapabilityTopic.Workbench, sb.ToString(), []);
    }

    private CapabilityExplanation BuildForm()
    {
        var settings = formSettings.Value;
        var writable = settings.EnabledModuleIds ?? [];
        var readOnly = settings.ReadOnlyModuleIds ?? [];
        var sb = new StringBuilder();
        sb.AppendLine("统一表单（单据）：模块是否可写由服务端名单决定，名单外模块只能浏览。");
        sb.AppendLine($"- 写名单（可新增/修改/删除）：{writable.Length} 个模块；只读名单：{readOnly.Length} 个模块。");
        sb.AppendLine("- 字段面与可写性来自表单定义（mode=new/edit/view），与保存路径同一份口径；主键与引擎维护列不作输入。");
        sb.AppendLine("- 单据级动作全部在统一表单工具栏（浏览态：返回、上下条、新增、复制、编辑、删除、批核|解批、审批历史、结案|取消结案、附件、打印、帮助；编辑态：保存/取消），顺序固定，配置只决定动作有无。");
        sb.AppendLine("- 新增/修改/删除经既有写管线：校验规则、效果链、状态翻转、审计都在其中，不存在第二条写路径。");
        sb.AppendLine("- 删除等破坏性操作必须先在浏览态看到单据细节再执行，列表页不提供“选复选框即删”。");
        sb.Append("真值来源：EOS.API/Models/UnifiedFormEditorSettings.cs、EOS.API/Data/Forms、EOS.Web/src/components/common/commandActions.tsx。");
        var facts = new List<CapabilityFact>();
        facts.AddRange(writable.Select(id => new CapabilityFact(CapabilityFactKind.Module, id.ToString())));
        facts.AddRange(readOnly.Select(id => new CapabilityFact(CapabilityFactKind.Module, id.ToString())));
        return new(CapabilityTopic.Form, sb.ToString(), facts);
    }

    private async Task<CapabilityExplanation> BuildFieldsAsync(CancellationToken token)
    {
        var tables = await schema.ListTablesAsync(null, token);
        var listed = tables.Take(MaxListedFacts).ToList();
        var sb = new StringBuilder();
        sb.AppendLine("字段元数据：字段的显示名、可见/默认列/可查询/只读/必填、成本位与保密位、默认值、虚拟表达式都在元数据里，改元数据即改界面。");
        sb.AppendLine("- 全局字段显示名在 FIELDS 的显示名列；个人自定义列与系统默认列分属两张用户/系统列配置表，用户配置优先于默认。");
        sb.AppendLine("- 虚拟字段只在标记为虚拟的字段上生效，表达式随字段保存即生效（保存前经受控解析器校验，不通过则整单拒绝），没有“校验→预览→发布”三步流。");
        sb.AppendLine("- 受限表达式（虚拟表达式、受控转换函数、数据过滤）不得未经受控解析进入 SQL 或业务运行时；产品别名（如 PRODUCT 的别名）不是物理表。");
        sb.AppendLine($"- 当前登记为业务表的元数据共 {tables.Count} 张（下表按元数据顺序列出前 {listed.Count} 张）。");
        foreach (var table in listed)
        {
            sb.AppendLine($"  · {table.TableId} {table.Description}");
        }

        if (tables.Count > listed.Count) sb.AppendLine($"（其余 {tables.Count - listed.Count} 张未列出，可用 describe_table 按表查字段）");
        sb.Append("真值来源：EOS.API/Data/FieldAdminRepository.cs、WorkbenchFieldMetaMapper.cs、VirtualColumnResolver.cs、RestrictedExpressionService.cs。");
        return new(CapabilityTopic.Fields, sb.ToString(),
            [.. listed.Select(table => new CapabilityFact(CapabilityFactKind.Table, table.TableId))]);
    }

    private CapabilityExplanation BuildChooser()
    {
        var keys = ChooserRepository.RegisteredSourceKeys;
        var sb = new StringBuilder();
        sb.AppendLine("统一选择器：凡需从既有业务数据里选记录（员工、客户、供应商、物料、产品、仓库、部门、模块、表、字段、报表等），一律走同一个选择器组件。");
        sb.AppendLine("- 数据源只有两种：表单字段（复用 CHOOSE_* 字段元数据）与服务端注册数据源（sourceKey，经选择器查询端点取数）；新代码不新增第三种。");
        sb.AppendLine("- 列与默认列来自服务端元数据或受控虚拟查找白名单，前端不硬编码列清单。");
        sb.AppendLine($"- 服务端已注册数据源 {keys.Count} 个（表名/列名只来自注册表，查询全部参数化，按数据源分别挂权限门）：");
        foreach (var key in keys)
        {
            var moduleId = ChooserRepository.PermissionModuleId(key);
            sb.AppendLine($"  · {key}" + (moduleId is null ? "（仅登录可读）" : $"（模块 {moduleId}）"));
        }

        sb.Append("真值来源：EOS.API/Data/ChooserRepository.cs、EOS.API/Controllers/ChooserController.cs、EOS.Web/src/components/common/UnifiedChooser.tsx。");
        return new(CapabilityTopic.Chooser, sb.ToString(),
            [.. keys.Select(key => new CapabilityFact(CapabilityFactKind.SourceKey, key))]);
    }

    private CapabilityExplanation BuildReport()
    {
        var aggregates = ReportAggregateRegistry.RegisteredReportIds;
        var sb = new StringBuilder();
        sb.AppendLine("报表是“业务模块的资源”，可见性与权限都挂在归属模块上。");
        sb.AppendLine("- 报表身份 = 报表编号：主键、全库唯一、不可变；规范 URL 为 /report/{reportId}。");
        sb.AppendLine("- 归属 = 报表表指向的业务模块，它是数据集与筛选条件、字段级权限、行级权限、模块功能权限的唯一锚点；报表不关联物理表元数据。");
        sb.AppendLine("- 报表不进菜单：发现途径是报表中心目录、搜索、深链与情境入口（统一表单工具栏的「报表」动作）。");
        sb.AppendLine("- 可见性唯一真源 = 归属模块的报表权限位（个人权限优先，否则组权限取或）；没有“逐报表再配一次权限”的第二处真源。");
        sb.AppendLine("- 条件随模块：条件模板按归属模块存取；打印走版式资产 + 解释层，不新增命令式渲染旁路。");
        sb.AppendLine($"- 跨表聚合的唯一出口是聚合注册表，当前登记 {aggregates.Count} 个跨表聚合报表：{string.Join("、", aggregates)}");
        sb.Append("真值来源：EOS.API/Data/ReportAggregateRegistry.cs、ReportRepository.cs、EOS.API/Controllers/ReportController.cs。");
        return new(CapabilityTopic.Report, sb.ToString(),
            [.. aggregates.Select(id => new CapabilityFact(CapabilityFactKind.ReportId, id))]);
    }

    private async Task<CapabilityExplanation> BuildModuleAsync(CancellationToken token)
    {
        var modules = await schema.ListModulesAsync(token);
        var listed = modules.Take(MaxListedFacts).ToList();
        var named = typeof(ModuleIds).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.IsLiteral && field.FieldType == typeof(int))
            .Select(field => ((int)field.GetRawConstantValue()!).ToString())
            .Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToList();
        var sb = new StringBuilder();
        sb.AppendLine("模块：一个模块 = 库内一行模块定义（模块号唯一），它决定这个业务对象的界面形态、主/明细表、承载页与可用动作。");
        sb.AppendLine("- 元数据链：模块 → 表 → 字段；改元数据即改界面，不为单个功能另建一份界面定义。");
        sb.AppendLine("- 承载页：模块里的页面 URL 只存承载页路径（列表 / 报表 / 搜索中心 / 明细查询等在渲染时自动追加模块号）；新增/编辑路由可指向统一表单（模块号由服务端替换）或已登记的精确路径；外部链接与脚本链接一律拒绝并回退占位页。");
        sb.AppendLine("- 有专属页面或流程的模块号在代码里登记为具名常量（避免各处裸写数字）；定制页路由必须三处一致：库内模块登记、后端精确路径、前端路由表。");
        sb.AppendLine($"- 库内模块共 {modules.Count} 个，其中代码具名常量 {named.Count} 个。");
        sb.AppendLine($"- 模块清单（前 {listed.Count} 个，按名称排序）：");
        foreach (var module in listed)
        {
            sb.AppendLine($"  · {module.Id} {module.Label}");
        }

        if (modules.Count > listed.Count) sb.AppendLine($"（其余 {modules.Count - listed.Count} 个未列出，可用 describe_module 按模块号查表结构）");
        sb.Append("真值来源：dbo.MODULES、EOS.API/Security/ModuleIds.cs、EOS.API/Data/ModuleRouteValidator.cs。");
        var facts = new List<CapabilityFact>();
        facts.AddRange(listed.Select(module => new CapabilityFact(CapabilityFactKind.Module, module.Id.ToString())));
        facts.AddRange(named.Select(id => new CapabilityFact(CapabilityFactKind.Module, id)));
        return new(CapabilityTopic.Module, sb.ToString(), facts);
    }

    private CapabilityExplanation BuildEffect()
    {
        var events = BusinessActionCatalog.Events.OrderBy(value => value, StringComparer.Ordinal).ToList();
        var registry = EffectRegistry.Keys.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase).ToList();
        var runnable = registry.Count(pair => pair.Value is EffectRegistry.Status.Formula or EffectRegistry.Status.Service);
        var sb = new StringBuilder();
        sb.AppendLine("效果链：模块声明的业务动作（事件 + 效果键 + 参数）在单据生命周期节点上触发；公式行由公式解释器执行，服务型效果由已注册处理器执行。");
        sb.AppendLine($"- 事件闭集：{string.Join(" / ", events)}；其中用户点击类事件不参与效果链。");
        sb.AppendLine($"- 不可产生效果的事件：{string.Join(" / ", BusinessActionCatalog.InertEvents.OrderBy(v => v, StringComparer.Ordinal))}。");
        sb.AppendLine($"- 效果键注册表共 {registry.Count} 个，可执行（公式或服务）{runnable} 个；未实现的效果键在配置里能选中、但执行时会被拒绝，不会猜着跑。");
        sb.AppendLine("- 效果键的说明与参数 Schema 是配置面的一部分（与键一起下发），说明缺口会如实报出，不编一段。");
        sb.AppendLine("- 反向结构只认一个根键（说明字段名以外的自由文本不会被任何代码读取）。");
        foreach (var group in registry.GroupBy(pair => pair.Value).OrderBy(group => group.Key))
        {
            sb.AppendLine($"  · {group.Key}（{group.Count()}）：{string.Join("、", group.Select(pair => pair.Key))}");
        }

        sb.Append("真值来源：EOS.API/Data/BusinessActionCatalog.cs、EOS.API/Data/Effects/EffectRegistry.cs、EffectParamDescriptors.cs。");
        return new(CapabilityTopic.Effect, sb.ToString(),
            [.. registry.Select(pair => new CapabilityFact(CapabilityFactKind.EffectKey, pair.Key))]);
    }

    private CapabilityExplanation BuildValidation()
    {
        var stages = BusinessActionCatalog.ValidationStages.OrderBy(value => value, StringComparer.Ordinal).ToList();
        var keys = BusinessActionCatalog.ValidationKeys.OrderBy(value => value, StringComparer.Ordinal).ToList();
        var sb = new StringBuilder();
        sb.AppendLine("校验规则：挂在模块上，按阶段在保存 / 批核 / 解批 / 删除时执行；规则清单随定义快照发布，运行时按快照取值。");
        sb.AppendLine($"- 阶段闭集：{string.Join(" / ", stages)}。");
        sb.AppendLine($"- 模板键闭集（{keys.Count} 个）：{string.Join("、", keys)}。");
        sb.AppendLine("- 规则参数只有一个根结构，参数键由注册表逐个白名单校验；结构不合法即拒存，不留“配了但不读”的参数。");
        sb.AppendLine("- 规则清单与运行时的逐条对拍是可验证的：库内配置必须全部落在代码闭集内、且全部进入当前快照。");
        sb.AppendLine("- 只读诊断只复核一次点查即可定论的判据，其余如实写“证据不足”，不在诊断里复刻业务校验。");
        sb.Append("真值来源：EOS.API/Data/BusinessActionCatalog.cs、EOS.API/Data/ValidationRules/ValidationRuleRegistry.cs、MODULE_VALIDATION_RULE。");
        return new(CapabilityTopic.Validation, sb.ToString(),
            [.. keys.Select(key => new CapabilityFact(CapabilityFactKind.ValidationKey, key))]);
    }

    private static CapabilityExplanation BuildEndpoint()
    {
        var routes = ControllerRoutes();
        var listed = routes.Take(MaxRoutesListed).ToList();
        var sb = new StringBuilder();
        sb.AppendLine("HTTP 端点：全部业务端点挂在 API 上，前缀形如 api/v1/<资源>；没有集中的路由常量表，以各控制器的路由特性为准，本清单由程序集反射现算（不落第二份路由目录）。");
        sb.AppendLine("- 权限边界在服务端：每个请求按当前用户重新授权，未登录 401、已登录但缺权限 403。");
        sb.AppendLine("- 防探测：模块不在可浏览范围、记录不在数据范围一律 404，不区分“不存在”与“无权限”；回答“我自己为什么做不了这件事”必须明说缺哪个动作位。");
        sb.AppendLine($"- 当前登记端点 {routes.Count} 个，列出前 {listed.Count} 个：");
        foreach (var route in listed) sb.AppendLine($"  · {route}");
        if (routes.Count > listed.Count) sb.AppendLine($"（其余 {routes.Count - listed.Count} 个未列出）");
        sb.Append("真值来源：EOS.API/Controllers/*.cs 的 Route / HttpMethod 特性（本主题由反射装配）。");
        return new(CapabilityTopic.Endpoint, sb.ToString(),
            [.. routes.Select(route => new CapabilityFact(CapabilityFactKind.Route, route))]);
    }

    /// <summary>按程序集反射列出控制器端点：(方法 / 路由模板)，按序稳定。</summary>
    internal static IReadOnlyList<string> ControllerRoutes()
    {
        var assembly = typeof(SystemCapabilityCatalog).Assembly;
        var routes = new List<string>();
        Type[] types;
        try
        {
            types = assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            types = [.. ex.Types.Where(type => type is not null).Select(type => type!)];
        }

        foreach (var type in types)
        {
            if (type.IsAbstract || !typeof(ControllerBase).IsAssignableFrom(type)) continue;
            var prefix = type.GetCustomAttribute<RouteAttribute>()?.Template?.Trim().Trim('/') ?? string.Empty;
            if (prefix.Length == 0) continue;
            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                foreach (var attribute in method.GetCustomAttributes<HttpMethodAttribute>())
                {
                    var verbs = string.Join("|", attribute.HttpMethods.OrderBy(value => value, StringComparer.Ordinal));
                    var suffix = attribute.Template is null ? string.Empty : attribute.Template.Trim().Trim('/');
                    routes.Add(suffix.Length == 0 ? $"{verbs} /{prefix}" : $"{verbs} /{prefix}/{suffix}");
                }
            }
        }

        routes.Sort(StringComparer.Ordinal);
        return routes;
    }
}
