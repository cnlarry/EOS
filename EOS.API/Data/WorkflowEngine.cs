using Microsoft.Data.SqlClient;
using System.Data;

namespace EOS.API.Data;

/// <summary>
/// 通用工作流审批链（旧 P_WF_RUN / P_WF_APPROVE 的受控 C# 等价，第一期：顺序多级审批）。
/// 复用旧 WF_* 表：WFFORM/WFFORM_FLOW（流程定义）、WF_MONITOR（运行实例）、
/// WF_MYTASK（待办任务）、WF_MYTASK_LOG（审批日志）、WF_APPROVE（单据批核历史）。
/// 无流程模块保持"直接批核"（DocumentWorkbenchRepository.WorkflowAsync）不变。
/// 第一期不支持：EXEC_CONDITION/PERSON_CONDITION 动态条件（直接拒绝而非拼接 SQL）、
/// 会签百分比（IS_SIGN/PASS_PERCENT 字段随任务落库但按顺序审批推进）、跳转（jump）。
/// 代理（CAN_SIR_AGENCY）按旧 f_get_sirs 校验。
/// </summary>
public sealed class WorkflowEngine(
    DbConnectionFactory connections,
    ControlledSprocInvoker controlledSprocs,
    ILogger<WorkflowEngine> logger)
{
    /// <summary>模块是否配置了流程（WFFORM + 至少一步 WFFORM_FLOW）。</summary>
    public static async Task<bool> HasFlowAsync(SqlConnection connection, int moduleId, CancellationToken token)
    {
        const string sql = """
            SELECT TOP 1 1 FROM dbo.WFFORM wf WITH (NOLOCK)
            WHERE wf.WF_M_IDX=@ModuleId
              AND EXISTS (SELECT 1 FROM dbo.WFFORM_FLOW f WITH (NOLOCK) WHERE f.WF_M_IDX=wf.WF_M_IDX);
            """;
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@ModuleId", SqlDbType.Int).Value = moduleId;
        return await command.ExecuteScalarAsync(token) is not null;
    }

    /// <summary>
    /// 启动流程：建/取 WF_MONITOR，删除旧任务，按 WFFORM_FLOW.SORT_NO 重建 WF_MYTASK。
    /// 含动态条件的流程（EXEC_CONDITION/PERSON_CONDITION 非空）第一期拒绝启动。
    /// </summary>
    public async Task<RecordSaveResult> StartFlowAsync(
        WorkbenchDefinition definition,
        IReadOnlyList<string> keyValues,
        string employeeName,
        CancellationToken token)
    {
        await using var connection = connections.Create();
        await connection.OpenAsync(token);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(token);
        var keyCondition = ControlledSprocInvoker.BuildKeyCondition(definition.MasterPkOrder, keyValues);

        long wfId;
        await using (var select = new SqlCommand("""
            SELECT WF_ID FROM dbo.WF_MONITOR WITH (UPDLOCK, HOLDLOCK)
            WHERE WF_M_IDX=@ModuleId AND KEY_VALUE=@KeyValue;
            """, connection, transaction))
        {
            select.Parameters.Add("@ModuleId", SqlDbType.Int).Value = definition.ModuleId;
            select.Parameters.Add("@KeyValue", SqlDbType.VarChar, 200).Value = keyCondition;
            var existing = await select.ExecuteScalarAsync(token);
            if (existing is not null)
            {
                wfId = Convert.ToInt64(existing);
            }
            else
            {
                await using var insert = new SqlCommand("""
                    INSERT INTO dbo.WF_MONITOR (WF_M_IDX, KEY_VALUE, KEY_VALUE_DESC, WF_STATE, UPDATE_SUBFLOW)
                    VALUES (@ModuleId, @KeyValue, @KeyValueDesc, '0', '');
                    SELECT CAST(SCOPE_IDENTITY() AS bigint);
                    """, connection, transaction);
                insert.Parameters.Add("@ModuleId", SqlDbType.Int).Value = definition.ModuleId;
                insert.Parameters.Add("@KeyValue", SqlDbType.VarChar, 200).Value = keyCondition;
                insert.Parameters.Add("@KeyValueDesc", SqlDbType.VarChar, 300).Value =
                    keyCondition.Length > 300 ? keyCondition[..300] : keyCondition;
                wfId = Convert.ToInt64(await insert.ExecuteScalarAsync(token));
            }
        }

        await using (var clear = new SqlCommand("DELETE FROM dbo.WF_MYTASK WHERE WF_ID=@WfId;", connection, transaction))
        {
            clear.Parameters.Add("@WfId", SqlDbType.BigInt).Value = wfId;
            await clear.ExecuteNonQueryAsync(token);
        }

        var steps = new List<(string SortNo, string Desc, string[] People, string ExecCondition,
            string PersonCondition, bool ApprovePower, bool ForwardPower, bool IsAutoExec, bool IsSign,
            bool IsEffect, bool PreMustUnder, bool CanSirAgency, bool IsMustSign)>();
        await using (var stepCommand = new SqlCommand("""
            SELECT SORT_NO, LTRIM(RTRIM(ISNULL(SUBFLOW_DESC,''))), LTRIM(RTRIM(ISNULL(EXEC_PERSON,''))),
                   LTRIM(RTRIM(ISNULL(EXEC_CONDITION,''))), LTRIM(RTRIM(ISNULL(PERSON_CONDITION,''))),
                   ISNULL(CAST(PERSON_APP_POWER AS int),0), ISNULL(CAST(PERSON_FORWARD_POWER AS int),0),
                   ISNULL(CAST(IS_AUTO_EXEC AS int),0), ISNULL(CAST(IS_SIGN AS int),0),
                   ISNULL(CAST(IS_EFFECT AS int),0), ISNULL(CAST(PRE_MUST_UNDER AS int),0),
                   ISNULL(CAST(CAN_SIR_AGENCY AS int),0), LTRIM(RTRIM(ISNULL(MUST_SIGNER,'')))
            FROM dbo.WFFORM_FLOW WITH (NOLOCK)
            WHERE WF_M_IDX=@ModuleId ORDER BY SORT_NO;
            """, connection, transaction))
        {
            stepCommand.Parameters.Add("@ModuleId", SqlDbType.Int).Value = definition.ModuleId;
            await using var reader = await stepCommand.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token))
            {
                steps.Add((
                    reader.GetString(0).Trim(),
                    reader.GetString(1),
                    reader.GetString(2).Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries),
                    reader.GetString(3),
                    reader.GetString(4),
                    reader.GetInt32(5) == 1,
                    reader.GetInt32(6) == 1,
                    reader.GetInt32(7) == 1,
                    reader.GetInt32(8) == 1,
                    reader.GetInt32(9) == 1,
                    reader.GetInt32(10) == 1,
                    reader.GetInt32(11) == 1,
                    !string.IsNullOrWhiteSpace(reader.GetString(12))));
            }
        }

        if (steps.Count == 0)
            return RecordSaveResult.Failed(RecordAccessStatus.ValidationFailed, "FLOW_NO_STEPS", "流程未配置审批步骤。");
        if (steps.Any(step => !string.IsNullOrWhiteSpace(step.ExecCondition) || !string.IsNullOrWhiteSpace(step.PersonCondition)))
            return RecordSaveResult.Failed(RecordAccessStatus.ValidationFailed, "FLOW_CONDITION_UNSUPPORTED",
                "流程步骤含动态条件（EXEC_CONDITION/PERSON_CONDITION），第一期不支持，已拒绝启动。");

        var first = true;
        const string insertSql = """
            INSERT INTO dbo.WF_MYTASK
                (WF_ID, SUBFLOW_NO, SUBFLOW_DESC, APPROVER, APPROVE_TAG, APPROVE_STATE,
                 IS_AUTO_EXEC, IS_SIGN, IS_MUST_SIGN, PASS_PERCENT, IS_EFFECT, PRE_MUST_UNDER,
                 CAN_SIR_AGENCY, APPROVE_POWER, FORWARD_POWER, IS_CURRENT)
            VALUES
                (@WfId, @SubflowNo, @SubflowDesc, @Approver, 0, '',
                 @IsAutoExec, @IsSign, @IsMustSign, @PassPercent, @IsEffect, @PreMustUnder,
                 @CanSirAgency, @ApprovePower, @ForwardPower, @IsCurrent);
            """;
        foreach (var step in steps)
        {
            foreach (var person in step.People)
            {
                await using var taskCommand = new SqlCommand(insertSql, connection, transaction);
                taskCommand.Parameters.Add("@WfId", SqlDbType.BigInt).Value = wfId;
                taskCommand.Parameters.Add("@SubflowNo", SqlDbType.Char, 3).Value = step.SortNo;
                taskCommand.Parameters.Add("@SubflowDesc", SqlDbType.VarChar, 50).Value = step.Desc;
                taskCommand.Parameters.Add("@Approver", SqlDbType.VarChar, 20).Value = person;
                taskCommand.Parameters.Add("@IsAutoExec", SqlDbType.Bit).Value = step.IsAutoExec;
                taskCommand.Parameters.Add("@IsSign", SqlDbType.Bit).Value = step.IsSign;
                taskCommand.Parameters.Add("@IsMustSign", SqlDbType.Bit).Value = step.IsMustSign;
                taskCommand.Parameters.Add("@PassPercent", SqlDbType.Int).Value = 100;
                taskCommand.Parameters.Add("@IsEffect", SqlDbType.Bit).Value = step.IsEffect;
                taskCommand.Parameters.Add("@PreMustUnder", SqlDbType.Bit).Value = step.PreMustUnder;
                taskCommand.Parameters.Add("@CanSirAgency", SqlDbType.Bit).Value = step.CanSirAgency;
                taskCommand.Parameters.Add("@ApprovePower", SqlDbType.Bit).Value = step.ApprovePower;
                taskCommand.Parameters.Add("@ForwardPower", SqlDbType.Bit).Value = step.ForwardPower;
                taskCommand.Parameters.Add("@IsCurrent", SqlDbType.Bit).Value = first;
                await taskCommand.ExecuteNonQueryAsync(token);
            }
            first = false;
        }

        await transaction.CommitAsync(token);
        logger.LogInformation("流程启动 module={ModuleId} key={Key} steps={Steps}",
            definition.ModuleId, string.Join(',', keyValues), steps.Count);
        return RecordSaveResult.SuccessFlowStarted(keyValues);
    }

    /// <summary>
    /// 任务审批（approveState='Y'/'N'）。末步同意：落主表 CONFIRM_TAG + 执行 WorkflowSproc 副作用 + WF_APPROVE 历史。
    /// </summary>
    public async Task<(bool Success, string? ErrorCode, string? ErrorMessage, bool FlowFinished, string? Message)> ApproveTaskAsync(
        long myTaskId,
        string userId,
        char approveState,
        string? message,
        CancellationToken token)
    {
        await using var connection = connections.Create();
        await connection.OpenAsync(token);

        long wfId;
        string subflowNo;
        // 阶段 A：任务审批（更新任务 + 日志 + 推进下一步），先提交释放锁
        string subflowDesc;
        string? currentState;
        bool hasNext;
        await using (var transaction = (SqlTransaction)await connection.BeginTransactionAsync(token))
        {
            string approver;
            bool canSirAgency;
            await using (var taskCommand = new SqlCommand("""
                SELECT WF_ID, SUBFLOW_NO, LTRIM(RTRIM(ISNULL(APPROVER,''))), ISNULL(CAN_SIR_AGENCY,0),
                       LTRIM(RTRIM(ISNULL(APPROVE_STATE,''))), LTRIM(RTRIM(ISNULL(SUBFLOW_DESC,'')))
                FROM dbo.WF_MYTASK WITH (UPDLOCK, HOLDLOCK) WHERE MYTASK_ID=@MyTaskId;
                """, connection, transaction))
            {
                taskCommand.Parameters.Add("@MyTaskId", SqlDbType.BigInt).Value = myTaskId;
                await using var reader = await taskCommand.ExecuteReaderAsync(token);
                if (!await reader.ReadAsync(token))
                    return (false, "TASK_NOT_FOUND", "审批任务不存在。", false, null);
                wfId = reader.GetInt64(0);
                subflowNo = reader.GetString(1).Trim();
                approver = reader.GetString(2);
                canSirAgency = reader.GetBoolean(3);
                currentState = reader.GetString(4);
                subflowDesc = reader.GetString(5);
            }

            if (!string.IsNullOrEmpty(currentState))
                return (false, "TASK_ALREADY_PROCESSED", "该审批任务已处理，不能重复审批。", false, null);

            var authorized = approver.Equals(userId, StringComparison.OrdinalIgnoreCase) || userId == "SYSTEM";
            if (!authorized && canSirAgency)
            {
                await using var agencyCommand = new SqlCommand("""
                    SELECT TOP 1 1 FROM dbo.f_get_sirs(@Approver) WHERE LTRIM(RTRIM(user_id))=@UserId;
                    """, connection, transaction);
                agencyCommand.Parameters.Add("@Approver", SqlDbType.VarChar, 20).Value = approver;
                agencyCommand.Parameters.Add("@UserId", SqlDbType.VarChar, 20).Value = userId;
                authorized = await agencyCommand.ExecuteScalarAsync(token) is not null;
            }
            if (!authorized)
                return (false, "TASK_NOT_AUTHORIZED", "当前用户不是该审批步骤的审批人，无权处理。", false, null);

            var now = DateTime.Now;
            await using (var update = new SqlCommand("""
                UPDATE dbo.WF_MYTASK SET APP_EMP_ID=@UserId, APPROVE_MSG=@Msg, APPROVE_TAG=1,
                    APPROVE_DATE=@Now, APPROVE_STATE=@State
                WHERE MYTASK_ID=@MyTaskId;
                """, connection, transaction))
            {
                update.Parameters.Add("@UserId", SqlDbType.VarChar, 20).Value = userId;
                update.Parameters.Add("@Msg", SqlDbType.VarChar, 3000).Value = (object?)message ?? DBNull.Value;
                update.Parameters.Add("@Now", SqlDbType.DateTime).Value = now;
                update.Parameters.Add("@State", SqlDbType.Char, 1).Value = approveState.ToString();
                update.Parameters.Add("@MyTaskId", SqlDbType.BigInt).Value = myTaskId;
                await update.ExecuteNonQueryAsync(token);
            }
            await using (var log = new SqlCommand("""
                INSERT INTO dbo.WF_MYTASK_LOG (WF_ID, MYTASK_ID, SUBFLOW_NO, SUBFLOW_DESC, APP_EMP_ID,
                    APPROVE_DATE, APPROVE_STATE, APPROVE_MSG)
                VALUES (@WfId, @MyTaskId, @SubflowNo, @SubflowDesc, @UserId, @Now, @State, @Msg);
                """, connection, transaction))
            {
                log.Parameters.Add("@WfId", SqlDbType.BigInt).Value = wfId;
                log.Parameters.Add("@MyTaskId", SqlDbType.BigInt).Value = myTaskId;
                log.Parameters.Add("@SubflowNo", SqlDbType.Char, 3).Value = subflowNo;
                log.Parameters.Add("@SubflowDesc", SqlDbType.VarChar, 50).Value = subflowDesc;
                log.Parameters.Add("@UserId", SqlDbType.VarChar, 20).Value = userId;
                log.Parameters.Add("@Now", SqlDbType.DateTime).Value = now;
                log.Parameters.Add("@State", SqlDbType.Char, 1).Value = approveState.ToString();
                log.Parameters.Add("@Msg", SqlDbType.VarChar, 3000).Value = (object?)message ?? DBNull.Value;
                await log.ExecuteNonQueryAsync(token);
            }

            if (approveState != 'Y')
            {
                await transaction.CommitAsync(token);
                return (true, null, null, false, "已驳回，流程终止（单据未确认）。");
            }

            // 找下一步
            hasNext = false;
            await using (var nextCommand = new SqlCommand("""
                SELECT TOP 1 SORT_NO FROM dbo.WFFORM_FLOW WITH (NOLOCK)
                WHERE WF_M_IDX=(SELECT WF_M_IDX FROM dbo.WF_MONITOR WHERE WF_ID=@WfId) AND SORT_NO>@SubflowNo
                ORDER BY SORT_NO;
                """, connection, transaction))
            {
                nextCommand.Parameters.Add("@WfId", SqlDbType.BigInt).Value = wfId;
                nextCommand.Parameters.Add("@SubflowNo", SqlDbType.Char, 3).Value = subflowNo;
                var next = await nextCommand.ExecuteScalarAsync(token);
                if (next is not null)
                {
                    hasNext = true;
                    await using var activate = new SqlCommand("""
                        UPDATE dbo.WF_MYTASK SET IS_CURRENT=0 WHERE WF_ID=@WfId;
                        UPDATE dbo.WF_MYTASK SET IS_CURRENT=1 WHERE WF_ID=@WfId AND SUBFLOW_NO=@Next;
                        """, connection, transaction);
                    activate.Parameters.Add("@WfId", SqlDbType.BigInt).Value = wfId;
                    activate.Parameters.Add("@Next", SqlDbType.Char, 3).Value = (string)next;
                    await activate.ExecuteNonQueryAsync(token);
                }
            }
            await transaction.CommitAsync(token);
        }

        if (hasNext)
            return (true, null, null, false, "审批通过，流程进入下一步。");

        // 阶段 B（末步）：主表确认 + WorkflowSproc 副作用 + WF_APPROVE 历史。
        // 任务锁已释放，SP 在独立连接执行，避免跨连接锁冲突/死锁。
        {
            // 末步通过：主表确认 + WorkflowSproc 副作用 + WF_APPROVE 历史
            int moduleId;
            string masterTable;
            string keyCondition;
            string? updateSproc;
            string? title;
            await using (var monitorCommand = new SqlCommand("""
                SELECT WF_M_IDX, KEY_VALUE
                FROM dbo.WF_MONITOR WITH (NOLOCK) WHERE WF_ID=@WfId;
                """, connection))
            {
                monitorCommand.Parameters.Add("@WfId", SqlDbType.BigInt).Value = wfId;
                await using var reader = await monitorCommand.ExecuteReaderAsync(token);
                if (!await reader.ReadAsync(token))
                    return (false, "FLOW_NOT_FOUND", "流程实例不存在。", false, null);
                moduleId = reader.GetInt32(0);
                keyCondition = reader.GetString(1);
            }
            await using (var moduleCommand = new SqlCommand("""
                SELECT LTRIM(RTRIM(ISNULL(MASTER_TABLE,''))), LTRIM(RTRIM(ISNULL(UPDATE_SP,''))),
                       LTRIM(RTRIM(ISNULL(M_DESC,'')))
                FROM dbo.MODULES WITH (NOLOCK) WHERE M_IDX=@ModuleId;
                """, connection))
            {
                moduleCommand.Parameters.Add("@ModuleId", SqlDbType.Int).Value = moduleId;
                await using var reader = await moduleCommand.ExecuteReaderAsync(token);
                if (!await reader.ReadAsync(token))
                    return (false, "MODULE_NOT_FOUND", "模块不存在。", false, null);
                masterTable = reader.GetString(0);
                updateSproc = reader.IsDBNull(1) ? null : reader.GetString(1);
                title = reader.GetString(2);
            }

            if (!string.IsNullOrWhiteSpace(updateSproc))
            {
                var sprocResult = await controlledSprocs.RunWorkflowAsync(
                    moduleId, updateSproc, Array.Empty<string>(), Array.Empty<string>(), true, token, keyCondition);
                if (!sprocResult.Success)
                    return (false, "WORKFLOW_FAILED", sprocResult.Message ?? "末步业务处理失败。", false, null);
            }

            await using var finalTransaction = (SqlTransaction)await connection.BeginTransactionAsync(token);
            try
            {
                await using var confirm = new SqlCommand(
                    $"UPDATE dbo.[{masterTable}] SET CONFIRM_PERSON=@Person, CONFIRM_DATE=GETDATE(), CONFIRM_TAG=1 " +
                    $"WHERE ISNULL(CONFIRM_TAG,0)=0 AND {keyCondition};", connection, finalTransaction);
                confirm.Parameters.Add("@Person", SqlDbType.NVarChar, 50).Value = userId;
                var affected = await confirm.ExecuteNonQueryAsync(token);
                if (affected == 0)
                    return (false, "WORKFLOW_STATE_CONFLICT", "记录不存在或已批核，无法重复批核。", false, null);

                await using var history = new SqlCommand("""
                    INSERT INTO dbo.WF_APPROVE (M_IDX, KEY_VALUE, KEY_VALUE_DESC)
                    VALUES (@ModuleId, @KeyValue, @KeyValueDesc);
                    """, connection, finalTransaction);
                history.Parameters.Add("@ModuleId", SqlDbType.Int).Value = moduleId;
                history.Parameters.Add("@KeyValue", SqlDbType.VarChar, 200).Value = keyCondition;
                history.Parameters.Add("@KeyValueDesc", SqlDbType.VarChar, 300).Value =
                    (title ?? string.Empty) + " 流程审批完成";
                await history.ExecuteNonQueryAsync(token);
                await finalTransaction.CommitAsync(token);
            }
            catch
            {
                await finalTransaction.RollbackAsync(token);
                throw;
            }
            logger.LogInformation("流程末步通过 module={ModuleId} key={Key}", moduleId, keyCondition);
            return (true, null, null, true, "流程审批完成，单据已确认。");
        }
    }

    /// <summary>按单审批历史时间线（WF_MYTASK_LOG + WF_APPROVE）。</summary>
    public async Task<IReadOnlyList<object>> GetHistoryAsync(
        SqlConnection connection,
        int moduleId,
        IReadOnlyList<string> pkColumns,
        IReadOnlyList<string> keyValues,
        CancellationToken token)
    {
        var keyCondition = ControlledSprocInvoker.BuildKeyCondition(pkColumns, keyValues);
        var result = new List<object>();
        await using var command = new SqlCommand("""
            SELECT L.SUBFLOW_NO, L.SUBFLOW_DESC, L.APP_EMP_ID, L.APPROVE_STATE, L.APPROVE_MSG,
                   CONVERT(varchar(19), L.APPROVE_DATE, 120) AS APPROVE_DATE
            FROM dbo.WF_MYTASK_LOG L
            JOIN dbo.WF_MONITOR M ON M.WF_ID=L.WF_ID
            WHERE M.WF_M_IDX=@ModuleId AND M.KEY_VALUE=@KeyValue
            ORDER BY L.APPROVE_DATE;
            """, connection);
        command.Parameters.Add("@ModuleId", SqlDbType.Int).Value = moduleId;
        command.Parameters.Add("@KeyValue", SqlDbType.VarChar, 200).Value = keyCondition;
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
            result.Add(new
            {
                Kind = "task",
                Step = reader.GetString(0).Trim(),
                StepDesc = reader.GetString(1),
                Approver = reader.IsDBNull(2) ? null : reader.GetString(2),
                State = reader.GetString(3).Trim(),
                Message = reader.IsDBNull(4) ? null : reader.GetString(4),
                Date = reader.GetString(5),
            });
        await reader.DisposeAsync();
        await using var confirmCommand = new SqlCommand("""
            SELECT KEY_VALUE_DESC, LAST_UPDATE_BY
            FROM dbo.WF_APPROVE WHERE M_IDX=@ModuleId AND KEY_VALUE=@KeyValue;
            """, connection);
        confirmCommand.Parameters.Add("@ModuleId", SqlDbType.Int).Value = moduleId;
        confirmCommand.Parameters.Add("@KeyValue", SqlDbType.VarChar, 200).Value = keyCondition;
        await using var confirmReader = await confirmCommand.ExecuteReaderAsync(token);
        while (await confirmReader.ReadAsync(token))
            result.Add(new
            {
                Kind = "confirm",
                Step = "",
                StepDesc = confirmReader.IsDBNull(0) ? null : confirmReader.GetString(0),
                Approver = confirmReader.IsDBNull(1) ? null : confirmReader.GetString(1),
                State = "Y",
                Message = "流程审批完成，单据已确认",
                Date = "",
            });
        return result;
    }

    /// <summary>当前用户真实待办（WF_MYTASK 未处理任务）。</summary>
    public async Task<IReadOnlyList<object>> GetMyFlowTasksAsync(
        SqlConnection connection,
        string userId,
        CancellationToken token)
    {
        var result = new List<object>();
        await using var command = new SqlCommand("""
            SELECT T.MYTASK_ID, T.WF_ID, T.SUBFLOW_NO, T.SUBFLOW_DESC, M.WF_M_IDX, M.KEY_VALUE,
                   LTRIM(RTRIM(ISNULL((SELECT M_DESC FROM dbo.MODULES WHERE M_IDX=M.WF_M_IDX),'')))
            FROM dbo.WF_MYTASK T WITH (NOLOCK)
            JOIN dbo.WF_MONITOR M WITH (NOLOCK) ON M.WF_ID=T.WF_ID
            WHERE LTRIM(RTRIM(T.APPROVER))=@UserId AND ISNULL(T.APPROVE_STATE,'')=''
              AND ISNULL(T.APPROVE_TAG,0)=0
            ORDER BY T.MYTASK_ID;
            """, connection);
        command.Parameters.Add("@UserId", SqlDbType.VarChar, 20).Value = userId;
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
            result.Add(new
            {
                MyTaskId = reader.GetInt64(0),
                WfId = reader.GetInt64(1),
                Step = reader.GetString(2).Trim(),
                StepDesc = reader.GetString(3),
                ModuleId = reader.GetInt32(4),
                KeyValue = reader.GetString(5),
                Title = reader.GetString(6),
            });
        return result;
    }
}
