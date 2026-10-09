using System.Data;
using System.Text.Json;
using System.Text.RegularExpressions;
using EOS.API.Data.DocumentActions;
using EOS.API.Data.Effects;
using EOS.API.Models;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;

namespace EOS.API.Data;

/// <summary>
/// Workbench Definition 发布前校验器：
/// 覆盖发布前必须校验的全部条目（白名单/路由、主表/子表/主键/关联键、字段白名单、
/// FILTER/分组表达式受控解析、虚拟列/转换函数、成本/保密/拒绝字段、高风险表达式、
/// 统一表单定义），输出机器可读报告（code/passed/message），供 CI、发布工具与
/// 开发 Agent 消费；校验未通过的定义不发布到运行时。
/// 与 预检共用同一道闸（流水线启用前先调服务端校验）。
/// </summary>
public sealed class WorkbenchDefinitionValidator(
    DbConnectionFactory connections,
    DocumentWorkbenchRepository workbench,
    WorkbenchDefinitionBuilder definitionBuilder,
    ModuleBusinessConfigRepository configRepository,
    DocumentActionRegistry documentActions,
    DocumentActionAuthorization documentActionAuthorization,
    IOptions<UnifiedFormEditorSettings> formSettings,
    ILogger<WorkbenchDefinitionValidator> logger)
{

    public async Task<WorkbenchDefinitionValidationReport> ValidateAsync(
        int moduleId,
        string userId,
        CancellationToken token)
    {
        var checks = new List<WorkbenchDefinitionValidationCheck>();
        await using var connection = connections.Create();
        await connection.OpenAsync(token);

        var module = await ReadModuleAsync(connection, moduleId, token);
        if (module is null)
        {
            checks.Add(new WorkbenchDefinitionValidationCheck("module_exists", false, $"模块 {moduleId} 不存在。"));
            return new(moduleId, string.Empty, false, $"module-{moduleId}-draft", checks);
        }
        checks.Add(new("module_exists", true, $"模块 {moduleId} {module.Title} 存在。"));

        // 写名单与只读名单都表示"运行期有统一表单"，区别只在能不能写
        var enabled = formSettings.Value.EnabledModuleIds.Contains(moduleId)
            || formSettings.Value.ReadOnlyModuleIds.Contains(moduleId);
        var workbenchModule = ModuleRouteValidator.IsWorkbenchModule(module.MUrl, module.MasterTable);
        if (!workbenchModule && !enabled)
        {
            checks.Add(new("runtime_whitelist", false,
                "模块既不在统一表单名单内，承载页也不是统一工作台（M_URL 不是 /workbench，也不是\"留空 + 有主表\"），无法进入运行时。"));
        }
        else
        {
            checks.Add(new("runtime_whitelist", true,
                workbenchModule ? "承载页是统一工作台（M_URL=/workbench，或留空但有主表）。" : "模块在统一表单名单内。"));
        }

        // 路由契约：只剩 M_URL 一个字段（NEW_URL / MODI_URL / HELP_URL 随迁移 321 物理删除）。
        // 这里只拦"退场形态"——已删掉的动作模板不得复活；不校验精确路径白名单，
        // 以免历史上那类 /legacy/... 占位值把发布卡死（它们本来就落占位页，安全降级）。
        var retiredTemplates = new[] { "/workbench/{moduleId}/new", "/workbench/{moduleId}/edit", "/workbench/{moduleId}/view" };
        var routeError = retiredTemplates
            .FirstOrDefault(template => string.Equals(module.MUrl.Trim(), template, StringComparison.OrdinalIgnoreCase)) is { } revived
            ? $"M_URL 写了已退场的动作模板（新增/修改路由随迁移 321 删除，请改填承载页）：{revived}"
            : null;
        checks.Add(routeError is null
            ? new("route_valid", true, "路由契约合法（只认 M_URL 承载页，不再有新增/修改路由）。")
            : new("route_valid", false, routeError));

        var masterOk = WorkbenchSql.Identifier.IsMatch(module.MasterTable) && await WorkbenchSql.TableExistsAsync(connection, module.MasterTable, token);
        checks.Add(masterOk
            ? new("master_table_exists", true, $"主表 {module.MasterTable} 存在。")
            : new("master_table_exists", false, $"主表 {module.MasterTable} 不存在或标识符非法。"));

        if (module.DetailTable is { Length: > 0 })
        {
            var detailOk = WorkbenchSql.Identifier.IsMatch(module.DetailTable) && await WorkbenchSql.TableExistsAsync(connection, module.DetailTable, token);
            checks.Add(detailOk
                ? new("detail_table_exists", true, $"子表 {module.DetailTable} 存在。")
                : new("detail_table_exists", false, $"子表 {module.DetailTable} 不存在或标识符非法。"));
        }
        else
        {
            checks.Add(new("detail_table_exists", true, "模块无子表。"));
        }

        IReadOnlyList<string> pkColumns = [];
        if (masterOk)
        {
            pkColumns = await WorkbenchSql.GetPrimaryKeyColumnsAsync(connection, null, module.MasterTable, token);
            checks.Add(pkColumns.Count > 0
                ? new("master_pk_exists", true, $"主表主键：{string.Join(",", pkColumns)}。")
                : new("master_pk_exists", false, "主表缺少主键定义。"));
        }
        else
        {
            checks.Add(new("master_pk_exists", false, "主表不可用，跳过主键校验。"));
        }

        if (module.DetailTable is { Length: > 0 } && masterOk && pkColumns.Count > 0)
        {
            var detailColumns = await GetTableColumnsAsync(connection, module.DetailTable, token);
            var missing = pkColumns.Where(pk => !detailColumns.Contains(pk, StringComparer.OrdinalIgnoreCase)).ToList();
            checks.Add(missing.Count == 0
                ? new("detail_association_keys", true, "子表包含主表全部主键关联列。")
                : new("detail_association_keys", false, $"子表缺少主表关联键：{string.Join(",", missing)}。"));
        }
        else if (module.DetailTable is { Length: > 0 })
        {
            checks.Add(new("detail_association_keys", false, "主表不可用，无法校验子表关联键。"));
        }
        else
        {
            checks.Add(new("detail_association_keys", true, "模块无子表，无需关联键。"));
        }

        var allowedFields = masterOk
            ? await ReadFilterFieldKeysAsync(connection, null, module.MasterTable, token)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (!string.IsNullOrWhiteSpace(module.Filter))
        {
            var filterOk = DataFilterParser.TryParse(module.Filter, module.MasterTable, allowedFields, out _, out _);
            checks.Add(filterOk
                ? new("filter_expression", true, "模块 FILTER 经受控解析器校验。")
                : new("filter_expression", false, "模块 FILTER 无法按受控解析器解析，禁止发布。"));
        }
        else
        {
            checks.Add(new("filter_expression", true, "模块无 FILTER。"));
        }

        var groupErrors = new List<string>();
        foreach (var (groupId, expression) in module.GroupExpressions)
        {
            if (string.IsNullOrWhiteSpace(expression))
            {
                continue;
            }
            if (!GroupExpressionParser.TryCompile(expression, module.MasterTable, allowedFields, out _))
            {
                groupErrors.Add($"GROUP{groupId}");
            }
        }
        checks.Add(groupErrors.Count == 0
            ? new("group_expressions", true, "分组表达式经受控编译器校验。")
            : new("group_expressions", false, $"分组表达式无法编译：{string.Join(",", groupErrors)}。"));

        var tables = module.DetailTable is { Length: > 0 }
            ? new[] { module.MasterTable, module.DetailTable }
            : new[] { module.MasterTable };
        var virtualErrors = new List<string>();
        foreach (var table in tables)
        {
            var virtualFields = await ReadVirtualFieldsAsync(connection, table, token);
            if (virtualFields.Count == 0)
            {
                continue;
            }
            var resolution = await new VirtualColumnResolver(connection).ResolveAsync(table, virtualFields, token);
            foreach (var key in resolution.UnresolvedKeys)
            {
                virtualErrors.Add($"{table}.{key}");
            }
        }
        checks.Add(virtualErrors.Count == 0
            ? new("virtual_columns_resolve", true, "虚拟列全部经受控解析器解析。")
            : new("virtual_columns_resolve", false, $"虚拟列无法解析：{string.Join(",", virtualErrors)}。"));

        var convertErrors = new List<string>();
        foreach (var table in tables)
        {
            foreach (var (field, function) in await ReadConvertFunctionsAsync(connection, table, token))
            {
                if (!RestrictedExpressionService.ConvertFunctionRegistry.ContainsKey(function))
                {
                    convertErrors.Add($"{table}.{field} → {function}");
                }
            }
        }
        checks.Add(convertErrors.Count == 0
            ? new("convert_functions_controlled", true, "CONVERT_FUNCTION 全部在受控函数白名单内。")
            : new("convert_functions_controlled", false, $"CONVERT_FUNCTION 不在白名单：{string.Join(",", convertErrors)}。"));

        WorkbenchDefinition? definition = null;
        if (workbenchModule || enabled)
        {
            // 发布路径的"可构建"校验必须以"元数据重建"后的定义为准（与写入快照的定义一致）：
            // 走 forPublish=true 会忽略已发布基线，让校验反映当前代码+元数据，而不是旧快照。
            definition = await definitionBuilder.GetDefinitionAsync(
                moduleId, userId, "Z", true, true,
                new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                new HashSet<string>(StringComparer.OrdinalIgnoreCase), token,
                forPublish: true);
            checks.Add(definition is not null
                ? new("definition_build", true, "工作台定义可构建（按当前代码+元数据重建）。")
                : new("definition_build", false, "无法构建工作台定义（路由/主表/字段/虚拟列解析失败）。"));
        }
        else
        {
            checks.Add(new("definition_build", false, "模块不在运行白名单，跳过定义构建。"));
        }

        if (enabled || workbenchModule)
        {
            if (definition is not null)
            {
                var form = await workbench.GetFormDefinitionAsync(
                    definition, userId, "new", true, true,
                    new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                    new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                    new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                    new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                    new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                    new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                    token,
                    true, true, true, true, true, true, true, true, true, true, true);
                checks.Add(form is not null
                    ? new("form_definition", true, "统一表单定义可构建（页签/默认值/选择器/必填解析）。")
                    : new("form_definition", false, "统一表单定义无法构建。"));
            }
            else
            {
                checks.Add(new("form_definition", false, "定义构建失败，跳过表单校验。"));
            }
        }
        else
        {
            checks.Add(new("form_definition", true, "模块非工作台/表单模块，跳过表单校验。"));
        }

        // ===== ：界面质量类校验（error 三项拦发布闸；warning 仅质量提示） =====
        if (definition is not null)
        {
            checks.AddRange(await ValidateFormQualityAsync(connection, tables, token));
        }

        var businessConfig = await configRepository.GetAsync(moduleId, token);
        if (businessConfig is not null)
        {
            var configValidation = ModuleBusinessConfigValidator.Validate(
                new SaveModuleBusinessConfigRequest(businessConfig.Actions, businessConfig.ValidationRules),
                documentActions.KeySet);
            checks.Add(configValidation.Ok
                ? new("business_config_valid", true, "业务动作/校验配置通过结构校验。")
                : new("business_config_valid", false,
                    $"业务动作/校验配置结构校验失败：{string.Join("；", configValidation.Messages.Take(5))}{(configValidation.Messages.Count > 5 ? " 等" : "")}。"));
        }
        else
        {
            checks.Add(new("business_config_valid", true, "模块无业务动作/校验配置。"));
        }

        // 效果参数的**物理引用**校验（参数里点名的表和列是否真的存在、能否被各自的处理器解析）。
        // 它此前只在管理端保存配置时执行 ⇒ 经迁移或直写落库的配置没人验、发布也放过，
        // 运行期才在保存/批核那一刻炸。发布门补上同一道校验（与保存端同一个帮手、同一个解析器）。
        if (businessConfig is not null && businessConfig.Actions.Count > 0)
        {
            var physicalIssues = await EffectParamPhysicalGate.RunAsync(
                connection, moduleId, module.MasterTable, module.DetailTable, businessConfig.Actions, token);
            checks.Add(physicalIssues.Count == 0
                ? new("effect_params_physical", true, "效果参数引用的表/列均存在。")
                : new("effect_params_physical", false,
                    $"效果参数引用了不存在的表/列：{string.Join("；", physicalIssues.Take(5))}{(physicalIssues.Count > 5 ? " 等" : "")}。"));
        }
        else
        {
            checks.Add(new("effect_params_physical", true, "模块无效果动作配置。"));
        }

        // 能力 → 列单向强制：具备批核能力的模块必须有 CONFIRM_TAG，
        // 缺列在发布期报错并引导补列，不再允许"无列却有能力"的配置进入运行时。
        if (definition is not null && masterOk)
        {
            var enabledEvents = businessConfig?.Actions
                .Where(action => action.Enabled)
                .Select(action => action.EventCode);
            var needsApprove = WorkflowStates.NeedsApproveColumn(
                definition.AutoApprove,
                definition.EffectEngineEnabled,
                definition.HasWorkflow,
                enabledEvents);
            if (needsApprove)
            {
                var masterColumns = await GetTableColumnsAsync(connection, module.MasterTable, token);
                checks.Add(masterColumns.Contains("CONFIRM_TAG", StringComparer.OrdinalIgnoreCase)
                    ? new("lifecycle_columns", true, "模块具备批核能力且主表有 CONFIRM_TAG。")
                    : new("lifecycle_columns", false,
                        $"模块具备批核能力（自动批核/批核过程/效果引擎接管/工作流/批核效果链）但主表 {module.MasterTable} 缺少 CONFIRM_TAG：请补列后重发布，或关闭对应批核能力。"));
            }
            else
            {
                checks.Add(new("lifecycle_columns", true, "模块无批核能力，无需状态位。"));
            }
        }

        // 用户点击类动作的键必须已在单据操作注册表中登记（未登记即拒发布）。
        // 与 effect_engine_keys_implemented 同款 fail-closed，但判据不同：
        // 效果键看效果注册表，按钮键看操作注册表——两者不能互相顶替。
        if (businessConfig is not null)
        {
            var manualRows = businessConfig.Actions
                .Where(action => action.Enabled && BusinessActionCatalog.IsManualEvent(action.EventCode))
                .ToList();
            var unknownActions = manualRows
                .Where(action => !documentActions.IsRegistered(action.EffectKey))
                .Select(action => $"SEQ={action.Seq} {action.EffectKey}")
                .ToList();
            checks.Add(unknownActions.Count == 0
                ? new("document_action_keys_registered", true,
                    manualRows.Count == 0 ? "模块无自定义按钮。" : $"自定义按钮键全部已登记（{manualRows.Count} 个）。")
                : new("document_action_keys_registered", false,
                    $"自定义按钮键未在操作注册表中登记，禁止发布：{string.Join("；", unknownActions)}。"));

            // 按钮授权镜子（WARN，不拒发布）：授权是 fail-closed 名单，"配了没人能用"是正常状态，
            // 但不能是无声的——否则没人知道按钮为什么在界面上消失。
            var registeredButtons = manualRows
                .Where(action => documentActions.IsRegistered(action.EffectKey))
                .Select(action => action.EffectKey.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (registeredButtons.Count > 0)
            {
                var counts = await documentActionAuthorization.CountsAsync(moduleId, registeredButtons, token);
                var unassigned = counts
                    .Where(pair => pair.Value.Users == 0 && pair.Value.Groups == 0)
                    .Select(pair => pair.Key)
                    .ToList();
                checks.Add(new("button_authorization_registered", true,
                    unassigned.Count == 0
                        ? $"自定义按钮均已授权：{string.Join("、", counts.Select(pair => $"{pair.Key}({pair.Value.Users} 用户/{pair.Value.Groups} 组)"))}。"
                        : $"以下自定义按钮尚无任何授权（发布后无人可点，属正常状态，授权后按钮才出现）：{string.Join("、", unassigned)}。",
                    "warning"));
            }
        }
        else
        {
            checks.Add(new("document_action_keys_registered", true, "模块无业务动作配置。"));
        }

        var engineEnabled = await ReadEffectEngineTagAsync(connection, moduleId, token);
        if (engineEnabled && businessConfig is not null)
        {
            var pendingKeys = businessConfig.Actions
                // 用户点击行不是效果链步骤，其键由操作注册表把关，见上一项校验。
                .Where(action => !BusinessActionCatalog.IsManualEvent(action.EventCode))
                .Select(action => action.EffectKey)
                .Where(effectKey => !EffectRegistry.IsImplemented(effectKey))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            checks.Add(pendingKeys.Count == 0
                ? new("effect_engine_keys_implemented", true, "效果引擎开启且动作键全部可执行。")
                : new("effect_engine_keys_implemented", false,
                    $"效果引擎开启但存在未实现效果键：{string.Join(",", pendingKeys.Take(8))}{(pendingKeys.Count > 8 ? " 等" : "")}。"));
        }
        else
        {
            checks.Add(new("effect_engine_keys_implemented", true,
                engineEnabled ? "效果引擎开启但模块暂无动作配置。" : "模块未开启效果引擎。"));
        }

        // 反向 kind 兼容矩阵：名义闭集（16 个取值）不等于每个效果键的**执行**闭集——
        // 例如 detail-field-sync 只认 restore-previous，配成 auto-reverse 会在真单据解批那一刻抛错。
        // 发布期就按矩阵拦下，而不是让配置者拿真单据去试。
        if (businessConfig is not null && businessConfig.Actions.Count > 0)
        {
            var unsupported = new List<string>();
            foreach (var action in businessConfig.Actions)
            {
                // 用户点击行没有反向语义（不参与效果链），其参数由操作注册表把关。
                if (BusinessActionCatalog.IsManualEvent(action.EventCode))
                {
                    continue;
                }
                var kind = ReadReverseKind(action.Reverse);
                if (kind is null)
                {
                    continue;
                }
                // 带**真**公式行的动作走公式解释器，其余走服务处理器——两者接受的 kind 不同。
                // 占位公式行（算子 / 目标表 / 目标字段全空）由配置迁移写入、运行时会被跳过
                // （见 EffectPlanLoader.ParseOp），因此不算公式行；把它当公式行会让服务型动作
                // 被按公式规则拒掉，报出"解批时会抛错"这种与实际运行不符的结论。
                var hasFormulaRows = ModuleBusinessConfigValidator.HasFormulaRows(action.Ops);
                if (!EffectReverseCompatibility.IsSupported(action.EffectKey, kind, hasFormulaRows))
                {
                    unsupported.Add($"SEQ={action.Seq} {action.EffectKey} → {kind}（可用：{string.Join("/", EffectReverseCompatibility.AllowedKinds(action.EffectKey, hasFormulaRows))}）");
                }
            }
            checks.Add(unsupported.Count == 0
                ? new("effect_reverse_kind_supported", true, "反向 kind 均落在各自效果键支持的取值内。")
                : new("effect_reverse_kind_supported", false,
                    $"反向 kind 不受该效果键支持（解批时会在真单据上抛错）：{string.Join("；", unsupported.Take(5))}{(unsupported.Count > 5 ? " 等" : "")}。"));
        }
        else
        {
            checks.Add(new("effect_reverse_kind_supported", true, "模块无效果动作配置。"));
        }

        var passed = checks.Where(check => check.Severity != "warning").All(check => check.Passed);
        string? definitionJson = null;
        if (passed && definition is not null)
        {
            // 快照基线：全权限定义；用户相关字段（UserId/ExecTag/CanDelete）归一为占位
            JsonElement? businessActions = null;
            JsonElement? validationRules = null;
            JsonElement? effectEngine = engineEnabled
                ? JsonSerializer.SerializeToElement(new { enabled = true })
                : null;
            if (businessConfig is not null)
            {
                businessActions = JsonSerializer.SerializeToElement(businessConfig.Actions);
                validationRules = JsonSerializer.SerializeToElement(businessConfig.ValidationRules);
            }
            definitionJson = JsonSerializer.Serialize(definition with
            {
                UserId = string.Empty,
                ExecTag = "Z",
                BusinessActions = businessActions,
                ValidationRules = validationRules,
                EffectEngine = effectEngine,
            });
        }

        logger.LogInformation("工作台定义校验 module={ModuleId} passed={Passed} checks={CheckCount}",
            moduleId, passed, checks.Count);
        return new(moduleId, module.Title, passed, $"module-{moduleId}-draft", checks, definitionJson);
    }

    private static async Task<bool> ReadEffectEngineTagAsync(
        SqlConnection connection,
        int moduleId,
        CancellationToken token)
    {
        const string sql = "SELECT ISNULL(EFFECT_ENGINE_TAG,0) FROM dbo.MODULES WITH (NOLOCK) WHERE M_IDX=@ModuleId;";
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@ModuleId", SqlDbType.Int).Value = moduleId;
        var value = await command.ExecuteScalarAsync(token);
        return value is not null && Convert.ToInt32(value) == 1;
    }

    /// <summary>读反向结构的 kind（未配反向结构或格式非法时返回 null——后者由保存期结构校验负责）。</summary>
    private static string? ReadReverseKind(string? reverseJson)
    {
        if (string.IsNullOrWhiteSpace(reverseJson))
        {
            return null;
        }
        try
        {
            using var document = JsonDocument.Parse(reverseJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("kind", out var kind)
                || kind.ValueKind != JsonValueKind.String)
            {
                return null;
            }
            return kind.GetString();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static async Task<ModuleRow?> ReadModuleAsync(SqlConnection connection, int moduleId, CancellationToken token)
    {
        const string sql = """
            SELECT LTRIM(RTRIM(M_DESC)),LTRIM(RTRIM(ISNULL(MASTER_TABLE,''))),LTRIM(RTRIM(ISNULL(DETAIL_TABLE,''))),
                   LTRIM(RTRIM(ISNULL(M_URL,''))),
                   LTRIM(RTRIM(ISNULL(FILTER,'')))
            FROM dbo.MODULES WITH (NOLOCK) WHERE M_IDX=@ModuleId;
            """;
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@ModuleId", SqlDbType.Int).Value = moduleId;
        await using var reader = await command.ExecuteReaderAsync(token);
        if (!await reader.ReadAsync(token))
        {
            return null;
        }

        var title = reader.GetString(0);
        var master = reader.GetString(1);
        var detail = reader.GetString(2);
        var url = reader.GetString(3);
        var filter = reader.GetString(4);
        await reader.CloseAsync();
        // 分组表达式在 MODULE_GROUPS（保存即生效、不进快照），发布校验时按当前配置读一遍
        var expressions = await ReadGroupExpressionsAsync(connection, moduleId, token);
        return new ModuleRow(
            moduleId,
            title,
            master,
            string.IsNullOrWhiteSpace(detail) ? null : detail,
            url,
            filter,
            expressions);
    }

    /// <summary>模块的分组表达式（GROUP_ID → GROUP_EXP），空表达式跳过。</summary>
    private static async Task<IReadOnlyList<(int GroupId, string Expression)>> ReadGroupExpressionsAsync(
        SqlConnection connection, int moduleId, CancellationToken token)
    {
        const string sql = """
            SELECT GROUP_ID,GROUP_EXP FROM dbo.MODULE_GROUPS WITH (NOLOCK)
            WHERE M_IDX=@ModuleId ORDER BY SORT_IDX,GROUP_ID;
            """;
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@ModuleId", SqlDbType.Int).Value = moduleId;
        await using var reader = await command.ExecuteReaderAsync(token);
        var result = new List<(int GroupId, string Expression)>();
        while (await reader.ReadAsync(token))
        {
            var expression = reader.IsDBNull(1) ? string.Empty : reader.GetString(1).Trim();
            if (expression.Length == 0) continue;
            result.Add((reader.GetInt32(0), expression));
        }
        return result;
    }


    private static async Task<IReadOnlySet<string>> GetTableColumnsAsync(SqlConnection connection, string table, CancellationToken token)
    {
        const string sql = """
            SELECT c.name FROM sys.columns c
            JOIN sys.objects o ON c.object_id=o.object_id AND o.type IN ('U','V')
            JOIN sys.schemas s ON o.schema_id=s.schema_id
            WHERE s.name=N'dbo' AND o.name=@Table;
            """;
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@Table", SqlDbType.NVarChar, 100).Value = table;
        await using var reader = await command.ExecuteReaderAsync(token);
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (await reader.ReadAsync(token))
        {
            result.Add(reader.GetString(0));
        }
        return result;
    }

    /// <summary>
    /// 模块 <c>FILTER</c> 的受控校验，**发布与菜单保存共用同一口径**（白名单 + 解析器同源）。
    /// 发布一路不必等到发布才发现坏值：<c>FILTER</c> 同时被报表与选择器**实时**读取（不经快照），
    /// 所以保存时就要拦住；空值表示"无行级限制"，恒通过。
    /// </summary>
    internal static async Task<bool> TryValidateModuleFilterAsync(
        SqlConnection connection, SqlTransaction? transaction, string masterTable, string? filter, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(filter)) return true;
        if (string.IsNullOrWhiteSpace(masterTable)) return false;
        var allowedFields = await ReadFilterFieldKeysAsync(connection, transaction, masterTable, token);
        return DataFilterParser.TryParse(filter, masterTable, allowedFields, out _, out _);
    }

    /// <summary>
    /// FILTER/分组白名单（系统口径）：主表物理存在、非虚拟字段（含隐藏字段，不受用户列选择影响）。
    /// 实现在 <see cref="ModuleFieldWhitelist"/>，与分组配置写侧、分组读侧同源。
    /// <paramref name="transaction"/> 为调用方连接上已开的本地事务（没有就传 null）：
    /// 菜单保存走事务内校验，命令必须带上事务，否则驱动直接拒绝执行。
    /// </summary>
    internal static Task<IReadOnlySet<string>> ReadFilterFieldKeysAsync(
        SqlConnection connection, SqlTransaction? transaction, string table, CancellationToken token)
        => ModuleFieldWhitelist.ReadAsync(connection, transaction, table, null, token);

    private static async Task<IReadOnlyList<WorkbenchField>> ReadVirtualFieldsAsync(SqlConnection connection, string table, CancellationToken token)
    {
        const string sql = """
            SELECT LTRIM(RTRIM(f.F_ID)),LTRIM(RTRIM(ISNULL(f.VIRTUAL_EXP,'')))
            FROM dbo.FIELDS f WITH (NOLOCK)
            WHERE f.T_ID=@Table AND COALESCE(f.IS_VIRTUAL,0)=1
              AND LTRIM(RTRIM(ISNULL(f.VIRTUAL_EXP,'')))<>'';
            """;
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@Table", SqlDbType.NVarChar, 100).Value = table;
        await using var reader = await command.ExecuteReaderAsync(token);
        var result = new List<WorkbenchField>();
        while (await reader.ReadAsync(token))
        {
            var key = reader.GetString(0).Trim();
            if (!WorkbenchSql.Identifier.IsMatch(key))
            {
                continue;
            }
            result.Add(new WorkbenchField(key, key, "nvarchar", 100, null, false,
                IsVirtual: true, VirtualExpression: reader.GetString(1)));
        }
        return result;
    }

    private static async Task<IReadOnlyList<(string Field, string Function)>> ReadConvertFunctionsAsync(
        SqlConnection connection, string table, CancellationToken token)
    {
        const string sql = """
            SELECT LTRIM(RTRIM(f.F_ID)),LTRIM(RTRIM(ISNULL(f.CONVERT_FUNCTION,'')))
            FROM dbo.FIELDS f WITH (NOLOCK)
            WHERE f.T_ID=@Table AND LTRIM(RTRIM(ISNULL(f.CONVERT_FUNCTION,'')))<>'';
            """;
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@Table", SqlDbType.NVarChar, 100).Value = table;
        await using var reader = await command.ExecuteReaderAsync(token);
        var result = new List<(string, string)>();
        while (await reader.ReadAsync(token))
        {
            result.Add((reader.GetString(0).Trim(), reader.GetString(1).Trim()));
        }
        return result;
    }

    /// <summary>
    /// 界面质量类校验：
    /// 阻断——FORM_OPTIONS 格式非法 / CHOOSE_RETURNVAL 映射目标不存在 / DFT_VALUE 按 F_TYPE 不可转换
    /// （GETDATE() 类无参函数表达式跳过）；警告——F_DESC 超长
    /// （渲染层「换两行+title」为硬保障，此处仅质量提示）、datetime 缺 DISPLAY_FORMAT。
    /// CHOOSE_RETURNVAL 目标按「主表∪子表模块字段全集」判定——旧语义允许子表选择器跨表回填主表字段。
    /// </summary>
    private async Task<IReadOnlyList<WorkbenchDefinitionValidationCheck>> ValidateFormQualityAsync(
        SqlConnection connection, IReadOnlyList<string> tables, CancellationToken token)
    {
        var checks = new List<WorkbenchDefinitionValidationCheck>();
        var fieldsByTable = new Dictionary<string, IReadOnlyList<FormQualityField>>(StringComparer.OrdinalIgnoreCase);
        foreach (var table in tables)
        {
            var fields = await ReadFormQualityFieldsAsync(connection, table, token);
            if (fields.Count > 0)
            {
                fieldsByTable[table] = fields;
            }
        }
        if (fieldsByTable.Count == 0)
        {
            return checks;
        }

        // 模块字段全集（主表 ∪ 子表）
        var moduleKeys = fieldsByTable.Values
            .SelectMany(list => list)
            .Select(field => field.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // RETURN_ITEMS 映射目标存在（阻断；模块级聚合一条，消息截断前 8 条防超长）
        var returnvalErrors = new List<string>();
        foreach (var (table, fields) in fieldsByTable)
        {
            foreach (var field in fields)
            {
                foreach (var mapping in field.ReturnItems)
                {
                    var target = FormFieldSelector.NormalizeChooserTarget(mapping.Target);
                    if (!moduleKeys.Contains(target))
                    {
                        returnvalErrors.Add($"{table}.{field.Key}(目标 {target})");
                    }
                }
            }
        }
        checks.Add(new("chooser_returnval_targets", returnvalErrors.Count == 0,
            returnvalErrors.Count == 0
                ? "RETURN_ITEMS 映射目标全部存在于模块字段集。"
                : $"存在跨模块死映射 {returnvalErrors.Count} 处（运行时未命中即跳过，不影响发布）：{string.Join(",", returnvalErrors.Take(8))}{(returnvalErrors.Count > 8 ? " 等" : "")}。",
            "warning"));

        // Chooser data source integrity (blocking): source table exists, FILTER_STRUCT compiles, return columns exist
        var chooserErrors = new List<string>();
        var chooserWarnings = new List<string>();
        foreach (var table in tables)
        {
            foreach (var source in await ReadChooserSourcesAsync(connection, table, token))
            {
                if (!await WorkbenchSql.TableExistsAsync(connection, source.SourceTable, token))
                {
                    chooserErrors.Add($"{table}.{source.Field}#{source.SerialNo} 来源表 {source.SourceTable} 不存在");
                    continue;
                }
                if (string.IsNullOrWhiteSpace(source.FilterStruct))
                {
                    chooserWarnings.Add($"{table}.{source.Field}#{source.SerialNo} 过滤条件待重建（FILTER_STRUCT 为空，运行期 fail-closed 空选项）");
                    continue;
                }
                if (!ChooserFilterStruct.TryParse(source.FilterStruct, out var filterStruct) || filterStruct is null)
                {
                    chooserErrors.Add($"{table}.{source.Field}#{source.SerialNo} FILTER_STRUCT 不是合法结构化 JSON");
                    continue;
                }
                var filterValidation = await ChooserFilterValidator.ValidateAsync(
                    connection, filterStruct, source.SourceTable, token);
                if (!filterValidation.Ok)
                {
                    chooserErrors.Add($"{table}.{source.Field}#{source.SerialNo} 过滤条件校验失败：{string.Join("；", filterValidation.Messages.Take(2))}");
                }
                if (!string.IsNullOrWhiteSpace(source.ReturnItems))
                {
                    var returnItems = ChooserReturnItems.Parse(source.ReturnItems);
                    if (returnItems is null)
                    {
                        chooserErrors.Add($"{table}.{source.Field}#{source.SerialNo} RETURN_ITEMS 不是合法 JSON 数组");
                    }
                    else
                    {
                        var columns = returnItems.Select(item => item.Column.Trim())
                            .Where(column => WorkbenchSql.Identifier.IsMatch(column))
                            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                        // 来源列允许物理列或来源表内受控虚拟列（与保存校验同口径）
                        if (columns.Length > 0
                            && !await WorkbenchSql.ReturnColumnsExistAsync(connection, source.SourceTable, columns, token))
                        {
                            chooserErrors.Add($"{table}.{source.Field}#{source.SerialNo} 回填映射引用了来源表既非物理列也非受控虚拟列的来源字段");
                        }
                    }
                }
            }
        }
        checks.Add(new("chooser_sources", chooserErrors.Count == 0,
            chooserErrors.Count == 0
                ? "选择器数据源完整性校验通过。"
                : $"选择器数据源损坏 {chooserErrors.Count} 处：{string.Join("；", chooserErrors.Take(8))}{(chooserErrors.Count > 8 ? " 等" : "")}。",
            "error"));
        if (chooserWarnings.Count > 0)
        {
            checks.Add(new("chooser_sources_pending", true,
                $"选择器过滤条件待重建 {chooserWarnings.Count} 处：{string.Join("；", chooserWarnings.Take(5))}{(chooserWarnings.Count > 5 ? " 等" : "")}。",
                "warning"));
        }

        foreach (var (table, fields) in fieldsByTable)
        {
            // FORM_OPTIONS 格式（阻断）：k=v;k=v，键不得重复
            var optionsErrors = new List<string>();
            foreach (var field in fields)
            {
                if (string.IsNullOrWhiteSpace(field.Options)) continue;
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var part in field.Options.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    var eq = part.IndexOf('=');
                    var optionKey = eq <= 0 ? string.Empty : part[..eq].Trim();
                    var label = eq <= 0 ? string.Empty : part[(eq + 1)..].Trim();
                    if (optionKey.Length == 0 || label.Length == 0)
                    {
                        optionsErrors.Add($"{table}.{field.Key}(FORM_OPTIONS='{field.Options}')");
                        break;
                    }
                    if (!seen.Add(optionKey))
                    {
                        optionsErrors.Add($"{table}.{field.Key}(重复键 {optionKey})");
                        break;
                    }
                }
            }
            checks.Add(new("form_options_format", optionsErrors.Count == 0,
                optionsErrors.Count == 0 ? $"{table} FORM_OPTIONS 格式合法。" : $"FORM_OPTIONS 非法：{string.Join(",", optionsErrors)}。"));

            // DFT_VALUE 可转换（阻断；GETDATE() 类无参函数调用）
            var dftErrors = new List<string>();
            foreach (var field in fields)
            {
                if (string.IsNullOrWhiteSpace(field.DefaultValue)) continue;
                if (Regex.IsMatch(field.DefaultValue, @"^[A-Za-z_]\w*\(\s*\)$")) continue;
                if (field.FType.ToLowerInvariant().Contains("date") && field.DefaultValue.Trim().Equals("D", StringComparison.OrdinalIgnoreCase)) continue;
                if (!RecordPayloadValidator.TryConvert(field.FType, field.DefaultValue, out _))
                {
                    dftErrors.Add($"{table}.{field.Key}(DFT_VALUE='{field.DefaultValue}' F_TYPE={field.FType})");
                }
            }
            checks.Add(new("dft_value_convertible", dftErrors.Count == 0,
                dftErrors.Count == 0 ? $"{table} DFT_VALUE 均可按 F_TYPE 转换。" : $"DFT_VALUE 不可转换：{string.Join(",", dftErrors)}。"));

            var visiblePhysical = fields.Where(field => field.IsVisible && !field.IsVirtual).ToList();

            // F_DESC 超长（警告）
            var longLabels = visiblePhysical.Where(field => field.LabelLength > 20)
                .Take(5).Select(field => $"{field.Key}:{field.LabelLength}字符").ToList();
            if (longLabels.Count > 0)
            {
                checks.Add(new("f_desc_length", true,
                    $"{table} 存在超长标签（>20 字符，渲染层两行+title 兜底）：{string.Join(",", longLabels)}。", "warning"));
            }

            // datetime 建议 DISPLAY_FORMAT（警告）
            var bareDatetime = visiblePhysical
                .Where(field => !field.IsReadonly && field.FType.ToLowerInvariant().Contains("datetime"))
                .Where(field => string.IsNullOrWhiteSpace(field.DisplayFormat))
                .Take(5).Select(field => field.Key).ToList();
            if (bareDatetime.Count > 0)
            {
                checks.Add(new("datetime_display_format_hint", true,
                    $"{table} datetime 字段缺 DISPLAY_FORMAT（列表侧时间分量可能不显示）：{string.Join(",", bareDatetime)}。", "warning"));
            }
        }
        return checks;
    }

    private sealed record FormQualityField(
        string Key, string Label, string FType, bool IsVisible, bool IsReadonly, bool IsVirtual,
        string? Options, string? DefaultValue, IReadOnlyList<ChooserReturnItem> ReturnItems, string? DisplayFormat)
    {
        public int LabelLength => Label.Length;
    }

    private sealed record ChooserSourceRow(string Field, int SerialNo, string SourceTable, string? FilterStruct, string? ReturnItems);

    private static async Task<IReadOnlyList<FormQualityField>> ReadFormQualityFieldsAsync(
        SqlConnection connection, string table, CancellationToken token)
    {
        const string sql = """
            SELECT LTRIM(RTRIM(f.F_ID)),LTRIM(RTRIM(f.F_DESC)),COALESCE(LTRIM(RTRIM(f.F_TYPE)),N'nvarchar'),
                   CAST(COALESCE(f.IS_VISIBLE,1) AS bit),CAST(COALESCE(f.IS_READONLY,0) AS bit),CAST(COALESCE(f.IS_VIRTUAL,0) AS bit),
                   f.FORM_OPTIONS,f.DFT_VALUE,
                   f.DISPLAY_FORMAT
            FROM dbo.FIELDS f WITH (NOLOCK) WHERE f.T_ID=@Table;
            """;
        var returnItemsByField = await ReadReturnItemsByFieldAsync(connection, table, token);
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@Table", SqlDbType.NVarChar, 100).Value = table;
        await using var reader = await command.ExecuteReaderAsync(token);
        var result = new List<FormQualityField>();
        while (await reader.ReadAsync(token))
        {
            result.Add(new FormQualityField(
                reader.GetString(0), reader.GetString(1), reader.GetString(2),
                reader.GetBoolean(3), reader.GetBoolean(4), reader.GetBoolean(5),
                reader.IsDBNull(6) ? null : reader.GetString(6).Trim(),
                reader.IsDBNull(7) ? null : reader.GetString(7).Trim(),
                returnItemsByField.TryGetValue(reader.GetString(0).Trim(), out var items) ? items : [],
                reader.IsDBNull(8) ? null : reader.GetString(8).Trim()));
        }
        return result;
    }

    /// <summary>读取表内全部字段的回填映射（FIELD_DATASOURCE.RETURN_ITEMS，按字段聚合）。</summary>
    private static async Task<Dictionary<string, IReadOnlyList<ChooserReturnItem>>> ReadReturnItemsByFieldAsync(
        SqlConnection connection, string table, CancellationToken token)
    {
        const string sql = """
            SELECT LTRIM(RTRIM(c.F_ID)),c.RETURN_ITEMS
            FROM dbo.FIELD_DATASOURCE c WITH (NOLOCK)
            WHERE c.T_ID=@Table AND CAST(COALESCE(c.ACTIVE_TAG,0) AS bit)=1
              AND LTRIM(RTRIM(ISNULL(c.RETURN_ITEMS,'')))<>'';
            """;
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@Table", SqlDbType.NVarChar, 100).Value = table;
        await using var reader = await command.ExecuteReaderAsync(token);
        var result = new Dictionary<string, IReadOnlyList<ChooserReturnItem>>(StringComparer.OrdinalIgnoreCase);
        while (await reader.ReadAsync(token))
        {
            var field = reader.GetString(0).Trim();
            var items = ChooserReturnItems.Parse(reader.GetString(1)) ?? [];
            if (!result.TryGetValue(field, out var existing))
            {
                result[field] = items;
            }
            else
            {
                result[field] = existing.Concat(items).ToList();
            }
        }
        return result;
    }

    /// <summary>读取表内启用数据源（FIELD_DATASOURCE； 完整性校验数据源）。</summary>
    private static async Task<IReadOnlyList<ChooserSourceRow>> ReadChooserSourcesAsync(
        SqlConnection connection, string table, CancellationToken token)
    {
        const string sql = """
            SELECT LTRIM(RTRIM(c.F_ID)),c.SERIAL_NO,LTRIM(RTRIM(ISNULL(c.SOURCE_T_ID,''))),
                   c.FILTER_STRUCT,c.RETURN_ITEMS
            FROM dbo.FIELD_DATASOURCE c WITH (NOLOCK)
            WHERE c.T_ID=@Table AND CAST(COALESCE(c.ACTIVE_TAG,0) AS bit)=1
            ORDER BY c.SERIAL_NO;
            """;
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@Table", SqlDbType.NVarChar, 100).Value = table;
        await using var reader = await command.ExecuteReaderAsync(token);
        var result = new List<ChooserSourceRow>();
        while (await reader.ReadAsync(token))
        {
            var sourceTable = reader.GetString(2).Trim();
            if (sourceTable.Length == 0) continue;
            result.Add(new ChooserSourceRow(
                reader.GetString(0).Trim(),
                reader.GetInt32(1),
                sourceTable,
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4)));
        }
        return result;
    }

    private sealed record ModuleRow(
        int ModuleId,
        string Title,
        string MasterTable,
        string? DetailTable,
        string MUrl,
        string Filter,
        IReadOnlyList<(int GroupId, string Expression)> GroupExpressions);
}
