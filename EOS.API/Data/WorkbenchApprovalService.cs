using System.Data;
using Microsoft.Data.SqlClient;

using EOS.API.Models;

namespace EOS.API.Data;

/// <summary>
/// 工作台审批服务（ADR-005 §2 组件表 WorkbenchApprovalService，阶段 3）：
/// 批核/解批/结案/未结案/自动批核 + 状态机副作用 + 幂等 + 删除补偿守卫。
/// 补偿语义（落定）：
/// - 结案单据（FINISHED_TAG=1）禁止删除，需先取消结案；
/// - 任何已批核单据（CONFIRM_TAG=1，含自动批核模块）禁止删除，需先解批；
/// - 已产生库存日志（INV_DEPOT_LOG）的单据禁止删除（解批回退库存后再删）。
/// 批核副作用 SP 自带事务（自动提交），状态守卫（CONFIRM_TAG/FINISHED_TAG）防重复副作用，
/// 幂等键提供顺序重放保护；解批前置 NOBACK 校验原样保留。
/// </summary>
public sealed class WorkbenchApprovalService(
    DbConnectionFactory connections,
    WorkbenchAuditWriter auditWriter,
    WorkflowEngine workflowEngine,
    ControlledSprocInvoker controlledSprocs,
    WorkbenchIdempotency idempotency,
    ILogger<WorkbenchApprovalService> logger)
{
    /// <summary>
    /// 单据批核/解批（旧 P_WF_&lt;DOC&gt; 的受控调用）。
    /// 仅对 ModuleBusinessMap 登记的模块开放；成功返回主键，失败返回业务消息。
    /// </summary>
    public async Task<RecordSaveResult> WorkflowAsync(
        WorkbenchDefinition definition,
        IReadOnlyList<string> keyValues,
        bool approve,
        string employeeName,
        string userId,
        string? idempotencyKey,
        CancellationToken token,
        string? message = null)
    {
        var action = approve ? "APPROVE" : "DEAPPROVE";
        if (idempotencyKey is not null)
        {
            var existing = await ClaimIdempotencyAsync(definition.ModuleId, action, idempotencyKey, token);
            if (existing is { ResultKey: not null })
            {
                return RecordSaveResult.Success(keyValues);
            }
        }

        var result = await WorkflowCoreAsync(definition, keyValues, approve, employeeName, userId, token, message);
        await CompleteOrReleaseIdempotencyAsync(idempotencyKey, action, definition.ModuleId, result, keyValues, token);
        return result;
    }

    /// <summary>
    /// 结案/取消结案（旧 Comm/DoFinishOne.aspx 的受控 C# 等价，主表单笔结案）。
    /// 语义：更新主表 FINISHED_TAG/FINISHED_PERSON/FINISHED_DATE；
    /// 结案仅允许 FINISHED_TAG=0，取消结案仅允许 FINISHED_TAG=1（守卫防重复/冲突）。
    /// </summary>
    public async Task<RecordSaveResult> FinishAsync(
        WorkbenchDefinition definition,
        IReadOnlyList<string> keyValues,
        bool finish,
        string employeeName,
        string userId,
        string? idempotencyKey,
        CancellationToken token)
    {
        var action = finish ? "ENDCASE" : "UNENDCASE";
        if (idempotencyKey is not null)
        {
            var existing = await ClaimIdempotencyAsync(definition.ModuleId, action, idempotencyKey, token);
            if (existing is { ResultKey: not null })
            {
                return RecordSaveResult.Success(keyValues);
            }
        }

        var result = await FinishCoreAsync(definition, keyValues, finish, employeeName, userId, token);
        await CompleteOrReleaseIdempotencyAsync(idempotencyKey, action, definition.ModuleId, result, keyValues, token);
        return result;
    }

    /// <summary>
    /// 自动批核（MODULES.AUTO_APPROVE=1）：新增后数据自动为批核状态，无需再点批核。
    /// 等价批核端点无流程路径：CONFIRM_TAG=1/CONFIRM_PERSON='SYSTEM'/CONFIRM_DATE=GETDATE() +
    /// P_WF_&lt;DOC&gt; 业务副作用（WorkflowSproc 存在时）+ APPROVE 审计；守卫防重复。
    /// </summary>
    public async Task<RecordSaveResult> AutoApproveAsync(
        SqlConnection connection,
        WorkbenchDefinition definition,
        IReadOnlyList<string> keyValues,
        string userId,
        CancellationToken token)
    {
        if (!await WorkbenchSql.ColumnExistsAsync(connection, null, definition.MasterTable, "CONFIRM_TAG", token))
        {
            return RecordSaveResult.Success(keyValues);
        }
        var keyCondition = ControlledSprocInvoker.BuildKeyCondition(definition.MasterPkOrder, keyValues);
        var originalState = await ReadConfirmStateAsync(connection, definition.MasterTable, keyCondition, token);
        if (originalState is null)
        {
            return RecordSaveResult.Failed(RecordAccessStatus.NotFound, "RECORD_NOT_FOUND", "记录不存在。");
        }
        if (originalState.Value.Tag == true)
        {
            return RecordSaveResult.Success(keyValues);
        }
        var confirmSql = $"UPDATE dbo.[{definition.MasterTable}] SET CONFIRM_PERSON=@ConfirmPerson,CONFIRM_DATE=GETDATE(),CONFIRM_TAG=1 WHERE ISNULL(CONFIRM_TAG,0)=0 AND {keyCondition};";
        await using (var confirmCommand = new SqlCommand(confirmSql, connection))
        {
            confirmCommand.Parameters.Add("@ConfirmPerson", SqlDbType.NVarChar, 50).Value = "SYSTEM";
            if (await confirmCommand.ExecuteNonQueryAsync(token) == 0)
            {
                return RecordSaveResult.Success(keyValues);
            }
        }
        if (definition.BusinessRule?.WorkflowSproc is { } sproc)
        {
            var result = await controlledSprocs.RunWorkflowAsync(definition.ModuleId, sproc, definition.MasterPkOrder, keyValues, true, token);
            if (!result.Success)
            {
                await RestoreConfirmStateAsync(connection, definition.MasterTable, keyCondition, originalState.Value, token);
                return RecordSaveResult.Failed(RecordAccessStatus.ValidationFailed, "WORKFLOW_FAILED",
                    result.Message ?? "自动批核失败。");
            }
        }
        await auditWriter.WriteEventAsync(connection, null, definition.ModuleId, string.Join(',', keyValues),
            "APPROVE", "自动批核", userId, "WORKBENCH_RECORD", result: 1, fieldChanges: null, token);
        logger.LogInformation("自动批核 module={ModuleId} key={Key} executor={User}", definition.ModuleId, string.Join(',', keyValues), userId);
        return RecordSaveResult.Success(keyValues);
    }

    /// <summary>
    /// 删除补偿守卫（ADR-005 §2，2026-08-23 全量收紧）：
    /// 结案单据禁止删除；任何已批核单据（含自动批核）禁止删除，需先解批；
    /// 已产生库存日志（INV_DEPOT_LOG）的单据禁止删除（解批回退后再删）。
    /// 返回 null 表示允许删除，否则返回阻止原因（RecordSaveResult）。
    /// </summary>
    public async Task<RecordSaveResult?> EnsureDeletionAllowedAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        WorkbenchDefinition definition,
        IReadOnlyList<string> keyValues,
        CancellationToken token)
    {
        var hasConfirm = await WorkbenchSql.ColumnExistsAsync(connection, transaction, definition.MasterTable, "CONFIRM_TAG", token);
        var hasFinished = await WorkbenchSql.ColumnExistsAsync(connection, transaction, definition.MasterTable, "FINISHED_TAG", token);
        if (!hasConfirm && !hasFinished)
        {
            return null;
        }

        var keyCondition = ControlledSprocInvoker.BuildKeyCondition(definition.MasterPkOrder, keyValues);
        var stateColumns = new List<string>();
        if (hasConfirm)
        {
            stateColumns.Add("CONFIRM_TAG");
        }
        if (hasFinished)
        {
            stateColumns.Add("FINISHED_TAG");
        }
        var sql = $"SELECT {string.Join(',', stateColumns.Select(column => $"ISNULL([{column}],0)"))} FROM dbo.[{definition.MasterTable}] WITH (NOLOCK) WHERE {keyCondition};";
        await using var command = new SqlCommand(sql, connection, transaction);
        await using var reader = await command.ExecuteReaderAsync(token);
        if (!await reader.ReadAsync(token))
        {
            return RecordSaveResult.Failed(RecordAccessStatus.NotFound, "RECORD_NOT_FOUND", "记录不存在。");
        }
        var confirmIndex = stateColumns.FindIndex(column => column == "CONFIRM_TAG");
        var finishedIndex = stateColumns.FindIndex(column => column == "FINISHED_TAG");
        var confirm = confirmIndex >= 0 && reader.GetBoolean(confirmIndex);
        var finished = finishedIndex >= 0 && reader.GetBoolean(finishedIndex);
        await reader.DisposeAsync();
        if (finished)
        {
            return RecordSaveResult.Failed(RecordAccessStatus.ValidationFailed, "FINISHED_RECORD_NOT_DELETABLE",
                "单据已结案，不能删除，请先取消结案。");
        }
        if (confirm)
        {
            return RecordSaveResult.Failed(RecordAccessStatus.ValidationFailed, "APPROVED_RECORD_NOT_DELETABLE",
                "单据已批核并产生业务副作用，不能删除，请先解批。");
        }
        // 兜底：CONFIRM=0 但库存日志仍引用本单（异常/部分回退态）→ 禁删
        if (definition.MasterPkOrder.Count >= 2 && keyValues.Count >= 2)
        {
            var typeColumn = definition.MasterPkOrder[0];
            var noColumn = definition.MasterPkOrder[1];
            const string logSql = """
                SELECT TOP 1 1 FROM dbo.INV_DEPOT_LOG WITH (NOLOCK)
                WHERE LTRIM(RTRIM(MUTUALITY_TYPE))=@t AND LTRIM(RTRIM(MUTUALITY_NO))=@n;
                """;
            await using var logCommand = new SqlCommand(logSql, connection, transaction);
            logCommand.Parameters.Add("@t", SqlDbType.NVarChar, 50).Value = keyValues[0];
            logCommand.Parameters.Add("@n", SqlDbType.NVarChar, 50).Value = keyValues[1];
            if (await logCommand.ExecuteScalarAsync(token) is not null)
            {
                return RecordSaveResult.Failed(RecordAccessStatus.ValidationFailed, "INVENTORY_LOG_EXISTS",
                    "单据已产生库存记录（INV_DEPOT_LOG），禁止删除；请先解批回退库存。");
            }
        }
        return null;
    }

    private async Task<RecordSaveResult> WorkflowCoreAsync(
        WorkbenchDefinition definition,
        IReadOnlyList<string> keyValues,
        bool approve,
        string employeeName,
        string userId,
        CancellationToken token,
        string? message = null)
    {
        var rule = definition.BusinessRule;
        if (rule?.WorkflowSproc is not { } sproc)
        {
            return RecordSaveResult.Failed(RecordAccessStatus.NotFound, "WORKFLOW_NOT_SUPPORTED", "该模块不支持批核操作。");
        }
        await using var connection = CreateConnection();
        await connection.OpenAsync(token);
        var keyCondition = ControlledSprocInvoker.BuildKeyCondition(definition.MasterPkOrder, keyValues);
        // 自动批核模块（MODULES.AUTO_APPROVE=1）：新增即已确认（SYSTEM），显式批核幂等返回成功，
        // 且不进入流程送审（用户语义：自动批核模块不走新增、审核模式）。
        if (approve && definition.AutoApprove)
        {
            var state = await ReadConfirmStateAsync(connection, definition.MasterTable, keyCondition, token);
            if (state is null)
            {
                return RecordSaveResult.Failed(RecordAccessStatus.NotFound, "RECORD_NOT_FOUND", "记录不存在。");
            }
            if (state.Value.Tag == true)
            {
                return RecordSaveResult.Success(keyValues);
            }
        }
        // 有流程定义的模块：批核即"送审"（启动审批链），单据保持未确认；
        // 无流程模块保持直接批核（对齐旧系统 P_WF_APPROVE_NOFLOW 语义）。
        else if (approve && await WorkflowEngine.HasFlowAsync(connection, definition.ModuleId, token))
        {
            return await workflowEngine.StartFlowAsync(definition, keyValues, employeeName, userId, token, message);
        }
        // 对齐旧系统 P_WF_APPROVE_NOFLOW：先更新主表确认状态（带守卫），再执行业务 SP。
        var originalState = await ReadConfirmStateAsync(connection, definition.MasterTable, keyCondition, token);
        if (originalState is null)
        {
            return RecordSaveResult.Failed(RecordAccessStatus.NotFound, "RECORD_NOT_FOUND", "记录不存在。");
        }
        // 解批前置校验（旧 P_WF_GET_NOBACK_STATE 的受控 C# 等价）
        if (!approve && originalState.Value.Tag == true)
        {
            var noBack = await CheckNotBackFieldsAsync(connection, definition, keyValues, token);
            if (noBack is not null)
            {
                return noBack;
            }
        }
        var confirmSql = approve
            ? $"UPDATE dbo.[{definition.MasterTable}] SET CONFIRM_PERSON=@ConfirmPerson,CONFIRM_DATE=GETDATE(),CONFIRM_TAG=1 WHERE ISNULL(CONFIRM_TAG,0)=0 AND {keyCondition};"
            : $"UPDATE dbo.[{definition.MasterTable}] SET CONFIRM_PERSON=@ConfirmPerson,CONFIRM_DATE=GETDATE(),CONFIRM_TAG=0 WHERE CONFIRM_TAG=1 AND {keyCondition};";
        await using (var confirmCommand = new SqlCommand(confirmSql, connection))
        {
            confirmCommand.Parameters.Add("@ConfirmPerson", SqlDbType.NVarChar, 50).Value = employeeName.Trim();
            var affected = await confirmCommand.ExecuteNonQueryAsync(token);
            if (affected == 0)
            {
                return RecordSaveResult.Failed(RecordAccessStatus.ValidationFailed, "WORKFLOW_STATE_CONFLICT",
                    approve ? "记录不存在或已批核，无法重复批核。" : "记录不存在或未批核，无法解批。");
            }
        }
        var result = await controlledSprocs.RunWorkflowAsync(definition.ModuleId, sproc, definition.MasterPkOrder, keyValues, approve, token);
        if (!result.Success)
        {
            await RestoreConfirmStateAsync(connection, definition.MasterTable, keyCondition, originalState.Value, token);
            return RecordSaveResult.Failed(RecordAccessStatus.ValidationFailed, "WORKFLOW_FAILED",
                result.Message ?? (approve ? "批核失败。" : "解批失败。"));
        }
        logger.LogInformation("统一表单{Action} module={ModuleId} key={Key}", approve ? "批核" : "解批", definition.ModuleId, string.Join(',', keyValues));
        await auditWriter.WriteEventAsync(connection, null, definition.ModuleId, string.Join(',', keyValues),
            approve ? "APPROVE" : "DEAPPROVE", approve ? "批核" : "解批", userId, "WORKBENCH_RECORD", result: 1, fieldChanges: null, token);
        return RecordSaveResult.Success(keyValues);
    }

    private async Task<RecordSaveResult> FinishCoreAsync(
        WorkbenchDefinition definition,
        IReadOnlyList<string> keyValues,
        bool finish,
        string employeeName,
        string userId,
        CancellationToken token)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(token);
        var keyCondition = ControlledSprocInvoker.BuildKeyCondition(definition.MasterPkOrder, keyValues);
        var hasTag = await WorkbenchSql.ColumnExistsAsync(connection, null, definition.MasterTable, "FINISHED_TAG", token);
        if (!hasTag)
        {
            return RecordSaveResult.Failed(RecordAccessStatus.NotFound, "ENDCASE_NOT_SUPPORTED", "该模块不支持结案操作。");
        }
        var hasPerson = await WorkbenchSql.ColumnExistsAsync(connection, null, definition.MasterTable, "FINISHED_PERSON", token);
        var hasDate = await WorkbenchSql.ColumnExistsAsync(connection, null, definition.MasterTable, "FINISHED_DATE", token);
        var sql = finish
            ? $"UPDATE dbo.[{definition.MasterTable}] SET FINISHED_TAG=1{(hasPerson ? ",FINISHED_PERSON=@Person" : string.Empty)}{(hasDate ? ",FINISHED_DATE=GETDATE()" : string.Empty)} WHERE ISNULL(FINISHED_TAG,0)=0 AND {keyCondition};"
            : $"UPDATE dbo.[{definition.MasterTable}] SET FINISHED_TAG=0{(hasPerson ? ",FINISHED_PERSON=@Person" : string.Empty)}{(hasDate ? ",FINISHED_DATE=GETDATE()" : string.Empty)} WHERE FINISHED_TAG=1 AND {keyCondition};";
        await using var command = new SqlCommand(sql, connection);
        if (hasPerson)
        {
            command.Parameters.Add("@Person", SqlDbType.NVarChar, 50).Value = employeeName.Trim();
        }
        var affected = await command.ExecuteNonQueryAsync(token);
        if (affected == 0)
        {
            return RecordSaveResult.Failed(RecordAccessStatus.ValidationFailed, "ENDCASE_STATE_CONFLICT",
                finish ? "记录不存在或已结案，无法重复结案。" : "记录不存在或未结案，无法取消结案。");
        }
        logger.LogInformation("统一表单{Action} module={ModuleId} key={Key}", finish ? "结案" : "取消结案", definition.ModuleId, string.Join(',', keyValues));
        await auditWriter.WriteEventAsync(connection, null, definition.ModuleId, string.Join(',', keyValues),
            finish ? "ENDCASE" : "UNENDCASE", finish ? "结案" : "取消结案", userId, "WORKBENCH_RECORD", result: 1, fieldChanges: null, token);
        return RecordSaveResult.Success(keyValues);
    }

    /// <summary>解批前置校验（旧 P_WF_GET_NOBACK_STATE 的受控 C# 等价，仅解批路径）。</summary>
    private async Task<RecordSaveResult?> CheckNotBackFieldsAsync(
        SqlConnection connection,
        WorkbenchDefinition definition,
        IReadOnlyList<string> keyValues,
        CancellationToken token)
    {
        string masterFields, detailFields;
        await using (var moduleCommand = new SqlCommand(
            "SELECT LTRIM(RTRIM(ISNULL(NOT_BACK_FIELDS_M,''))),LTRIM(RTRIM(ISNULL(NOT_BACK_FIELDS,''))) " +
            "FROM dbo.MODULES WITH (NOLOCK) WHERE M_IDX=@ModuleId;", connection))
        {
            moduleCommand.Parameters.Add("@ModuleId", SqlDbType.Int).Value = definition.ModuleId;
            await using var reader = await moduleCommand.ExecuteReaderAsync(token);
            if (!await reader.ReadAsync(token))
            {
                return null;
            }
            masterFields = reader.GetString(0);
            detailFields = reader.GetString(1);
        }

        if (string.IsNullOrWhiteSpace(masterFields) && string.IsNullOrWhiteSpace(detailFields))
        {
            return null;
        }

        var blocked = new List<string>();
        var keyCondition = ControlledSprocInvoker.BuildKeyCondition(definition.MasterPkOrder, keyValues);
        if (!string.IsNullOrWhiteSpace(masterFields) && !string.IsNullOrWhiteSpace(definition.MasterTable))
        {
            await CheckNotBackTableAsync(connection, definition.ModuleId, definition.MasterTable, masterFields, keyCondition, blocked, token);
        }
        if (!string.IsNullOrWhiteSpace(detailFields) && !string.IsNullOrWhiteSpace(definition.DetailTable))
        {
            await CheckNotBackTableAsync(connection, definition.ModuleId, definition.DetailTable!, detailFields, keyCondition, blocked, token);
        }

        if (blocked.Count == 0)
        {
            return null;
        }
        return RecordSaveResult.Failed(RecordAccessStatus.ValidationFailed, "NOBACK_BLOCKED",
            $"单据存在已发生的业务数据（{string.Join("、", blocked)}），不能解批。");
    }

    private async Task CheckNotBackTableAsync(
        SqlConnection connection,
        int moduleId,
        string table,
        string fieldsCsv,
        string keyCondition,
        List<string> blocked,
        CancellationToken token)
    {
        var fields = fieldsCsv.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Where(field => WorkbenchSql.Identifier.IsMatch(field)).ToArray();
        if (fields.Length == 0)
        {
            return;
        }

        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using (var columnCommand = new SqlCommand(
            "SELECT c.name FROM sys.columns c JOIN sys.objects o ON c.object_id=o.object_id AND o.type IN ('U','V') JOIN sys.schemas s ON o.schema_id=s.schema_id WHERE s.name=N'dbo' AND o.name=@Table ORDER BY c.column_id;", connection))
        {
            columnCommand.Parameters.Add("@Table", SqlDbType.NVarChar, 100).Value = table;
            await using var reader = await columnCommand.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token))
            {
                existing.Add(reader.GetString(0));
            }
        }
        var valid = fields.Where(field => existing.Contains(field)).ToArray();
        foreach (var missing in fields.Where(field => !existing.Contains(field)))
        {
            logger.LogWarning("解批前置校验字段物理不存在，已跳过 module={ModuleId} table={Table} field={Field}",
                moduleId, table, missing);
        }
        if (valid.Length == 0)
        {
            return;
        }

        var labels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var inClause = string.Join(",", valid.Select((_, index) => $"@f{index}"));
        await using (var descCommand = new SqlCommand(
            $"SELECT LTRIM(RTRIM(F_ID)),LTRIM(RTRIM(ISNULL(F_DESC,''))) FROM dbo.FIELDS WITH (NOLOCK) " +
            $"WHERE LTRIM(RTRIM(T_ID))=@Table AND F_ID IN ({inClause});", connection))
        {
            descCommand.Parameters.Add("@Table", SqlDbType.NVarChar, 100).Value = table;
            for (var i = 0; i < valid.Length; i++)
            {
                descCommand.Parameters.Add($"@f{i}", SqlDbType.NVarChar, 100).Value = valid[i];
            }
            await using var reader = await descCommand.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token))
            {
                labels[reader.GetString(0)] = reader.GetString(1);
            }
        }

        var predicates = string.Join(" OR ", valid.Select(field => $"CAST([{field}] AS varchar(100))>'0'"));
        await using var checkCommand = new SqlCommand(
            $"SELECT COUNT_BIG(1) FROM dbo.[{table}] WITH (NOLOCK) WHERE {keyCondition} AND ({predicates});", connection);
        var count = Convert.ToInt64(await checkCommand.ExecuteScalarAsync(token));
        if (count > 0)
        {
            blocked.AddRange(valid.Select(field => labels.TryGetValue(field, out var label) && label.Length > 0 ? label : field));
        }
    }

    private static async Task<(bool? Tag, string? Person, DateTime? Date)?> ReadConfirmStateAsync(
        SqlConnection connection, string table, string keyCondition, CancellationToken token)
    {
        var sql = $"SELECT CONFIRM_TAG,CONFIRM_PERSON,CONFIRM_DATE FROM dbo.[{table}] WITH (NOLOCK) WHERE {keyCondition};";
        await using var command = new SqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(token);
        if (!await reader.ReadAsync(token))
        {
            return null;
        }
        var tag = reader.IsDBNull(0) ? (bool?)null : reader.GetBoolean(0);
        var person = reader.IsDBNull(1) ? null : reader.GetString(1);
        var date = reader.IsDBNull(2) ? (DateTime?)null : reader.GetDateTime(2);
        return (tag, person, date);
    }

    private static async Task RestoreConfirmStateAsync(
        SqlConnection connection, string table, string keyCondition, (bool? Tag, string? Person, DateTime? Date) state, CancellationToken token)
    {
        var sql = $"UPDATE dbo.[{table}] SET CONFIRM_TAG=@Tag,CONFIRM_PERSON=@Person,CONFIRM_DATE=@Date WHERE {keyCondition};";
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@Tag", SqlDbType.Bit).Value = (object?)state.Tag ?? DBNull.Value;
        command.Parameters.Add("@Person", SqlDbType.NVarChar, 50).Value = (object?)state.Person ?? DBNull.Value;
        command.Parameters.Add("@Date", SqlDbType.DateTime).Value = (object?)state.Date ?? DBNull.Value;
        await command.ExecuteNonQueryAsync(token);
    }

    private async Task<WorkbenchIdempotencyRecord?> ClaimIdempotencyAsync(int moduleId, string action, string key, CancellationToken token)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(token);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(token);
        try
        {
            var existing = await idempotency.TryClaimAsync(connection, transaction, key, moduleId, action, token);
            await transaction.CommitAsync(token);
            return existing;
        }
        catch
        {
            await transaction.RollbackAsync(token);
            throw;
        }
    }

    private async Task CompleteOrReleaseIdempotencyAsync(
        string? key, string action, int moduleId, RecordSaveResult result, IReadOnlyList<string> keyValues, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return;
        }
        await using var connection = CreateConnection();
        await connection.OpenAsync(token);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(token);
        try
        {
            if (result.Status == RecordAccessStatus.Ok)
            {
                await idempotency.CompleteAsync(connection, transaction, key, string.Join(',', keyValues), result.FlowStarted, token);
            }
            else
            {
                await idempotency.ReleaseAsync(connection, transaction, key, token);
            }
            await transaction.CommitAsync(token);
        }
        catch
        {
            await transaction.RollbackAsync(token);
            throw;
        }
    }

    private SqlConnection CreateConnection() => connections.Create();
}
