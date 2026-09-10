using System.Data;
using System.Text.Json;
using EOS.API.Models;
using Microsoft.Data.SqlClient;

namespace EOS.API.Data;

/// <summary>
/// 模块业务动作/校验配置工作区读写（2301 可视化配置的数据通道）。
/// 保存 = 整模块替换 + 事务：结构校验（ModuleBusinessConfigValidator）→
/// 模块形态/物理列校验 → 删旧插新 → 标脏 → 审计。
/// 定位键/条件深校验待单据关系效果侧登记（FIELD_RELATION 扩展）落地后补。
/// </summary>
public sealed class ModuleBusinessConfigRepository(
    DbConnectionFactory connections,
    WorkbenchDirtyMarker dirtyMarker,
    WorkbenchAuditWriter auditWriter,
    ILogger<ModuleBusinessConfigRepository> logger)
{
    public async Task<ModuleBusinessConfigDto?> GetAsync(int moduleId, CancellationToken token)
    {
        await using var connection = connections.Create();
        await connection.OpenAsync(token);
        if (!await ModuleExistsAsync(connection, null, moduleId, token))
            return null;

        var actions = new List<BusinessActionDto>();
        var actionIndex = new Dictionary<long, int>();
        await using (var actionCommand = new SqlCommand(
            """
            SELECT ACTION_ID,EVENT_CODE,SEQ,EFFECT_KEY,EFFECT_NAME,ENABLED,FAIL_MODE,
                   CONDITION_STRUCT,PARAM_STRUCT,REVERSE_STRUCT,REMARK,SOURCE_REF
            FROM dbo.MODULE_BUSINESS_ACTION WITH (NOLOCK)
            WHERE MODULE_ID=@ModuleId
            ORDER BY EVENT_CODE,SEQ;
            """, connection))
        {
            actionCommand.Parameters.Add("@ModuleId", SqlDbType.Int).Value = moduleId;
            await using var reader = await actionCommand.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token))
            {
                var actionId = reader.GetInt64(0);
                var action = new BusinessActionDto(
                    reader.GetInt32(2),
                    GetString(reader, 1) ?? string.Empty,
                    GetString(reader, 3) ?? string.Empty,
                    GetString(reader, 4),
                    reader.GetBoolean(5),
                    GetString(reader, 6) ?? "BLOCK",
                    GetString(reader, 7),
                    GetString(reader, 8),
                    GetString(reader, 9),
                    GetString(reader, 10),
                    GetString(reader, 11),
                    new List<BusinessActionOpDto>());
                actionIndex.Add(actionId, actions.Count);
                actions.Add(action);
            }
        }

        await using (var opCommand = new SqlCommand(
            """
            SELECT o.ACTION_ID,o.OP_SEQ,o.TARGET_TABLE,o.TARGET_FIELD,o.OP_CODE,o.SOURCE_SCOPE,
                   o.SOURCE_TABLE,o.SOURCE_FIELD,o.SOURCE_AGG,o.SOURCE_CONSTANT,
                   o.SOURCE_TERMS_STRUCT,o.MATCH_STRUCT,o.CONDITION_STRUCT,o.REMARK
            FROM dbo.MODULE_BUSINESS_ACTION_OP o WITH (NOLOCK)
            INNER JOIN dbo.MODULE_BUSINESS_ACTION a WITH (NOLOCK) ON a.ACTION_ID=o.ACTION_ID
            WHERE a.MODULE_ID=@ModuleId
            ORDER BY a.EVENT_CODE,a.SEQ,o.OP_SEQ;
            """, connection))
        {
            opCommand.Parameters.Add("@ModuleId", SqlDbType.Int).Value = moduleId;
            await using var reader = await opCommand.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token))
            {
                if (!actionIndex.TryGetValue(reader.GetInt64(0), out var index))
                    continue;
                var op = new BusinessActionOpDto(
                    reader.GetInt32(1),
                    GetString(reader, 2) ?? string.Empty,
                    GetString(reader, 3) ?? string.Empty,
                    GetString(reader, 4) ?? string.Empty,
                    GetString(reader, 5) ?? string.Empty,
                    GetString(reader, 6),
                    GetString(reader, 7),
                    GetString(reader, 8),
                    GetString(reader, 9),
                    GetString(reader, 10),
                    GetString(reader, 11),
                    GetString(reader, 12),
                    GetString(reader, 13));
                var current = actions[index];
                actions[index] = current with { Ops = (current.Ops ?? []).Append(op).ToList() };
            }
        }

        var rules = new List<ModuleValidationRuleDto>();
        await using (var ruleCommand = new SqlCommand(
            """
            SELECT SEQ,STAGE,VALIDATION_KEY,ENABLED,PARAM_STRUCT,MESSAGE,REMARK,SOURCE_REF
            FROM dbo.MODULE_VALIDATION_RULE WITH (NOLOCK)
            WHERE MODULE_ID=@ModuleId
            ORDER BY STAGE,SEQ;
            """, connection))
        {
            ruleCommand.Parameters.Add("@ModuleId", SqlDbType.Int).Value = moduleId;
            await using var reader = await ruleCommand.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token))
            {
                rules.Add(new ModuleValidationRuleDto(
                    reader.GetInt32(0),
                    GetString(reader, 1) ?? string.Empty,
                    GetString(reader, 2) ?? string.Empty,
                    reader.GetBoolean(3),
                    GetString(reader, 4),
                    GetString(reader, 5),
                    GetString(reader, 6),
                    GetString(reader, 7)));
            }
        }

        return new ModuleBusinessConfigDto(moduleId, actions, rules);
    }

    public async Task SaveAsync(
        int moduleId,
        SaveModuleBusinessConfigRequest request,
        string updatedBy,
        CancellationToken token)
    {
        var issues = ModuleBusinessConfigValidator.Validate(request);
        if (issues.Count > 0)
            throw new ArgumentException("业务动作配置校验未通过：\r\n" + string.Join("\r\n", issues));

        await using var connection = connections.Create();
        await connection.OpenAsync(token);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(token);
        try
        {
            var (masterTable, detailTable) = await ReadModuleShapeAsync(connection, transaction, moduleId, token)
                ?? throw new KeyNotFoundException($"模块 {moduleId} 不存在。");
            if (masterTable is null && detailTable is null
                && (request.Actions.Count > 0 || request.ValidationRules.Count > 0))
                throw new ArgumentException("两表皆空模块禁止配置业务动作/校验规则。");

            var physicalIssues = await ValidatePhysicalAsync(
                moduleId, masterTable, detailTable, request, token);
            if (physicalIssues.Count > 0)
                throw new ArgumentException("业务动作配置物理校验未通过：\r\n" + string.Join("\r\n", physicalIssues));

            await using (var deleteRules = new SqlCommand(
                "DELETE FROM dbo.MODULE_VALIDATION_RULE WHERE MODULE_ID=@ModuleId;",
                connection, transaction))
            {
                deleteRules.Parameters.Add("@ModuleId", SqlDbType.Int).Value = moduleId;
                await deleteRules.ExecuteNonQueryAsync(token);
            }
            await using (var deleteActions = new SqlCommand(
                "DELETE FROM dbo.MODULE_BUSINESS_ACTION WHERE MODULE_ID=@ModuleId;",
                connection, transaction))
            {
                deleteActions.Parameters.Add("@ModuleId", SqlDbType.Int).Value = moduleId;
                await deleteActions.ExecuteNonQueryAsync(token);
            }

            foreach (var action in request.Actions.OrderBy(item => item.Seq))
            {
                var actionId = await InsertActionAsync(connection, transaction, moduleId, action, updatedBy, token);
                if (action.Ops is { Count: > 0 })
                    foreach (var op in action.Ops.OrderBy(item => item.OpSeq))
                        await InsertOpAsync(connection, transaction, actionId, op, token);
            }
            foreach (var rule in request.ValidationRules.OrderBy(item => item.Seq))
                await InsertRuleAsync(connection, transaction, moduleId, rule, updatedBy, token);

            await dirtyMarker.MarkDirtyAsync(connection, transaction, moduleId, updatedBy, token);
            await transaction.CommitAsync(token);
        }
        catch
        {
            await transaction.RollbackAsync(token);
            throw;
        }

        await auditWriter.WriteBestEffortAsync(
            moduleId, "MODULE_BUSINESS_ACTION", "SAVE", "保存业务动作/校验配置", updatedBy,
            "MENU", result: 1, null, token);
        logger.LogInformation(
            "保存业务动作配置 module={ModuleId} actions={Actions} rules={Rules}",
            moduleId, request.Actions.Count, request.ValidationRules.Count);
    }

    private async Task<IReadOnlyList<string>> ValidatePhysicalAsync(
        int moduleId,
        string? masterTable,
        string? detailTable,
        SaveModuleBusinessConfigRequest request,
        CancellationToken token)
    {
        var issues = new List<string>();
        if (request.Actions.Count == 0)
            return issues;

        await using var connection = connections.Create();
        await connection.OpenAsync(token);
        var columns = await LoadPhysicalColumnsAsync(connection, token);
        foreach (var action in request.Actions)
        {
            foreach (var op in action.Ops ?? Array.Empty<BusinessActionOpDto>())
            {
                var where = $"动作 SEQ={action.Seq} 公式行 OP_SEQ={op.OpSeq}";
                if (ModuleBusinessConfigValidator.IsPlaceholderOp(op.OpCode, op.TargetTable, op.TargetField))
                    continue;
                if (!columns.Contains(Key(op.TargetTable, op.TargetField)))
                    issues.Add($"{where}：目标表/字段不存在 {op.TargetTable}.{op.TargetField}。");

                var scope = op.SourceScope.Trim().ToUpperInvariant();
                var sourceTable = scope switch
                {
                    "MASTER" => masterTable,
                    "DETAIL" => detailTable,
                    "TABLE" => op.SourceTable,
                    _ => null,
                };
                if (sourceTable is null)
                {
                    if (scope is "MASTER" or "DETAIL")
                        issues.Add($"{where}：模块 {moduleId} 未配置{(scope == "MASTER" ? "操作主表" : "操作副表")}，无法使用 {scope} 源。");
                    continue;
                }
                if (op.SourceField is { Length: > 0 } && !columns.Contains(Key(sourceTable, op.SourceField)))
                    issues.Add($"{where}：源表/字段不存在 {sourceTable}.{op.SourceField}。");
                if (!string.IsNullOrWhiteSpace(op.SourceTerms))
                    foreach (var field in ReadTermFields(op.SourceTerms!))
                        if (!columns.Contains(Key(sourceTable, field)))
                            issues.Add($"{where}：源加减项字段不存在 {sourceTable}.{field}。");
            }
        }
        if (await HasFieldRelationEffectColumnsAsync(connection, token))
        {
            var edges = await LoadEffectEdgesAsync(connection, token);
            foreach (var action in request.Actions)
            {
                foreach (var op in action.Ops ?? Array.Empty<BusinessActionOpDto>())
                {
                    if (string.IsNullOrWhiteSpace(op.Match))
                        continue;
                    var matchItems = TryParseMatchItems(op.Match!);
                    if (matchItems is null || matchItems.Count == 0)
                        continue;
                    // The effect-edge catalog registers row-level (detail) correlations
                    // only; master completion rows locate their targets through the same
                    // match keys without an edge entry (their target table has no rows
                    // in the catalog). Enforce the edge check only when the target table
                    // itself participates in the catalog, otherwise the row shape is
                    // validated by the physical and condition checks below.
                    if (!edges.Values.Any(group => group.Any(edge => edge.ToTable == op.TargetTable)))
                        continue;
                    var resolved = matchItems
                        .Select(item => new
                        {
                            Scope = item.SourceScope,
                            FromTable = ResolveMatchSourceTable(item.SourceScope, item.SourceTable,
                                masterTable, detailTable, op.SourceTable),
                            FromColumn = item.SourceField,
                            ToColumn = item.TargetColumn,
                        })
                        .ToList();
                    // A match may use a subset of a registered edge: every key must
                    // correspond to a registered (scope, from-table, from-column,
                    // to-table, to-column) tuple on the same target table, while the
                    // edge may carry additional keys the formula does not constrain.
                    // Keys without a registered counterpart still fail closed.
                    var matched = edges.Values.Any(group =>
                        group.Count > 0
                        && group.Any(edge => edge.ToTable == op.TargetTable)
                        && resolved.All(item => group.Any(edge =>
                            edge.ToTable == op.TargetTable
                            && edge.FromTable == item.FromTable
                            && edge.FromColumn == item.FromColumn
                            && edge.ToColumn == item.ToColumn
                            && edge.Scope == item.Scope)));
                    if (!matched)
                    {
                        issues.Add(
                            $"动作 SEQ={action.Seq} 公式行 OP_SEQ={op.OpSeq}：定位键未登记效果关系边（目标表 {op.TargetTable}），请先登记关系再保存。");
                    }
                }
            }
        }
        return issues;
    }

    private static async Task<bool> HasFieldRelationEffectColumnsAsync(
        SqlConnection connection,
        CancellationToken token)
    {
        const string sql = """
            SELECT CASE WHEN EXISTS (
                SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID(N'dbo.FIELD_RELATION')
                  AND name = N'RELATION_KIND'
            ) THEN 1 ELSE 0 END;
            """;
        await using var command = new SqlCommand(sql, connection);
        return Convert.ToInt32(await command.ExecuteScalarAsync(token)) == 1;
    }

    private static async Task<Dictionary<long, List<EffectEdge>>> LoadEffectEdgesAsync(
        SqlConnection connection,
        CancellationToken token)
    {
        const string sql = """
            SELECT RELATION_ID,RELATION_NAME,SOURCE_SCOPE,KEY_ORDINAL,
                   FROM_TABLE,FROM_COLUMN,TO_TABLE,TO_COLUMN
            FROM dbo.FIELD_RELATION WITH (NOLOCK)
            WHERE RELATION_KIND=N'EFFECT'
            ORDER BY RELATION_ID,KEY_ORDINAL;
            """;
        await using var command = new SqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(token);
        var result = new Dictionary<long, List<EffectEdge>>();
        while (await reader.ReadAsync(token))
        {
            var relationId = reader.GetInt64(0);
            var edge = new EffectEdge(
                reader.IsDBNull(2) ? null : reader.GetString(2).Trim(),
                reader.GetInt32(3),
                reader.GetString(4).Trim(),
                reader.GetString(5).Trim(),
                reader.GetString(6).Trim(),
                reader.GetString(7).Trim());
            if (!result.TryGetValue(relationId, out var list))
            {
                list = new List<EffectEdge>();
                result[relationId] = list;
            }
            list.Add(edge);
        }
        return result;
    }

    private static IReadOnlyList<MatchItem>? TryParseMatchItems(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return null;
            var items = new List<MatchItem>();
            foreach (var element in doc.RootElement.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.Object
                    || !element.TryGetProperty("target", out var target)
                    || target.ValueKind != JsonValueKind.String
                    || !element.TryGetProperty("source", out var source)
                    || source.ValueKind != JsonValueKind.Object)
                {
                    return null;
                }
                var scope = source.TryGetProperty("scope", out var scopeValue)
                    && scopeValue.ValueKind == JsonValueKind.String
                        ? scopeValue.GetString()!.Trim().ToUpperInvariant()
                        : string.Empty;
                var field = source.TryGetProperty("field", out var fieldValue)
                    && fieldValue.ValueKind == JsonValueKind.String
                        ? fieldValue.GetString()!.Trim()
                        : string.Empty;
                var sourceTable = source.TryGetProperty("table", out var tableValue)
                    && tableValue.ValueKind == JsonValueKind.String
                        ? tableValue.GetString()!.Trim()
                        : null;
                items.Add(new MatchItem(target.GetString()!.Trim(), scope, field, sourceTable));
            }
            return items;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? ResolveMatchSourceTable(
        string scope,
        string? sourceTable,
        string? masterTable,
        string? detailTable,
        string? opSourceTable) => scope switch
    {
        "MASTER" => masterTable,
        "DETAIL" => detailTable,
        "TABLE" => sourceTable ?? opSourceTable,
        _ => null,
    };

    private static async Task<long> InsertActionAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        int moduleId,
        BusinessActionDto action,
        string updatedBy,
        CancellationToken token)
    {
        const string sql = """
            INSERT INTO dbo.MODULE_BUSINESS_ACTION
                (MODULE_ID,EVENT_CODE,SEQ,EFFECT_KEY,EFFECT_NAME,ENABLED,FAIL_MODE,
                 CONDITION_STRUCT,PARAM_STRUCT,REVERSE_STRUCT,REMARK,SOURCE_REF,
                 CREATE_PERSON,CREATE_DATE,LAST_UPDATE_BY,LAST_UPDATE_DATE)
            VALUES
                (@ModuleId,@Event,@Seq,@EffectKey,@EffectName,@Enabled,@FailMode,
                 @Condition,@Params,@Reverse,@Remark,@SourceRef,
                 @UpdatedBy,SYSDATETIME(),@UpdatedBy,SYSDATETIME());
            SELECT SCOPE_IDENTITY();
            """;
        await using var command = new SqlCommand(sql, connection, transaction);
        command.Parameters.Add("@ModuleId", SqlDbType.Int).Value = moduleId;
        command.Parameters.Add("@Event", SqlDbType.NVarChar, 30).Value = action.EventCode.Trim();
        command.Parameters.Add("@Seq", SqlDbType.Int).Value = action.Seq;
        command.Parameters.Add("@EffectKey", SqlDbType.NVarChar, 50).Value = action.EffectKey.Trim();
        AddNullable(command, "@EffectName", action.EffectName, 200);
        command.Parameters.Add("@Enabled", SqlDbType.Bit).Value = action.Enabled;
        command.Parameters.Add("@FailMode", SqlDbType.NVarChar, 10).Value = action.FailMode.Trim();
        AddNullable(command, "@Condition", action.Condition, null);
        AddNullable(command, "@Params", action.Params, null);
        AddNullable(command, "@Reverse", action.Reverse, null);
        AddNullable(command, "@Remark", action.Remark, 500);
        AddNullable(command, "@SourceRef", action.SourceRef, 100);
        command.Parameters.Add("@UpdatedBy", SqlDbType.NVarChar, 40).Value = updatedBy;
        return Convert.ToInt64(await command.ExecuteScalarAsync(token));
    }

    private static async Task InsertOpAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        long actionId,
        BusinessActionOpDto op,
        CancellationToken token)
    {
        const string sql = """
            INSERT INTO dbo.MODULE_BUSINESS_ACTION_OP
                (ACTION_ID,OP_SEQ,TARGET_TABLE,TARGET_FIELD,OP_CODE,SOURCE_SCOPE,
                 SOURCE_TABLE,SOURCE_FIELD,SOURCE_AGG,SOURCE_CONSTANT,
                 SOURCE_TERMS_STRUCT,MATCH_STRUCT,CONDITION_STRUCT,REMARK)
            VALUES
                (@ActionId,@OpSeq,@TargetTable,@TargetField,@OpCode,@SourceScope,
                 @SourceTable,@SourceField,@SourceAgg,@SourceConstant,
                 @SourceTerms,@Match,@Condition,@Remark);
            """;
        await using var command = new SqlCommand(sql, connection, transaction);
        command.Parameters.Add("@ActionId", SqlDbType.BigInt).Value = actionId;
        command.Parameters.Add("@OpSeq", SqlDbType.Int).Value = op.OpSeq;
        command.Parameters.Add("@TargetTable", SqlDbType.NVarChar, 64).Value = op.TargetTable.Trim();
        command.Parameters.Add("@TargetField", SqlDbType.NVarChar, 64).Value = op.TargetField.Trim();
        command.Parameters.Add("@OpCode", SqlDbType.NVarChar, 20).Value = op.OpCode.Trim();
        command.Parameters.Add("@SourceScope", SqlDbType.NVarChar, 10).Value = op.SourceScope.Trim();
        AddNullable(command, "@SourceTable", op.SourceTable, 64);
        AddNullable(command, "@SourceField", op.SourceField, 64);
        AddNullable(command, "@SourceAgg", op.SourceAgg, 10);
        // The empty string is a legal clear value for CONSTANT sources (e.g.
        // FINISHED_PERSON=''), so it must survive storage instead of collapsing to NULL.
        var constantParameter = command.Parameters.Add("@SourceConstant", SqlDbType.NVarChar, -1);
        constantParameter.Value = op.SourceConstant is null ? DBNull.Value : op.SourceConstant.Trim();
        AddNullable(command, "@SourceTerms", op.SourceTerms, null);
        AddNullable(command, "@Match", op.Match, null);
        AddNullable(command, "@Condition", op.Condition, null);
        AddNullable(command, "@Remark", op.Remark, 200);
        await command.ExecuteNonQueryAsync(token);
    }

    private static async Task InsertRuleAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        int moduleId,
        ModuleValidationRuleDto rule,
        string updatedBy,
        CancellationToken token)
    {
        const string sql = """
            INSERT INTO dbo.MODULE_VALIDATION_RULE
                (MODULE_ID,STAGE,SEQ,VALIDATION_KEY,ENABLED,PARAM_STRUCT,MESSAGE,REMARK,SOURCE_REF,
                 CREATE_PERSON,CREATE_DATE,LAST_UPDATE_BY,LAST_UPDATE_DATE)
            VALUES
                (@ModuleId,@Stage,@Seq,@ValidationKey,@Enabled,@Params,@Message,@Remark,@SourceRef,
                 @UpdatedBy,SYSDATETIME(),@UpdatedBy,SYSDATETIME());
            """;
        await using var command = new SqlCommand(sql, connection, transaction);
        command.Parameters.Add("@ModuleId", SqlDbType.Int).Value = moduleId;
        command.Parameters.Add("@Stage", SqlDbType.NVarChar, 20).Value = rule.Stage.Trim();
        command.Parameters.Add("@Seq", SqlDbType.Int).Value = rule.Seq;
        command.Parameters.Add("@ValidationKey", SqlDbType.NVarChar, 50).Value = rule.ValidationKey.Trim();
        command.Parameters.Add("@Enabled", SqlDbType.Bit).Value = rule.Enabled;
        command.Parameters.Add("@Params", SqlDbType.NVarChar, -1).Value = rule.Params?.Trim() ?? "{}";
        AddNullable(command, "@Message", rule.Message, 500);
        AddNullable(command, "@Remark", rule.Remark, 500);
        AddNullable(command, "@SourceRef", rule.SourceRef, 100);
        command.Parameters.Add("@UpdatedBy", SqlDbType.NVarChar, 40).Value = updatedBy;
        await command.ExecuteNonQueryAsync(token);
    }

    private static async Task<bool> ModuleExistsAsync(
        SqlConnection connection,
        SqlTransaction? transaction,
        int moduleId,
        CancellationToken token)
    {
        await using var command = new SqlCommand(
            "SELECT CASE WHEN EXISTS(SELECT 1 FROM dbo.MODULES WHERE M_IDX=@ModuleId) THEN 1 ELSE 0 END;",
            connection, transaction);
        command.Parameters.Add("@ModuleId", SqlDbType.Int).Value = moduleId;
        return Convert.ToInt32(await command.ExecuteScalarAsync(token)) == 1;
    }

    private static async Task<(string? Master, string? Detail)?> ReadModuleShapeAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        int moduleId,
        CancellationToken token)
    {
        await using var command = new SqlCommand(
            "SELECT MASTER_TABLE,DETAIL_TABLE FROM dbo.MODULES WITH (NOLOCK) WHERE M_IDX=@ModuleId;",
            connection, transaction);
        command.Parameters.Add("@ModuleId", SqlDbType.Int).Value = moduleId;
        await using var reader = await command.ExecuteReaderAsync(token);
        if (!await reader.ReadAsync(token))
            return null;
        var master = reader.IsDBNull(0) ? null : reader.GetString(0).Trim();
        var detail = reader.IsDBNull(1) ? null : reader.GetString(1).Trim();
        return (string.IsNullOrWhiteSpace(master) ? null : master, string.IsNullOrWhiteSpace(detail) ? null : detail);
    }

    private static async Task<HashSet<string>> LoadPhysicalColumnsAsync(
        SqlConnection connection,
        CancellationToken token)
    {
        const string sql = """
            SELECT o.name,c.name
            FROM sys.objects o
            JOIN sys.columns c ON c.object_id=o.object_id
            WHERE o.type IN ('U','V') AND SCHEMA_NAME(o.schema_id)=N'dbo';
            """;
        await using var command = new SqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(token);
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (await reader.ReadAsync(token))
            result.Add(Key(reader.GetString(0), reader.GetString(1)));
        return result;
    }

    private static IReadOnlyList<string> ReadTermFields(string json)
    {
        var fields = new List<string>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return fields;
            foreach (var item in doc.RootElement.EnumerateArray())
                if (item.ValueKind == JsonValueKind.Object
                    && item.TryGetProperty("field", out var f) && f.ValueKind == JsonValueKind.String
                    && f.GetString() is { Length: > 0 } field)
                    fields.Add(field);
        }
        catch (JsonException)
        {
            // 结构校验已拦截非法 JSON，此处只做字段收集。
        }
        return fields;
    }

    private static string Key(string table, string field) => table + "." + field;

    private sealed record EffectEdge(
        string? Scope,
        int KeyOrdinal,
        string FromTable,
        string FromColumn,
        string ToTable,
        string ToColumn);

    private sealed record MatchItem(
        string TargetColumn,
        string SourceScope,
        string SourceField,
        string? SourceTable);

    private static string? GetString(SqlDataReader reader, int index) =>
        reader.IsDBNull(index) ? null : reader.GetString(index).Trim();

    private static void AddNullable(
        SqlCommand command,
        string name,
        string? value,
        int? maxLength)
    {
        var parameter = maxLength is { } length
            ? command.Parameters.Add(name, SqlDbType.NVarChar, length)
            : command.Parameters.Add(name, SqlDbType.NVarChar, -1);
        parameter.Value = string.IsNullOrWhiteSpace(value) ? DBNull.Value : value.Trim();
    }
}
