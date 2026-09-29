using System.Data;
using System.Text.Json;
using EOS.API.Data;
using EOS.API.Data.Workbench;
using EOS.API.Features.Assistant.Metrics;
using EOS.API.Features.Assistant.Situation;
using EOS.API.Features.Assistant.Tools;
using EOS.API.Models;
using EOS.API.Security;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;

namespace EOS.API.Features.Assistant.Diagnosis;

/// <summary>一条"此刻办不下去"的记录（摘要来源用；原因由规则元数据或生命周期状态给出）。</summary>
public sealed record DiagnosisBlockedRecord(
    IReadOnlyList<string> RecordKey,
    string Key,
    string Reason,
    string Source);

/// <summary>
/// 诊断的事实读取：本类**唯一一处为诊断写 SQL**。
///
/// <para>
/// 只读、参数化、有界：表名与列名只来自已发布定义与配置参数，且落 SQL 前一律过
/// <c>sys.*</c> 目录白名单（`dbo` 下的表/视图）；列名还要在该表上真实存在，否则整条判据放弃判定。
/// 取不到就写进 <c>Missing</c>——**不做"大概是这个原因"的推断**。
/// </para>
/// </summary>
public sealed class RecordDiagnosisReader(
    IWorkbenchSearchGateway gateway,
    IModuleFlowGateway flows,
    IFieldRelationRepository relations,
    DbConnectionFactory connections,
    IOptions<AssistantDiagnosisOptions> options,
    IOptions<AssistantSituationBudgetOptions> situationLimits,
    ILogger<RecordDiagnosisReader> logger) : IRecordDiagnosisReader, IBlockedRecordProbe
{
    private const string RecordResourceType = "WORKBENCH_RECORD";

    public async Task<RecordDiagnosisFacts?> LoadAsync(
        string userId, DiagnosisContext context, CancellationToken token)
    {
        var definition = context.Definition;
        var permission = context.Permission;
        var scope = permission.Scope();

        var rows = await gateway.GetExportRowsByKeysAsync(
            definition, [context.Keys], token, exportFields: definition.MasterFields,
            dataFilter: permission.Rights.DataFilter);
        if (rows.Count == 0)
        {
            return null;
        }

        var row = rows[0];
        var state = await flows.GetRecordStateAsync(
            definition.MasterTable, definition.MasterPkOrder, context.Keys, token);
        if (!state.Found)
        {
            return null;
        }

        var missing = new List<string>();
        var rules = DiagnosisRuleParser.Parse(definition.ValidationRules);
        var signals = await ProbeRulesAsync(definition, rules, row, missing, token);
        var form = await gateway.GetFormDefinitionAsync(
            definition, userId, "edit", scope.CanViewCost, scope.CanViewSecrecy,
            scope.DeniedMaster, scope.DeniedDetail, scope.DeniedMaster, scope.DeniedDetail,
            scope.DeniedMaster, scope.DeniedDetail, token);
        var fields = BuildFieldGuards(form, scope.CanViewCost, scope.CanViewSecrecy);
        var provenance = await BuildProvenanceAsync(definition, form, token);
        var effects = await LoadEffectsAsync(definition.ModuleId, token);
        var flow = await LoadFlowAsync(definition, context.Keys, token);
        var lastFailure = await LoadLastFailureAsync(definition.ModuleId, context.Keys, token);

        if (flow is null && definition.HasWorkflow)
        {
            missing.Add("流程定义（该模块声明了审批流程，但未取到流程定义）");
        }

        return new RecordDiagnosisFacts(
            context.Keys,
            string.Join("/", context.Keys),
            state.Confirmed,
            state.Finished,
            rules,
            signals,
            fields,
            provenance,
            effects,
            flow,
            lastFailure,
            missing);
    }

    /// <summary>
    /// 有界地逐单求值"此刻过不了校验 / 状态不允许"：扫**最近的**记录（按建立日期倒序 + 年龄上界），
    /// 逐单判"已结案"与可只读判定的校验判据。取不到或判不了的一律不报（宁可少报）。
    /// </summary>
    public async Task<IReadOnlyList<DiagnosisBlockedRecord>> ProbeBlockedAsync(
        string userId, WorkbenchDefinition definition, ModulePermission permission, CancellationToken token)
    {
        var limits = situationLimits.Value;
        var fields = definition.MasterFields.Select(field => field.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!fields.Contains("CREATE_DATE") || !fields.Contains("CONFIRM_TAG")) return [];

        var cutoff = DateTime.UtcNow.Date.AddDays(-limits.BlockedNowMaxAgeDays).ToString("yyyy-MM-dd");
        WorkbenchData data;
        try
        {
            data = await gateway.GetRowsAsync(
                definition, detail: false, new Dictionary<string, string>(), page: 1,
                pageSize: limits.BlockedNowScanRecords, token,
                query: new WorkbenchQuery([new WorkbenchQueryCondition("CREATE_DATE", "gte", cutoff, null, null)]),
                sortField: "CREATE_DATE",
                sortDirection: "desc",
                dataFilter: permission.Rights.DataFilter);
        }
        catch (ArgumentException ex)
        {
            // 排序字段或条件不被查询实现接受：如实跳过本模块，不影响其余模块。
            logger.LogWarning(ex, "助手诊断跳过模块的「此刻办不下去」扫描 module={ModuleId}", definition.ModuleId);
            return [];
        }

        var rows = data.Rows;
        var rules = DiagnosisRuleParser.Parse(definition.ValidationRules)
            .Where(rule => rule.Enabled && rule.ValidationKey.Equals("reference-exists", StringComparison.OrdinalIgnoreCase))
            .Take(limits.BlockedNowProbeRules)
            .ToList();
        var blocked = new List<DiagnosisBlockedRecord>();
        foreach (var row in rows)
        {
            var keyValues = ReadKeyValues(definition, row);
            if (keyValues is null) continue;
            var key = string.Join("/", keyValues);
            if (IsTrue(row, "FINISHED_TAG"))
            {
                blocked.Add(new DiagnosisBlockedRecord(
                    keyValues, key, "单据已结案，改动前须先取消结案。", LifecycleEditGuards.FinishedCode));
                continue;
            }

            var missing = new List<string>();
            var signals = await ProbeRulesAsync(definition, rules, row, missing, token);
            foreach (var signal in signals)
            {
                var rule = FindRule(rules, signal);
                if (rule is null || !rule.Enabled || string.IsNullOrWhiteSpace(rule.Message)) continue;
                blocked.Add(new DiagnosisBlockedRecord(
                    keyValues, key, rule.Message, $"validation:{signal.Stage}/{signal.ValidationKey}#{signal.Seq}"));
                break;
            }
        }

        return blocked;
    }

    /// <summary>
    /// 只读可判定的校验判据：当前只覆盖 <c>reference-exists</c> 的单键形态
    /// （被引用表 + refKey + 无 join/targets）——这是"这张单存不下去"最常见的一类，
    /// 且一次存在性查询即可判定。其余类别只能在保存时由引擎判定，一律不猜。
    /// </summary>
    private async Task<IReadOnlyList<DiagnosisRuleSignal>> ProbeRulesAsync(
        WorkbenchDefinition definition,
        IReadOnlyList<DiagnosisRule> rules,
        IReadOnlyDictionary<string, object?> row,
        List<string> missing,
        CancellationToken token)
    {
        var signals = new List<DiagnosisRuleSignal>();
        foreach (var rule in rules)
        {
            if (!rule.Enabled || !rule.ValidationKey.Equals("reference-exists", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var targets = DiagnosisRuleParser.ReadReferenceTargets(rule.Parameters);
            if (targets.Count == 0)
            {
                missing.Add($"规则 {rule.Stage}/{rule.ValidationKey}#{rule.Seq} 的引用目标形态无法只读判定");
                continue;
            }

            foreach (var target in targets)
            {
                if (!await IsReadableObjectAsync(target.Table, token))
                {
                    missing.Add($"被引用表 {target.Table} 不在可读白名单（dbo 下的表/视图）");
                    continue;
                }

                var value = ReadValue(row, target.KeyField);
                if (string.IsNullOrWhiteSpace(value))
                {
                    if (!target.AllowEmpty)
                    {
                        signals.Add(new DiagnosisRuleSignal(
                            rule.Stage, rule.ValidationKey, rule.Seq, $"关联键 {target.KeyField} 为空值"));
                    }

                    continue;
                }

                var exists = await ExistsAsync(target, value, token);
                if (exists is null)
                {
                    missing.Add($"规则 {rule.Stage}/{rule.ValidationKey}#{rule.Seq}：被引用表 {target.Table} 上没有可比对的列 {target.KeyField}");
                }
                else if (exists == false)
                {
                    signals.Add(new DiagnosisRuleSignal(
                        rule.Stage, rule.ValidationKey, rule.Seq, $"被引用表 {target.Table} 找不到匹配行（{target.KeyField}={value}）"));
                }
            }
        }

        return signals;
    }

    private static DiagnosisRule? FindRule(IReadOnlyList<DiagnosisRule> rules, DiagnosisRuleSignal signal) =>
        rules.FirstOrDefault(rule => rule.Stage.Equals(signal.Stage, StringComparison.OrdinalIgnoreCase)
            && rule.ValidationKey.Equals(signal.ValidationKey, StringComparison.OrdinalIgnoreCase)
            && rule.Seq == signal.Seq);

    private static IReadOnlyList<string>? ReadKeyValues(
        WorkbenchDefinition definition, IReadOnlyDictionary<string, object?> row)
    {
        var values = new List<string>(definition.MasterPkOrder.Count);
        foreach (var column in definition.MasterPkOrder)
        {
            if (!row.TryGetValue(column, out var value) || value is null) return null;
            values.Add(value.ToString() ?? string.Empty);
        }

        return values;
    }

    private static bool IsTrue(IReadOnlyDictionary<string, object?> row, string column)
    {
        if (!row.TryGetValue(column, out var value) || value is null) return false;
        if (value is bool flag) return flag;
        var text = value.ToString();
        return text == "1" || string.Equals(text, "True", StringComparison.OrdinalIgnoreCase);
    }

    private static string? ReadValue(IReadOnlyDictionary<string, object?> row, string column) =>
        row.TryGetValue(column, out var value) ? value?.ToString() : null;

    /// <summary>字段保护：只列"写不进去"的字段，且跳过单据生命周期系统列（其写入由状态与动作路径决定）。</summary>
    private static IReadOnlyList<DiagnosisFieldFact> BuildFieldGuards(
        FormDefinition? form, bool canViewCost, bool canViewSecrecy)
    {
        if (form is null) return [];
        var guards = new List<DiagnosisFieldFact>();
        foreach (var field in form.MasterFields)
        {
            if (WorkflowStates.IsLifecycleColumn(field.Key)) continue;
            if (DiagnosisFieldGuardText.IsEngineMaintained(field.Key))
            {
                guards.Add(new DiagnosisFieldFact(field.Key, field.Label,
                    DiagnosisFieldGuardText.EngineMaintained(field.Key),
                    DiagnosisFieldGuardText.EngineMaintainedSource, true));
                continue;
            }

            if (field.IsVirtual)
            {
                guards.Add(new DiagnosisFieldFact(field.Key, field.Label,
                    DiagnosisFieldGuardText.Virtual(field.Label), "FIELDS.IS_VIRTUAL", true));
                continue;
            }

            if (field.IsCost && !canViewCost)
            {
                guards.Add(new DiagnosisFieldFact(field.Key, field.Label,
                    DiagnosisFieldGuardText.CostDenied(field.Label), "FIELDS.IS_COST", true));
                continue;
            }

            if (field.IsSecrecy && !canViewSecrecy)
            {
                guards.Add(new DiagnosisFieldFact(field.Key, field.Label,
                    DiagnosisFieldGuardText.SecrecyDenied(field.Label), "FIELDS.IS_SECRECY", true));
                continue;
            }

            if (field.IsReadonly)
            {
                guards.Add(new DiagnosisFieldFact(field.Key, field.Label,
                    DiagnosisFieldGuardText.Readonly(field.Label), "FIELDS.IS_READONLY", true));
            }
        }

        return guards;
    }

    /// <summary>
    /// 值来源：虚拟列 / 选择器回填 / 已登记字段关系 / 表单默认值。
    /// **虚拟列只报"是虚拟列"，表达式原文不下发**。
    /// </summary>
    private async Task<IReadOnlyList<DiagnosisProvenanceFact>> BuildProvenanceAsync(
        WorkbenchDefinition definition, FormDefinition? form, CancellationToken token)
    {
        if (form is null) return [];
        var registered = await LoadRelationsAsync(definition.MasterTable, token);
        var provenance = new List<DiagnosisProvenanceFact>();
        foreach (var field in form.MasterFields)
        {
            if (field.IsVirtual)
            {
                provenance.Add(new DiagnosisProvenanceFact(field.Key, field.Label, "virtual", null));
                continue;
            }

            if (field.Choosers is { Count: > 0 })
            {
                var chooser = field.Choosers[0];
                provenance.Add(new DiagnosisProvenanceFact(
                    field.Key, field.Label, "chooser", chooser.SourceKey ?? chooser.Table));
                continue;
            }

            if (registered.TryGetValue(field.Key, out var relation))
            {
                provenance.Add(new DiagnosisProvenanceFact(field.Key, field.Label, "relation", relation));
                continue;
            }

            var hasDefault = !string.IsNullOrWhiteSpace(field.DefaultValue)
                || (form.DefaultValues is not null && form.DefaultValues.ContainsKey(field.Key));
            if (hasDefault)
            {
                provenance.Add(new DiagnosisProvenanceFact(field.Key, field.Label, "default", null));
            }
        }

        return provenance;
    }

    private async Task<Dictionary<string, string>> LoadRelationsAsync(string masterTable, CancellationToken token)
    {
        var rows = await relations.ListAsync(masterTable, token);
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            if (!row.FromTable.Equals(masterTable, StringComparison.OrdinalIgnoreCase)) continue;
            map.TryAdd(row.FromColumn, $"{row.ToTable}.{row.ToColumn}");
        }

        return map;
    }

    private async Task<IReadOnlyList<DiagnosisEffectFact>> LoadEffectsAsync(int moduleId, CancellationToken token)
    {
        await using var connection = connections.Create();
        await connection.OpenAsync(token);
        await using var command = new SqlCommand("""
            SELECT TOP (@Max) a.EFFECT_KEY, o.TARGET_TABLE, o.TARGET_FIELD, o.OP_CODE
            FROM dbo.MODULE_BUSINESS_ACTION_OP o WITH (NOLOCK)
            JOIN dbo.MODULE_BUSINESS_ACTION a WITH (NOLOCK) ON a.ACTION_ID = o.ACTION_ID
            WHERE a.M_IDX = @ModuleId AND ISNULL(a.ENABLED, 1) = 1
            ORDER BY a.SEQ, o.OP_SEQ;
            """, connection);
        command.Parameters.Add("@Max", SqlDbType.Int).Value = options.Value.MaxEffects;
        command.Parameters.Add("@ModuleId", SqlDbType.Int).Value = moduleId;
        var effects = new List<DiagnosisEffectFact>();
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
        {
            effects.Add(new DiagnosisEffectFact(
                reader.IsDBNull(0) ? string.Empty : reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3)));
        }

        return effects;
    }

    /// <summary>流程段按需：模块确实配了流程定义时才展开（未配置流程的模块不报"无流程"）。</summary>
    private async Task<DiagnosisFlowFact?> LoadFlowAsync(
        WorkbenchDefinition definition, IReadOnlyList<string> keys, CancellationToken token)
    {
        var flowDefinition = await flows.GetFlowDefinitionAsync(definition.ModuleId, token);
        if (flowDefinition is null) return null;
        var instance = await flows.GetInstanceAsync(
            definition.ModuleId, WorkbenchKeyCondition.Build(definition.MasterPkOrder, keys), token);
        if (instance is null)
        {
            return new DiagnosisFlowFact("未发起", null, [], null);
        }

        return new DiagnosisFlowFact(
            instance.State, instance.CurrentStepDesc, instance.CurrentApprovers, instance.StartUser);
    }

    private async Task<DiagnosisFailureFact?> LoadLastFailureAsync(
        int moduleId, IReadOnlyList<string> keys, CancellationToken token)
    {
        var limits = options.Value;
        await using var connection = connections.Create();
        await connection.OpenAsync(token);
        await using var command = new SqlCommand("""
            SELECT TOP (@Max) CONVERT(varchar(19), OCCURRED_AT, 120), ERROR_CODE, CORRELATION_ID, SUMMARY
            FROM dbo.AUDIT_EVENT WITH (NOLOCK)
            WHERE M_IDX = @ModuleId AND RESULT = 0 AND RESOURCE_TYPE = @ResourceType
              AND RESOURCE_KEY = @ResourceKey AND OCCURRED_AT >= @Since
            ORDER BY OCCURRED_AT DESC, EVENT_ID DESC;
            """, connection);
        command.Parameters.Add("@Max", SqlDbType.Int).Value = limits.LastFailureLimit;
        command.Parameters.Add("@ModuleId", SqlDbType.Int).Value = moduleId;
        command.Parameters.Add("@ResourceType", SqlDbType.NVarChar, 40).Value = RecordResourceType;
        command.Parameters.Add("@ResourceKey", SqlDbType.NVarChar, 200).Value = string.Join(',', keys);
        command.Parameters.Add("@Since", SqlDbType.DateTime2).Value = DateTime.UtcNow.AddDays(-limits.LastFailureDays);
        await using var reader = await command.ExecuteReaderAsync(token);
        if (!await reader.ReadAsync(token)) return null;
        return new DiagnosisFailureFact(
            reader.IsDBNull(0) ? string.Empty : reader.GetString(0),
            reader.IsDBNull(1) ? null : reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.IsDBNull(3) ? string.Empty : reader.GetString(3));
    }

    /// <summary>被引用表必须**真实存在**且是 <c>dbo</c> 下的表/视图，否则不拼进 SQL。</summary>
    private async Task<bool> IsReadableObjectAsync(string table, CancellationToken token)
    {
        if (!DiagnosisRuleParser.IsSafeIdentifier(table)) return false;
        await using var connection = connections.Create();
        await connection.OpenAsync(token);
        await using var command = new SqlCommand("""
            SELECT TOP 1 1
            FROM sys.objects o WITH (NOLOCK)
            JOIN sys.schemas s WITH (NOLOCK) ON s.schema_id = o.schema_id
            WHERE s.name = N'dbo' AND o.name = @Name AND o.type IN ('U', 'V');
            """, connection);
        command.Parameters.Add("@Name", SqlDbType.NVarChar, 128).Value = table;
        return await command.ExecuteScalarAsync(token) is not null;
    }

    /// <summary>
    /// 存在性判定：一次按主键列的点查。表名与列名在拼进 SQL 前已过白名单（表在 <c>dbo</c> 下、
    /// 列在该表上真实存在），值一律参数化。
    /// **判不了返回 null**（调用方记为"无法判定"）——判不了绝不当作"命中"，也绝不当作"没问题"。
    /// </summary>
    private async Task<bool?> ExistsAsync(
        DiagnosisRuleParser.ReferenceTarget target, string value, CancellationToken token)
    {
        await using var connection = connections.Create();
        await connection.OpenAsync(token);
        if (!await ColumnExistsAsync(connection, target.Table, target.KeyField, token))
        {
            logger.LogWarning("助手诊断无法判定被引用行（列不存在）table={Table} column={Column}",
                target.Table, target.KeyField);
            return null;
        }

        var sql = $"SELECT TOP 1 1 FROM dbo.[{target.Table}] WITH (NOLOCK) WHERE [{target.KeyField}] = @Value";
        var useTag = false;
        if (target.ActiveTagField is { Length: > 0 } tagField && DiagnosisRuleParser.IsSafeIdentifier(tagField))
        {
            if (!await ColumnExistsAsync(connection, target.Table, tagField, token)) return null;
            sql += $" AND [{tagField}] = @TagExpect";
            useTag = true;
        }

        sql += ";";
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@Value", SqlDbType.NVarChar, 200).Value = value;
        if (useTag)
        {
            command.Parameters.Add("@TagExpect", SqlDbType.Int).Value = target.ActiveTagExpect;
        }

        return await command.ExecuteScalarAsync(token) is not null;
    }

    private static async Task<bool> ColumnExistsAsync(
        SqlConnection connection, string table, string column, CancellationToken token)
    {
        await using var command = new SqlCommand("""
            SELECT TOP 1 1
            FROM sys.columns c WITH (NOLOCK)
            JOIN sys.objects o WITH (NOLOCK) ON o.object_id = c.object_id
            JOIN sys.schemas s WITH (NOLOCK) ON s.schema_id = o.schema_id
            WHERE s.name = N'dbo' AND o.name = @Table AND c.name = @Column;
            """, connection);
        command.Parameters.Add("@Table", SqlDbType.NVarChar, 128).Value = table;
        command.Parameters.Add("@Column", SqlDbType.NVarChar, 128).Value = column;
        return await command.ExecuteScalarAsync(token) is not null;
    }
}
