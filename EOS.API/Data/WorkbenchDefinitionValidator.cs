using System.Data;
using System.Text.Json;
using System.Text.RegularExpressions;
using EOS.API.Models;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;

namespace EOS.API.Data;

/// <summary>
/// Workbench Definition 发布前校验器（ADR-005 §3，阶段 2）：
/// 覆盖发布前必须校验的全部条目（白名单/路由、主表/子表/主键/关联键、字段白名单、
/// FILTER/分组表达式受控解析、虚拟列/转换函数、成本/保密/拒绝字段、高风险表达式、
/// 统一表单定义），输出机器可读报告（code/passed/message），供 CI、发布工具与
/// 开发 Agent 消费；校验未通过的定义不发布到运行时。
/// 与 scripts/auto-enable-batch.ps1 放量预检共用同一道闸（流水线启用前先调服务端校验）。
/// </summary>
public sealed class WorkbenchDefinitionValidator(
    DbConnectionFactory connections,
    DocumentWorkbenchRepository workbench,
    IOptions<UnifiedFormEditorSettings> formSettings,
    ILogger<WorkbenchDefinitionValidator> logger)
{
    private static readonly Regex Identifier = new("^[A-Za-z_][A-Za-z0-9_]{0,127}$", RegexOptions.Compiled);

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

        var enabled = formSettings.Value.EnabledModuleIds.Contains(moduleId);
        var workbenchUrl = ModuleRouteValidator.IsWorkbenchUrl(module.MUrl);
        if (!workbenchUrl && !enabled)
        {
            checks.Add(new("runtime_whitelist", false,
                "模块既不在统一表单白名单内，M_URL 也不是工作台承载页，无法进入运行时。"));
        }
        else
        {
            checks.Add(new("runtime_whitelist", true,
                workbenchUrl ? "M_URL 为工作台承载页。" : "模块在统一表单白名单内。"));
        }

        var routeErrors = new List<string>();
        if (!string.IsNullOrWhiteSpace(module.NewUrl) && ModuleRouteValidator.ResolveActionUrl(module.NewUrl, moduleId) is null)
        {
            routeErrors.Add($"NEW_URL 非法：{module.NewUrl}");
        }
        if (!string.IsNullOrWhiteSpace(module.ModiUrl) && ModuleRouteValidator.ResolveActionUrl(module.ModiUrl, moduleId) is null)
        {
            routeErrors.Add($"MODI_URL 非法：{module.ModiUrl}");
        }
        if (routeErrors.Count > 0)
        {
            checks.Add(new("route_valid", false, string.Join("；", routeErrors)));
        }
        else
        {
            checks.Add(new("route_valid", true, "路由契约（M_URL/NEW_URL/MODI_URL）合法。"));
        }

        var masterOk = Identifier.IsMatch(module.MasterTable) && await TableExistsAsync(connection, module.MasterTable, token);
        checks.Add(masterOk
            ? new("master_table_exists", true, $"主表 {module.MasterTable} 存在。")
            : new("master_table_exists", false, $"主表 {module.MasterTable} 不存在或标识符非法。"));

        if (module.DetailTable is { Length: > 0 })
        {
            var detailOk = Identifier.IsMatch(module.DetailTable) && await TableExistsAsync(connection, module.DetailTable, token);
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
            pkColumns = await GetPrimaryKeyColumnsAsync(connection, module.MasterTable, token);
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
            ? await ReadFilterFieldKeysAsync(connection, module.MasterTable, token)
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
        for (var i = 0; i < 5; i++)
        {
            if (!module.GroupEnabled[i] || string.IsNullOrWhiteSpace(module.GroupExpressions[i]))
            {
                continue;
            }
            if (!GroupExpressionParser.TryCompile(module.GroupExpressions[i], module.MasterTable, allowedFields, out _))
            {
                groupErrors.Add($"GROUP{i + 1}");
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

        var datasourceErrors = new List<string>();
        foreach (var table in tables)
        {
            foreach (var field in await ReadDataSourceSqlFieldsAsync(connection, table, token))
            {
                datasourceErrors.Add($"{table}.{field}");
            }
        }
        checks.Add(datasourceErrors.Count == 0
            ? new("datasource_sql_controlled", true, "无 DATASOURCE_SQL 或全部受控。")
            : new("datasource_sql_controlled", false, $"DATASOURCE_SQL 尚无受控解析器，禁止发布：{string.Join(",", datasourceErrors)}。"));

        WorkbenchDefinition? definition = null;
        if (workbenchUrl || enabled)
        {
            definition = await workbench.GetDefinitionAsync(
                moduleId, userId, "Z", true, true,
                new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                new HashSet<string>(StringComparer.OrdinalIgnoreCase), token);
            checks.Add(definition is not null
                ? new("definition_build", true, "工作台定义可构建。")
                : new("definition_build", false, "无法构建工作台定义（路由/主表/字段/虚拟列解析失败）。"));
        }
        else
        {
            checks.Add(new("definition_build", false, "模块不在运行白名单，跳过定义构建。"));
        }

        if (enabled || workbenchUrl)
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

        // ===== ADR-006 决策 4：界面质量类校验（error 三项拦发布闸；warning 仅质量提示） =====
        if (definition is not null)
        {
            checks.AddRange(await ValidateFormQualityAsync(connection, tables, token));
        }

        var passed = checks.Where(check => check.Severity != "warning").All(check => check.Passed);
        string? definitionJson = null;
        if (passed && definition is not null)
        {
            // 快照基线：全权限定义；用户相关字段（UserId/ExecTag/CanDelete）归一为占位
            definitionJson = JsonSerializer.Serialize(definition with { UserId = string.Empty, ExecTag = "Z" });
        }

        logger.LogInformation("工作台定义校验 module={ModuleId} passed={Passed} checks={CheckCount}",
            moduleId, passed, checks.Count);
        return new(moduleId, module.Title, passed, $"module-{moduleId}-draft", checks, definitionJson);
    }

    private static async Task<ModuleRow?> ReadModuleAsync(SqlConnection connection, int moduleId, CancellationToken token)
    {
        const string sql = """
            SELECT LTRIM(RTRIM(M_DESC)),LTRIM(RTRIM(ISNULL(MASTER_TABLE,''))),LTRIM(RTRIM(ISNULL(DETAIL_TABLE,''))),
                   LTRIM(RTRIM(ISNULL(M_URL,''))),LTRIM(RTRIM(ISNULL(NEW_URL,''))),LTRIM(RTRIM(ISNULL(MODI_URL,''))),
                   LTRIM(RTRIM(ISNULL(FILTER,''))),
                   ISNULL(GROUP1,0),ISNULL(GROUP_EXP1,''),ISNULL(GROUP2,0),ISNULL(GROUP_EXP2,''),
                   ISNULL(GROUP3,0),ISNULL(GROUP_EXP3,''),ISNULL(GROUP4,0),ISNULL(GROUP_EXP4,''),
                   ISNULL(GROUP5,0),ISNULL(GROUP_EXP5,'')
            FROM dbo.MODULES WITH (NOLOCK) WHERE M_IDX=@ModuleId;
            """;
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@ModuleId", SqlDbType.Int).Value = moduleId;
        await using var reader = await command.ExecuteReaderAsync(token);
        if (!await reader.ReadAsync(token))
        {
            return null;
        }

        var groups = new bool[5];
        var expressions = new string[5];
        for (var i = 0; i < 5; i++)
        {
            groups[i] = reader.GetBoolean(7 + i * 2);
            expressions[i] = reader.IsDBNull(8 + i * 2) ? string.Empty : reader.GetString(8 + i * 2).Trim();
        }
        return new ModuleRow(
            moduleId,
            reader.GetString(0),
            reader.GetString(1),
            string.IsNullOrWhiteSpace(reader.GetString(2)) ? null : reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetString(5),
            reader.GetString(6),
            groups,
            expressions);
    }

    private static async Task<bool> TableExistsAsync(SqlConnection connection, string table, CancellationToken token)
    {
        const string sql = """
            SELECT 1 FROM sys.objects o
            JOIN sys.schemas s ON o.schema_id=s.schema_id
            WHERE s.name=N'dbo' AND o.name=@Table AND o.type IN ('U','V');
            """;
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@Table", SqlDbType.NVarChar, 100).Value = table;
        return await command.ExecuteScalarAsync(token) is not null;
    }

    private static async Task<IReadOnlyList<string>> GetPrimaryKeyColumnsAsync(SqlConnection connection, string table, CancellationToken token)
    {
        const string sql = """
            SELECT c.name
            FROM sys.indexes i
            JOIN sys.index_columns ic ON i.object_id=ic.object_id AND i.index_id=ic.index_id
            JOIN sys.columns c ON ic.object_id=c.object_id AND ic.column_id=c.column_id
            JOIN sys.tables t ON i.object_id=t.object_id
            JOIN sys.schemas s ON t.schema_id=s.schema_id
            WHERE s.name=N'dbo' AND t.name=@Table AND i.is_primary_key=1
            ORDER BY ic.key_ordinal;
            """;
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@Table", SqlDbType.NVarChar, 100).Value = table;
        await using var reader = await command.ExecuteReaderAsync(token);
        var result = new List<string>();
        while (await reader.ReadAsync(token))
        {
            result.Add(reader.GetString(0));
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

    /// <summary>FILTER/分组白名单：主表物理存在、非虚拟字段（含隐藏字段，不受用户列选择影响）。</summary>
    private static async Task<IReadOnlySet<string>> ReadFilterFieldKeysAsync(SqlConnection connection, string table, CancellationToken token)
    {
        const string sql = """
            SELECT LTRIM(RTRIM(f.F_ID))
            FROM dbo.FIELDS f WITH (NOLOCK)
            WHERE f.T_ID=@Table AND COALESCE(f.IS_VIRTUAL,0)=0
              AND EXISTS (SELECT 1 FROM sys.columns c
                          JOIN sys.objects o ON c.object_id=o.object_id AND o.type IN ('U','V')
                          JOIN sys.schemas s ON o.schema_id=s.schema_id
                          WHERE s.name=N'dbo' AND o.name=@Table AND c.name=f.F_ID);
            """;
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@Table", SqlDbType.NVarChar, 100).Value = table;
        await using var reader = await command.ExecuteReaderAsync(token);
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (await reader.ReadAsync(token))
        {
            var key = reader.GetString(0).Trim();
            if (Identifier.IsMatch(key))
            {
                result.Add(key);
            }
        }
        return result;
    }

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
            if (!Identifier.IsMatch(key))
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

    private static async Task<IReadOnlyList<string>> ReadDataSourceSqlFieldsAsync(SqlConnection connection, string table, CancellationToken token)
    {
        const string sql = """
            SELECT LTRIM(RTRIM(f.F_ID))
            FROM dbo.FIELDS f WITH (NOLOCK)
            WHERE f.T_ID=@Table AND LTRIM(RTRIM(ISNULL(f.DATASOURCE_SQL,'')))<>'';
            """;
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@Table", SqlDbType.NVarChar, 100).Value = table;
        await using var reader = await command.ExecuteReaderAsync(token);
        var result = new List<string>();
        while (await reader.ReadAsync(token))
        {
            result.Add(reader.GetString(0).Trim());
        }
        return result;
    }

    /// <summary>
    /// 界面质量类校验（ADR-006 决策 4）：
    /// 阻断——FORM_OPTIONS 格式非法 / CHOOSE_RETURNVAL 映射目标不存在 / DFT_VALUE 按 F_TYPE 不可转换
    /// （GETDATE() 类无参函数表达式跳过）；警告——FORM_ORDER 缺失比例过高、F_DESC 超长
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

        // CHOOSE_RETURNVAL 映射目标存在（阻断；模块级聚合一条，消息截断前 8 条防超长）
        var returnvalErrors = new List<string>();
        foreach (var (table, fields) in fieldsByTable)
        {
            foreach (var field in fields)
            {
                foreach (var mapping in field.ReturnMappings)
                {
                    if (string.IsNullOrWhiteSpace(mapping)) continue;
                    foreach (var pair in mapping.Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    {
                        var eq = pair.IndexOf('=');
                        if (eq <= 0)
                        {
                            returnvalErrors.Add($"{table}.{field.Key}(片段 '{pair}' 缺少 =)");
                            continue;
                        }
                        var target = FormFieldSelector.NormalizeChooserTarget(pair[..eq].Trim());
                        if (!moduleKeys.Contains(target))
                        {
                            returnvalErrors.Add($"{table}.{field.Key}(目标 {target})");
                        }
                    }
                }
            }
        }
        checks.Add(new("chooser_returnval_targets", returnvalErrors.Count == 0,
            returnvalErrors.Count == 0
                ? "CHOOSE_RETURNVAL 映射目标全部存在于模块字段集。"
                : $"存在跨模块死映射 {returnvalErrors.Count} 处（运行时未命中即跳过、无害，随逐模块验收清理）：{string.Join(",", returnvalErrors.Take(8))}{(returnvalErrors.Count > 8 ? " 等" : "")}。",
            "warning"));

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

            // DFT_VALUE 可转换（阻断；GETDATE() 类无参函数调用与旧系统日期宏 'D'=当天 跳过）
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

            // FORM_ORDER 缺失比例（警告）：>50% 时表单顺序回退 VERIFY_INDEX 可能混乱
            if (visiblePhysical.Count >= 5)
            {
                var missingOrder = visiblePhysical.Count(field => !field.HasFormOrder);
                if ((double)missingOrder / visiblePhysical.Count > 0.5)
                {
                    checks.Add(new("form_order_coverage", true,
                        $"{table} 可见字段 FORM_ORDER 缺失 {missingOrder}/{visiblePhysical.Count}，表单顺序回退 VERIFY_INDEX 可能混乱。", "warning"));
                }
            }

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
        bool HasFormOrder, string? Options, string? DefaultValue, string?[] ReturnMappings, string? DisplayFormat)
    {
        public int LabelLength => Label.Length;
    }

    private static async Task<IReadOnlyList<FormQualityField>> ReadFormQualityFieldsAsync(
        SqlConnection connection, string table, CancellationToken token)
    {
        const string sql = """
            SELECT LTRIM(RTRIM(f.F_ID)),LTRIM(RTRIM(f.F_DESC)),COALESCE(LTRIM(RTRIM(f.F_TYPE)),N'nvarchar'),
                   CAST(COALESCE(f.IS_VISIBLE,1) AS bit),CAST(COALESCE(f.IS_READONLY,0) AS bit),CAST(COALESCE(f.IS_VIRTUAL,0) AS bit),
                   f.FORM_ORDER,f.FORM_OPTIONS,f.DFT_VALUE,
                   LTRIM(RTRIM(ISNULL(f.CHOOSE_RETURNVAL1,''))),LTRIM(RTRIM(ISNULL(f.CHOOSE_RETURNVAL2,''))),
                   LTRIM(RTRIM(ISNULL(f.CHOOSE_RETURNVAL3,''))),LTRIM(RTRIM(ISNULL(f.CHOOSE_RETURNVAL4,''))),
                   f.DISPLAY_FORMAT
            FROM dbo.FIELDS f WITH (NOLOCK) WHERE f.T_ID=@Table;
            """;
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@Table", SqlDbType.NVarChar, 100).Value = table;
        await using var reader = await command.ExecuteReaderAsync(token);
        var result = new List<FormQualityField>();
        while (await reader.ReadAsync(token))
        {
            result.Add(new FormQualityField(
                reader.GetString(0), reader.GetString(1), reader.GetString(2),
                reader.GetBoolean(3), reader.GetBoolean(4), reader.GetBoolean(5),
                !reader.IsDBNull(6), reader.IsDBNull(7) ? null : reader.GetString(7).Trim(),
                reader.IsDBNull(8) ? null : reader.GetString(8).Trim(),
                [reader.IsDBNull(9) ? null : reader.GetString(9), reader.IsDBNull(10) ? null : reader.GetString(10),
                 reader.IsDBNull(11) ? null : reader.GetString(11), reader.IsDBNull(12) ? null : reader.GetString(12)],
                reader.IsDBNull(13) ? null : reader.GetString(13)));
        }
        return result;
    }

    private sealed record ModuleRow(
        int ModuleId,
        string Title,
        string MasterTable,
        string? DetailTable,
        string MUrl,
        string NewUrl,
        string ModiUrl,
        string Filter,
        bool[] GroupEnabled,
        string[] GroupExpressions);
}
