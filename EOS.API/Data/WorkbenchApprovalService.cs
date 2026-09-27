using System.Data;
using Microsoft.Data.SqlClient;

using EOS.API.Data.Effects;
using EOS.API.Data.Inventory;
using EOS.API.Models;

namespace EOS.API.Data;

/// <summary>
/// 工作台审批服务：
/// 批核/解批/结案/未结案/自动批核 + 状态机副作用 + 幂等 + 删除补偿守卫。
/// 补偿语义（落定）：
/// - 结案单据（FINISHED_TAG=1）禁止删除，需先取消结案；
/// - 任何已批核单据（CONFIRM_TAG=1，含自动批核模块）禁止删除，需先解批；
/// - 已产生库存流水的单据禁止删除（解批回退库存后再删）。
/// 批核副作用 SP 自带事务（自动提交），状态守卫（CONFIRM_TAG/FINISHED_TAG）防重复副作用，
/// 幂等键提供顺序重放保护；解批前置 NOBACK 校验原样保留。
/// </summary>
public sealed class WorkbenchApprovalService(
    DbConnectionFactory connections,
    WorkbenchAuditWriter auditWriter,
    WorkflowEngine workflowEngine,
    EffectEngineInvoker effectEngine,
    WorkbenchIdempotency idempotency,
    ILogger<WorkbenchApprovalService> logger)
{
    /// <summary>
    /// 单据批核/解批。
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
    /// 结案/取消结案。
    /// 语义：更新主表 FINISHED_TAG/FINISHED_PERSON/FINISHED_DATE，并同步该单明细表的 FINISHED_TAG
    /// （单据级结案即"全部结案"），随后跑 ENDCASE / UNENDCASE 效果链——三者同一事务。
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
    /// 自动批核（MODULES.AUTO_APPROVE=1）：保存成功后立即进入批核生效——
    /// 与手工批核**共用同一条生效链**，差异只在触发入口（保存动作 vs 按钮）与审计摘要，
    /// 运行时代码不按模块号分支：引擎接管走 <see cref="RunEngineApprovalAsync"/>，
    /// 未接管且登记了批核过程时走过渡桥，两者皆无即纯状态翻转。
    /// 经办人 = 保存人（确认人/日期与该单据的建立人/修改人一致）：谁保存的这条记录，
    /// 就是谁让它生效的；"是自动还是手工批核"记在审计摘要里，不再靠 SYSTEM 占位区分。
    /// 失败不回滚保存：以结果回传前端提示「已保存，但自动批核失败」，单据停在未批核态可重试。
    /// </summary>
    public async Task<RecordSaveResult> AutoApproveAsync(
        WorkbenchDefinition definition,
        IReadOnlyList<string> keyValues,
        string confirmPerson,
        string userId,
        CancellationToken token)
    {
        if (effectEngine.IsEnabledFor(definition))
        {
            return await RunEngineApprovalAsync(definition, keyValues, approve: true, confirmPerson, userId, token,
                idempotentWhenSettled: true, auditSummary: "自动批核");
        }

        // 过渡桥：旧批核过程在独立连接上执行，无法与状态翻转同事务，失败按补偿还原。
        await using var connection = CreateConnection();
        await connection.OpenAsync(token);
        // 缺列不再静默返回成功（调用方会误以为已批核），而是显式失败并引导补列。
        if (!await WorkbenchSql.ColumnExistsAsync(connection, null, definition.MasterTable, "CONFIRM_TAG", token))
        {
            return RecordSaveResult.Failed(RecordAccessStatus.ValidationFailed, "LIFECYCLE_COLUMN_MISSING",
                $"该模块启用自动批核，但主表 {definition.MasterTable} 缺少 CONFIRM_TAG 列：请补列后重发布，或关闭自动批核。");
        }
        var keyWhere = WorkbenchSql.BuildKeyWhere(definition.MasterPkOrder, keyValues);
        var originalState = await ReadConfirmStateAsync(connection, definition.MasterTable, definition.MasterPkOrder, keyValues, token);
        if (originalState is null)
        {
            return RecordSaveResult.Failed(RecordAccessStatus.NotFound, "RECORD_NOT_FOUND", "记录不存在。");
        }
        if (originalState.Value.Tag == true)
        {
            return RecordSaveResult.Success(keyValues);
        }
        var confirmSql = $"UPDATE dbo.[{definition.MasterTable}] SET CONFIRM_PERSON=@ConfirmPerson,CONFIRM_DATE=GETDATE(),CONFIRM_TAG=1 WHERE ISNULL(CONFIRM_TAG,0)=0 AND {keyWhere};";
        await using (var confirmCommand = new SqlCommand(confirmSql, connection))
        {
            confirmCommand.Parameters.Add("@ConfirmPerson", SqlDbType.NVarChar, 50).Value = confirmPerson.Trim();
            WorkbenchSql.AddKeyParameters(confirmCommand, definition.MasterPkOrder, keyValues);
            if (await confirmCommand.ExecuteNonQueryAsync(token) == 0)
            {
                return RecordSaveResult.Success(keyValues);
            }
        }
        await auditWriter.WriteEventAsync(connection, null, definition.ModuleId, string.Join(',', keyValues),
            "APPROVE", "自动批核", userId, "WORKBENCH_RECORD", result: 1,
            fieldChanges: ConfirmStateChanges(
                originalState,
                await ReadConfirmStateAsync(connection, definition.MasterTable, definition.MasterPkOrder, keyValues, token)),
            token);
        logger.LogInformation("自动批核（无副作用） module={ModuleId} key={Key} executor={User}", definition.ModuleId, string.Join(',', keyValues), userId);
        return RecordSaveResult.Success(keyValues);
    }

    /// <summary>
    /// 效果引擎接管后的批核/解批生效链——自动批核与手工批核**共用这一条**：
    /// 同一事务内依次执行「该阶段校验闸 → 守卫翻转确认状态 → 效果链 → 审计」，
    /// 任一步失败整链回滚：确认状态自然还原、效果零写入（的阻断语义，无需补偿写）。
    /// 幂等与并发安全：状态翻转带 `ISNULL(CONFIRM_TAG,0)=0` 守卫，重复触发/并发触发时
    /// 只有第一个写者会执行效果链，其余直接按"已生效"成功返回，不会重复累计。
    /// </summary>
    private async Task<RecordSaveResult> RunEngineApprovalAsync(
        WorkbenchDefinition definition,
        IReadOnlyList<string> keyValues,
        bool approve,
        string confirmPerson,
        string userId,
        CancellationToken token,
        // 保存触发的自动批核是幂等的（重复保存直接成功）；用户手工点批核则保持
        // 「已批核 → WORKFLOW_STATE_CONFLICT」的显式冲突提示，两者对重复触发的口径不同。
        bool idempotentWhenSettled = false,
        // 审计摘要：保留"自动批核 / 批核"的区分——经办人改成保存人之后，
        // 状态列不再承担"是不是自动批的"这个信息，改由审计承载。
        string? auditSummary = null)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(token);
        if (await CheckApprovalPreconditionsAsync(connection, definition, keyValues, approve, token, idempotentWhenSettled) is { } precondition)
        {
            return precondition;
        }

        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(token);
        IReadOnlyList<SaveWarning>? warnings = null;
        try
        {
            var outcome = await RunApprovalCoreAsync(
                connection, transaction, definition, keyValues, approve, confirmPerson, userId, token,
                idempotentWhenSettled, auditSummary);
            if (outcome.Blocked is { } failure)
            {
                await transaction.RollbackAsync(token);
                return failure;
            }
            warnings = outcome.Warnings;
            await transaction.CommitAsync(token);
        }
        catch
        {
            await transaction.RollbackAsync(token);
            throw;
        }
        logger.LogInformation("统一表单{Action} module={ModuleId} key={Key} executor={User} confirmPerson={Person}",
            approve ? "批核" : "解批", definition.ModuleId, string.Join(',', keyValues), userId, confirmPerson);
        return RecordSaveResult.Success(keyValues, warnings);
    }

    /// <summary>
    /// 开事务之前的全部门槛（缺列 / 单据不存在 / 状态冲突 / 已结案 / 解批 NOBACK 守卫）。
    /// 与 <see cref="RunEngineApprovalAsync"/> 逐字同源：预演走同一个方法，才不会出现
    /// "预演说能批、真点却被拦"。返回 null 表示可以继续。
    /// </summary>
    public async Task<RecordSaveResult?> CheckApprovalPreconditionsAsync(
        SqlConnection connection,
        WorkbenchDefinition definition,
        IReadOnlyList<string> keyValues,
        bool approve,
        CancellationToken token,
        bool idempotentWhenSettled = false,
        // 预演在**已开事务**的连接上调用本方法：此时连接处于本地挂起事务中，
        // 命令必须显式带上事务，否则驱动直接拒绝执行。真实路径在开事务之前调用，传 null。
        SqlTransaction? transaction = null)
    {
        if (!await WorkbenchSql.ColumnExistsAsync(connection, transaction, definition.MasterTable, "CONFIRM_TAG", token))
        {
            return RecordSaveResult.Failed(RecordAccessStatus.ValidationFailed, "LIFECYCLE_COLUMN_MISSING",
                $"该模块启用自动批核，但主表 {definition.MasterTable} 缺少 CONFIRM_TAG 列：请补列后重发布，或关闭自动批核。");
        }
        var originalState = await ReadConfirmStateAsync(
            connection, definition.MasterTable, definition.MasterPkOrder, keyValues, token, transaction);
        if (originalState is null)
        {
            return RecordSaveResult.Failed(RecordAccessStatus.NotFound, "RECORD_NOT_FOUND", "记录不存在。");
        }
        if (approve && originalState.Value.Tag == true)
        {
            return idempotentWhenSettled
                ? RecordSaveResult.Success(keyValues)
                : RecordSaveResult.Failed(RecordAccessStatus.ValidationFailed, "WORKFLOW_STATE_CONFLICT",
                    "记录不存在或已批核，无法重复批核。");
        }
        if (!approve && originalState.Value.Tag != true)
        {
            return RecordSaveResult.Failed(RecordAccessStatus.ValidationFailed, "WORKFLOW_STATE_CONFLICT",
                "记录不存在或未批核，无法解批。");
        }
        if (!approve)
        {
            // 结案锁死：已结案的单据不许解批，必须先取消结案（与删除/编辑同一道闸）。
            if (await ReadFinishedTagAsync(
                    connection, definition.MasterTable, definition.MasterPkOrder, keyValues, token, transaction) == true)
            {
                return FinishedRecordNotDeapprovable();
            }
            var noBack = await CheckNotBackFieldsAsync(connection, definition, keyValues, token, transaction);
            if (noBack is not null)
            {
                return noBack;
            }
        }
        return null;
    }

    /// <summary>
    /// 预演前三道分流判据：真实批核先按「无副作用批核 / 送审 / 引擎接管」分流，
    /// **只有引擎接管那条会跑效果链**。其余分支若也允许预演，报告就与真点不一样——
    /// 一份与真实路径不符的报告比不给报告更糟，所以这里显式拒绝并说明原因。
    /// 返回 null 表示这条路径确实会执行效果链。
    /// </summary>
    public async Task<RecordSaveResult?> CheckSimulationSupportedAsync(
        SqlConnection connection,
        WorkbenchDefinition definition,
        bool approve,
        CancellationToken token)
    {
        var effectsEnabled = effectEngine.IsEnabledFor(definition);
        var hasFlow = await WorkflowEngine.HasFlowAsync(connection, definition.ModuleId, token);
        if (WorkflowStates.IsStatelessApproveCapable(definition.AutoApprove, effectsEnabled, hasFlow))
        {
            return NotSimulatable(
                "该模块是无副作用批核（自动批核且无流程、无效果链）：真实批核只翻转状态位，没有效果链可预演。");
        }
        if (!effectsEnabled)
        {
            return NotSimulatable(
                "该模块未启用效果引擎：真实批核不会执行效果链，预演无法给出与真实路径一致的报告。");
        }
        if (approve && !definition.AutoApprove && hasFlow)
        {
            return NotSimulatable(
                "该模块配置了审批流程：真实批核这一步是「送审」（启动流程），效果链要等流程通过后才跑；预演不覆盖流程。");
        }
        return null;
    }

    private static RecordSaveResult NotSimulatable(string message) =>
        RecordSaveResult.Failed(RecordAccessStatus.ValidationFailed, "SIMULATION_NOT_SUPPORTED", message);

    /// <summary>已结案单据拒绝解批。删除、编辑与解批共用同一道闸，错误码与文案只在这里产出一处。</summary>
    private static RecordSaveResult FinishedRecordNotDeapprovable() =>
        RecordSaveResult.Failed(RecordAccessStatus.ValidationFailed, "FINISHED_RECORD_NOT_DEAPPROVABLE",
            "单据已结案，不能解批，请先取消结案。");

    /// <summary>
    /// 批核/解批的**事务内核心**：校验闸 → 翻转确认状态 → 效果链 → 审计，**不提交**。
    /// 提交由调用方决定——真实路径提交，预演路径无条件回滚，两条路共用这一段。
    /// 翻转必须在这里做（而不是只跑效果链）：效果条件可能引用 CONFIRM_TAG，
    /// 不翻转就求值会得到错误答案。
    /// </summary>
    public async Task<ApprovalCoreResult> RunApprovalCoreAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        WorkbenchDefinition definition,
        IReadOnlyList<string> keyValues,
        bool approve,
        string confirmPerson,
        string userId,
        CancellationToken token,
        bool idempotentWhenSettled = false,
        string? auditSummary = null,
        bool simulate = false)
    {
        var eventKind = approve ? EffectEvent.ApproveEffect : EffectEvent.Deapprove;
        // 校验闸先行：被拦时状态与效果都没有落库（顺序即阻断语义，不能颠倒）。
        if (await effectEngine.ValidateStageAsync(connection, transaction, definition, eventKind, keyValues, token) is { } blocked)
        {
            return ApprovalCoreResult.BlockedBy(
                RecordSaveResult.Failed(RecordAccessStatus.ValidationFailed, "BUSINESS_VALIDATION_FAILED", blocked));
        }
        var keyWhere = WorkbenchSql.BuildKeyWhere(definition.MasterPkOrder, keyValues);
        // 前后值：批核 / 解批改的就是确认状态这三列，翻转之前先留一份。
        var beforeConfirm = await ReadConfirmStateAsync(
            connection, definition.MasterTable, definition.MasterPkOrder, keyValues, token, transaction);
        var confirmSql = approve
            ? $"UPDATE dbo.[{definition.MasterTable}] SET CONFIRM_PERSON=@ConfirmPerson,CONFIRM_DATE=GETDATE(),CONFIRM_TAG=1 WHERE ISNULL(CONFIRM_TAG,0)=0 AND {keyWhere};"
            : $"UPDATE dbo.[{definition.MasterTable}] SET CONFIRM_PERSON=@ConfirmPerson,CONFIRM_DATE=GETDATE(),CONFIRM_TAG=0 WHERE CONFIRM_TAG=1 AND {keyWhere};";
        await using (var confirmCommand = new SqlCommand(confirmSql, connection, transaction))
        {
            confirmCommand.Parameters.Add("@ConfirmPerson", SqlDbType.NVarChar, 50).Value = confirmPerson.Trim();
            WorkbenchSql.AddKeyParameters(confirmCommand, definition.MasterPkOrder, keyValues);
            if (await confirmCommand.ExecuteNonQueryAsync(token) == 0)
            {
                // 并发下已被另一方翻转：不重复执行效果链。
                return ApprovalCoreResult.BlockedBy(idempotentWhenSettled
                    ? RecordSaveResult.Success(keyValues)
                    : RecordSaveResult.Failed(RecordAccessStatus.ValidationFailed, "WORKFLOW_STATE_CONFLICT",
                        approve ? "记录不存在或已批核，无法重复批核。" : "记录不存在或未批核，无法解批。"));
            }
        }
        // 解批会把 CONFIRM_PERSON / CONFIRM_DATE 一并覆盖（状态列只存"最后一位操作人"），
        // 因此"当初是谁在何时结的账"**只**留在这条审计的旧值里——反结账留痕靠的就是它。
        var confirmChanges = ConfirmStateChanges(
            beforeConfirm,
            await ReadConfirmStateAsync(
                connection, definition.MasterTable, definition.MasterPkOrder, keyValues, token, transaction));
        EffectEngineInvoker.EffectRunResult effectRun;
        if (simulate)
        {
            effectRun = await effectEngine.TryRunSimulatedAsync(
                connection, transaction, definition, eventKind, keyValues, userId, token);
        }
        else
        {
            // 走**带步骤**的那条：处理器放行但写下告警时（档位配成"只告警"），
            // 步骤里的 Warning 就是回传给用户的唯一载体，丢掉它等于让"只告警"什么也不说。
            effectRun = await effectEngine.TryRunWithStepsAsync(
                connection, transaction, definition, eventKind, keyValues, userId, token);
        }
        if (effectRun.Error is not null)
        {
            return new ApprovalCoreResult(
                RecordSaveResult.Failed(RecordAccessStatus.ValidationFailed, "WORKFLOW_FAILED", effectRun.Error),
                effectRun.Steps);
        }
        await auditWriter.WriteEventAsync(connection, transaction, definition.ModuleId, string.Join(',', keyValues),
            approve ? "APPROVE" : "DEAPPROVE", auditSummary ?? (approve ? "批核" : "解批"), userId, "WORKBENCH_RECORD", result: 1, fieldChanges: confirmChanges, token);
        return new ApprovalCoreResult(null, effectRun.Steps, StepWarnings(effectRun.Steps));
    }

    /// <summary>
    /// 事务内核心的结果：Blocked 非空表示链路没有走完（校验闸拦下或状态守卫命中 0 行），
    /// Steps 是效果链逐步结果（预演与真实路径都收），Warnings 是处理器写下的**非阻断告警**。
    /// </summary>
    public sealed record ApprovalCoreResult(
        RecordSaveResult? Blocked,
        IReadOnlyList<EffectStepResult> Steps,
        IReadOnlyList<SaveWarning>? Warnings = null)
    {
        public static ApprovalCoreResult BlockedBy(RecordSaveResult failure) => new(failure, []);
    }

    /// <summary>
    /// 效果步骤里带出来的非阻断告警 → 保存告警通道（与"失败"分开：失败走 Blocked，
    /// 告警是"做成了但请留意"）。**含"失败但按 WARN 档放行"的步骤**——链确实往下走了，
    /// 那一步的失败原因正是用户最该看见的一句话；只收成功步骤，会让 WARN 档什么也不说。
    /// 按文案去重，免得同一句在多个步骤里重复刷屏。
    /// </summary>
    private static IReadOnlyList<SaveWarning>? StepWarnings(IReadOnlyList<EffectStepResult> steps)
    {
        var messages = steps
            .Where(step => !string.IsNullOrWhiteSpace(step.Warning))
            .SelectMany(step => step.Warning!.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        return messages.Count == 0
            ? null
            : messages.Select(text => new SaveWarning("EFFECT_WARNING", text)).ToList();
    }

    /// <summary>
    /// 删除补偿守卫：
    /// 结案单据禁止删除；任何已批核单据（含自动批核）禁止删除，需先解批；
    /// 已产生库存流水的单据禁止删除（解批回退后再删）。
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

        var keyWhere = WorkbenchSql.BuildKeyWhere(definition.MasterPkOrder, keyValues);
        var stateColumns = new List<string>();
        if (hasConfirm)
        {
            stateColumns.Add("CONFIRM_TAG");
        }
        if (hasFinished)
        {
            stateColumns.Add("FINISHED_TAG");
        }
        var sql = $"SELECT {string.Join(',', stateColumns.Select(column => $"ISNULL([{column}],0)"))} FROM dbo.[{definition.MasterTable}] WITH (NOLOCK) WHERE {keyWhere};";
        await using var command = new SqlCommand(sql, connection, transaction);
        WorkbenchSql.AddKeyParameters(command, definition.MasterPkOrder, keyValues);
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
            // 库存流水的存在性判断经 InventoryQueryService：单别 / 单号的去空格比较口径在那里。
            if (await InventoryQueryService.HasLedgerAsync(
                    connection, transaction, keyValues[0], keyValues[1], token))
            {
                return RecordSaveResult.Failed(RecordAccessStatus.ValidationFailed, "INVENTORY_LOG_EXISTS",
                    "单据已产生库存流水，禁止删除；请先解批回退库存。");
            }
        }
        return null;
    }

    /// <summary>
    /// 读取结案状态位；主表没有该列时返回 null（缺列不阻塞，与既有守卫同口径）。
    /// </summary>
    private static async Task<bool?> ReadFinishedTagAsync(
        SqlConnection connection, string table, IReadOnlyList<string> pkColumns, IReadOnlyList<string> keyValues,
        CancellationToken token, SqlTransaction? transaction = null)
    {
        if (!await WorkbenchSql.ColumnExistsAsync(connection, transaction, table, "FINISHED_TAG", token))
        {
            return null;
        }
        var keyWhere = WorkbenchSql.BuildKeyWhere(pkColumns, keyValues);
        await using var command = new SqlCommand(
            $"SELECT FINISHED_TAG FROM dbo.[{table}] WITH (NOLOCK) WHERE {keyWhere};", connection, transaction);
        WorkbenchSql.AddKeyParameters(command, pkColumns, keyValues);
        await using var reader = await command.ExecuteReaderAsync(token);
        return await reader.ReadAsync(token) && !reader.IsDBNull(0) ? reader.GetBoolean(0) : null;
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
        // 批核能力有两个来源：已发布定义的效果链（数据驱动）、自动批核模块的无副作用状态翻转。
        // 遗留批核过程钩子（MODULES.UPDATE_SP）已物理删除，故不再有"静态登记批核过程"这一维度。
        var effectsEnabled = effectEngine.IsEnabledFor(definition);
        await using var connection = CreateConnection();
        await connection.OpenAsync(token);
        // 流程定义一次查询，供无副作用判定与送审分支复用。
        var hasFlow = await WorkflowEngine.HasFlowAsync(connection, definition.ModuleId, token);
        // 无副作用批核必须在能力守卫之前判定：它与按钮显隐共用同一条件，
        // 否则按钮显示可点、请求却在守卫处被判不支持（自动批核模块无法手动解批）。
        var stateless = WorkflowStates.IsStatelessApproveCapable(
            definition.AutoApprove, effectsEnabled, hasFlow);
        if (!stateless && !effectsEnabled)
        {
            return RecordSaveResult.Failed(RecordAccessStatus.NotFound, "WORKFLOW_NOT_SUPPORTED", "该模块不支持批核操作。");
        }
        // 结案锁死：已结案的单据不许解批，必须先取消结案。
        // 删除与编辑路径早已拦这一条，解批此前没有拦——而结案常常意味着"本单已经转出下游单据/已经过账"，
        // 直接调解批端点绕过守卫会让下游单据失去来源。放在这里是为了让无副作用解批与效果链解批共用一道闸。
        if (!approve
            && await ReadFinishedTagAsync(connection, definition.MasterTable, definition.MasterPkOrder, keyValues, token) == true)
        {
            return FinishedRecordNotDeapprovable();
        }
        // 无副作用批核/解批（自动批核且无批核过程/效果链/流程定义）：保存路径的自动批核
        // 本就是纯状态翻转，显式动作同口径。有流程定义的仍走送审，有过程/效果链的仍走原路径。
        // 判定与表单按钮显隐共用 WorkflowStates.IsStatelessApproveCapable，两边不得分叉。
        if (stateless)
        {
            return await StatelessApproveAsync(connection, definition, keyValues, approve, employeeName, userId, token);
        }
        // 自动批核模块（MODULES.AUTO_APPROVE=1）：保存即已确认（经办人=保存人），
        // 显式批核幂等返回成功，且不进入流程送审（用户语义：自动批核模块不走新增、审核模式）。
        if (approve && definition.AutoApprove)
        {
            // 与保存路径同口径，缺列显式失败（否则 ReadConfirmStateAsync 直查
            // CONFIRM_TAG 会抛 SQL 异常，以 500 收场）。
            if (!await WorkbenchSql.ColumnExistsAsync(connection, null, definition.MasterTable, "CONFIRM_TAG", token))
            {
                return RecordSaveResult.Failed(RecordAccessStatus.ValidationFailed, "LIFECYCLE_COLUMN_MISSING",
                    $"该模块启用自动批核，但主表 {definition.MasterTable} 缺少 CONFIRM_TAG 列：请补列后重发布，或关闭自动批核。");
            }
            var state = await ReadConfirmStateAsync(connection, definition.MasterTable, definition.MasterPkOrder, keyValues, token);
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
        // 无流程模块保持直接批核。
        else if (approve && hasFlow)
        {
            return await workflowEngine.StartFlowAsync(definition, keyValues, employeeName, userId, token, message);
        }
        // 引擎接管（手工批核/解批）：与自动批核共用同一条生效链（校验闸 → 状态 → 效果 → 审计，同一事务）。
        if (effectsEnabled)
        {
            return await RunEngineApprovalAsync(definition, keyValues, approve, employeeName, userId, token);
        }
        // 引擎未接管：没有可执行的生效链。遗留批核过程钩子（MODULES.UPDATE_SP）已从库内物理删除，
        // 因此这里 fail-closed 拒绝，不再回落到"状态先落定、再调过程、失败补偿还原"的旧桥接语义。
        return RecordSaveResult.Failed(RecordAccessStatus.ValidationFailed, "WORKFLOW_FAILED",
            "该模块未启用效果引擎，且不再支持遗留批核过程。");
    }

    /// <summary>
    /// 无副作用批核/解批（自动批核模块且无批核过程/效果链/流程定义）：
    /// 仅翻转 CONFIRM_TAG（+经办人/日期）并写审计；解批保留 NOT_BACK_FIELDS 前置校验。
    /// 与保存路径的自动批核对称（那边无能力即纯状态翻转）。
    /// </summary>
    private async Task<RecordSaveResult> StatelessApproveAsync(
        SqlConnection connection,
        WorkbenchDefinition definition,
        IReadOnlyList<string> keyValues,
        bool approve,
        string employeeName,
        string userId,
        CancellationToken token)
    {
        if (!await WorkbenchSql.ColumnExistsAsync(connection, null, definition.MasterTable, "CONFIRM_TAG", token))
        {
            return RecordSaveResult.Failed(RecordAccessStatus.ValidationFailed, "LIFECYCLE_COLUMN_MISSING",
                $"该模块启用自动批核，但主表 {definition.MasterTable} 缺少 CONFIRM_TAG 列：请补列后重发布，或关闭自动批核。");
        }
        var originalState = await ReadConfirmStateAsync(connection, definition.MasterTable, definition.MasterPkOrder, keyValues, token);
        if (originalState is null)
        {
            return RecordSaveResult.Failed(RecordAccessStatus.NotFound, "RECORD_NOT_FOUND", "记录不存在。");
        }
        if (approve && originalState.Value.Tag == true)
        {
            return RecordSaveResult.Success(keyValues);
        }
        if (!approve && originalState.Value.Tag == true)
        {
            var noBack = await CheckNotBackFieldsAsync(connection, definition, keyValues, token);
            if (noBack is not null)
            {
                return noBack;
            }
        }
        var keyWhere = WorkbenchSql.BuildKeyWhere(definition.MasterPkOrder, keyValues);
        var confirmSql = approve
            ? $"UPDATE dbo.[{definition.MasterTable}] SET CONFIRM_PERSON=@ConfirmPerson,CONFIRM_DATE=GETDATE(),CONFIRM_TAG=1 WHERE ISNULL(CONFIRM_TAG,0)=0 AND {keyWhere};"
            : $"UPDATE dbo.[{definition.MasterTable}] SET CONFIRM_PERSON=@ConfirmPerson,CONFIRM_DATE=GETDATE(),CONFIRM_TAG=0 WHERE CONFIRM_TAG=1 AND {keyWhere};";
        await using var confirmCommand = new SqlCommand(confirmSql, connection);
        confirmCommand.Parameters.Add("@ConfirmPerson", SqlDbType.NVarChar, 50).Value = employeeName.Trim();
        WorkbenchSql.AddKeyParameters(confirmCommand, definition.MasterPkOrder, keyValues);
        if (await confirmCommand.ExecuteNonQueryAsync(token) == 0)
        {
            return RecordSaveResult.Failed(RecordAccessStatus.ValidationFailed, "WORKFLOW_STATE_CONFLICT",
                approve ? "记录不存在或已批核，无法重复批核。" : "记录不存在或未批核，无法解批。");
        }
        logger.LogInformation("统一表单{Action}（无副作用） module={ModuleId} key={Key}", approve ? "批核" : "解批", definition.ModuleId, string.Join(',', keyValues));
        await auditWriter.WriteEventAsync(connection, null, definition.ModuleId, string.Join(',', keyValues),
            approve ? "APPROVE" : "DEAPPROVE", approve ? "批核" : "解批", userId, "WORKBENCH_RECORD", result: 1,
            fieldChanges: ConfirmStateChanges(
                originalState,
                await ReadConfirmStateAsync(connection, definition.MasterTable, definition.MasterPkOrder, keyValues, token)),
            token);
        return RecordSaveResult.Success(keyValues);
    }

    /// <summary>
    /// 结案/取消结案：开事务跑 <see cref="RunFinishCoreAsync"/>，成功提交、失败整笔回滚。
    /// 与批核路径同形（<see cref="RunEngineApprovalAsync"/> / <see cref="RunApprovalCoreAsync"/>）。
    /// </summary>
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
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(token);
        IReadOnlyList<SaveWarning>? warnings = null;
        try
        {
            var outcome = await RunFinishCoreAsync(
                connection, transaction, definition, keyValues, finish, employeeName, userId, token);
            if (outcome.Blocked is { } failure)
            {
                await transaction.RollbackAsync(token);
                return failure;
            }
            warnings = outcome.Warnings;
            await transaction.CommitAsync(token);
        }
        catch
        {
            await transaction.RollbackAsync(token);
            throw;
        }
        logger.LogInformation("统一表单{Action} module={ModuleId} key={Key}", finish ? "结案" : "取消结案", definition.ModuleId, string.Join(',', keyValues));
        return RecordSaveResult.Success(keyValues, warnings);
    }

    /// <summary>
    /// 结案/取消结案的**事务内核心**：主表标记 → 明细标记 → 效果链 → 审计，**不提交**（提交由调用方决定）。
    /// 四步同一事务，任一步失败整笔回滚——不许出现"标记翻了但预留没释放"这类半截状态。
    /// 重复点击在第一步就被守卫挡下（`ISNULL(FINISHED_TAG,0)` 判据 + 影响 0 行），
    /// 此时尚未写入任何东西，也不会跑效果链：重复结案不会变成"再释放一次"。
    /// </summary>
    /// <remarks>
    /// 明细腿的口径是**全部结案**：单据级结案同时把该单明细行的 `FINISHED_TAG` 置为同一方向。
    /// 现代界面只有一个单据级结案入口，映射的是旧系统 `doFinish(type=3)`（明细全置位 + 主表置位）；
    /// 明细表没有该列（含单表模块）就跳过这一步。
    /// </remarks>
    public async Task<ApprovalCoreResult> RunFinishCoreAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        WorkbenchDefinition definition,
        IReadOnlyList<string> keyValues,
        bool finish,
        string employeeName,
        string userId,
        CancellationToken token)
    {
        var keyWhere = WorkbenchSql.BuildKeyWhere(definition.MasterPkOrder, keyValues);
        var hasTag = await WorkbenchSql.ColumnExistsAsync(connection, transaction, definition.MasterTable, "FINISHED_TAG", token);
        if (!hasTag)
        {
            return ApprovalCoreResult.BlockedBy(
                RecordSaveResult.Failed(RecordAccessStatus.NotFound, "ENDCASE_NOT_SUPPORTED", "该模块不支持结案操作。"));
        }
        var hasPerson = await WorkbenchSql.ColumnExistsAsync(connection, transaction, definition.MasterTable, "FINISHED_PERSON", token);
        var hasDate = await WorkbenchSql.ColumnExistsAsync(connection, transaction, definition.MasterTable, "FINISHED_DATE", token);
        var detailTable = string.IsNullOrWhiteSpace(definition.DetailTable) ? null : definition.DetailTable;
        var hasDetailTag = detailTable is not null
            && await WorkbenchSql.ColumnExistsAsync(connection, transaction, detailTable, "FINISHED_TAG", token);
        var masterSql = finish
            ? $"UPDATE dbo.[{definition.MasterTable}] SET FINISHED_TAG=1{(hasPerson ? ",FINISHED_PERSON=@Person" : string.Empty)}{(hasDate ? ",FINISHED_DATE=GETDATE()" : string.Empty)} WHERE ISNULL(FINISHED_TAG,0)=0 AND {keyWhere};"
            : $"UPDATE dbo.[{definition.MasterTable}] SET FINISHED_TAG=0{(hasPerson ? ",FINISHED_PERSON=@Person" : string.Empty)}{(hasDate ? ",FINISHED_DATE=GETDATE()" : string.Empty)} WHERE FINISHED_TAG=1 AND {keyWhere};";
        var detailSql = detailTable is null || !hasDetailTag
            ? null
            : $"UPDATE dbo.[{detailTable}] SET FINISHED_TAG={(finish ? 1 : 0)} WHERE {keyWhere};";

        await using (var command = new SqlCommand(masterSql, connection, transaction))
        {
            if (hasPerson)
            {
                command.Parameters.Add("@Person", SqlDbType.NVarChar, 50).Value = employeeName.Trim();
            }
            WorkbenchSql.AddKeyParameters(command, definition.MasterPkOrder, keyValues);
            if (await command.ExecuteNonQueryAsync(token) == 0)
            {
                return ApprovalCoreResult.BlockedBy(RecordSaveResult.Failed(
                    RecordAccessStatus.ValidationFailed, "ENDCASE_STATE_CONFLICT",
                    finish ? "记录不存在或已结案，无法重复结案。" : "记录不存在或未结案，无法取消结案。"));
            }
        }

        if (detailSql is not null)
        {
            await using var detailCommand = new SqlCommand(detailSql, connection, transaction);
            WorkbenchSql.AddKeyParameters(detailCommand, definition.MasterPkOrder, keyValues);
            await detailCommand.ExecuteNonQueryAsync(token);
        }

        // 效果链只有在"该事件配了动作"时才执行：模块没启用效果引擎、或没配该事件的动作时，
        // 引擎返回"没接管"，既不报错也不算空链异常（与批核路径同一口径）。
        var effectRun = await effectEngine.TryRunWithStepsAsync(
            connection, transaction, definition,
            finish ? EffectEvent.Endcase : EffectEvent.Unendcase, keyValues, userId, token);
        if (effectRun.Error is not null)
        {
            return new ApprovalCoreResult(
                RecordSaveResult.Failed(RecordAccessStatus.ValidationFailed, "WORKFLOW_FAILED", effectRun.Error),
                effectRun.Steps);
        }

        await auditWriter.WriteEventAsync(connection, transaction, definition.ModuleId, string.Join(',', keyValues),
            finish ? "ENDCASE" : "UNENDCASE", finish ? "结案" : "取消结案", userId, "WORKBENCH_RECORD", result: 1, fieldChanges: null, token);
        return new ApprovalCoreResult(null, effectRun.Steps, StepWarnings(effectRun.Steps));
    }

    /// <summary>解批前置校验。</summary>
    private async Task<RecordSaveResult?> CheckNotBackFieldsAsync(
        SqlConnection connection,
        WorkbenchDefinition definition,
        IReadOnlyList<string> keyValues,
        CancellationToken token,
        SqlTransaction? transaction = null)
    {
        string masterFields, detailFields;
        await using (var moduleCommand = new SqlCommand(
            "SELECT LTRIM(RTRIM(ISNULL(NOT_BACK_FIELDS_M,''))),LTRIM(RTRIM(ISNULL(NOT_BACK_FIELDS,''))) " +
            "FROM dbo.MODULES WITH (NOLOCK) WHERE M_IDX=@ModuleId;", connection, transaction))
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
        if (!string.IsNullOrWhiteSpace(masterFields) && !string.IsNullOrWhiteSpace(definition.MasterTable))
        {
            await CheckNotBackTableAsync(connection, definition, definition.MasterTable, masterFields, keyValues, blocked, token, transaction);
        }
        if (!string.IsNullOrWhiteSpace(detailFields) && !string.IsNullOrWhiteSpace(definition.DetailTable))
        {
            await CheckNotBackTableAsync(connection, definition, definition.DetailTable!, detailFields, keyValues, blocked, token, transaction);
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
        WorkbenchDefinition definition,
        string table,
        string fieldsCsv,
        IReadOnlyList<string> keyValues,
        List<string> blocked,
        CancellationToken token,
        SqlTransaction? transaction = null)
    {
        var fields = fieldsCsv.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Where(field => WorkbenchSql.Identifier.IsMatch(field)).ToArray();
        if (fields.Length == 0)
        {
            return;
        }

        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using (var columnCommand = new SqlCommand(
            "SELECT c.name FROM sys.columns c JOIN sys.objects o ON c.object_id=o.object_id AND o.type IN ('U','V') JOIN sys.schemas s ON o.schema_id=s.schema_id WHERE s.name=N'dbo' AND o.name=@Table ORDER BY c.column_id;", connection, transaction))
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
                definition.ModuleId, table, missing);
        }
        if (valid.Length == 0)
        {
            return;
        }

        var labels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var inClause = string.Join(",", valid.Select((_, index) => $"@f{index}"));
        await using (var descCommand = new SqlCommand(
            $"SELECT LTRIM(RTRIM(F_ID)),LTRIM(RTRIM(ISNULL(F_DESC,''))) FROM dbo.FIELDS WITH (NOLOCK) " +
            $"WHERE LTRIM(RTRIM(T_ID))=@Table AND F_ID IN ({inClause});", connection, transaction))
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
        var keyWhere = WorkbenchSql.BuildKeyWhere(definition.MasterPkOrder, keyValues);
        await using var checkCommand = new SqlCommand(
            $"SELECT COUNT_BIG(1) FROM dbo.[{table}] WITH (NOLOCK) WHERE {keyWhere} AND ({predicates});", connection, transaction);
        WorkbenchSql.AddKeyParameters(checkCommand, definition.MasterPkOrder, keyValues);
        var count = Convert.ToInt64(await checkCommand.ExecuteScalarAsync(token));
        if (count > 0)
        {
            blocked.AddRange(valid.Select(field => labels.TryGetValue(field, out var label) && label.Length > 0 ? label : field));
        }
    }

    /// <summary>
    /// 批核 / 解批写进审计的**前后值**：这次动作改的就是确认状态那三列。
    /// </summary>
    /// <remarks>
    /// 为什么非记不可（而不是"审计有事件就够了"）：解批会把 `CONFIRM_PERSON` / `CONFIRM_DATE`
    /// **一并覆盖**（这三列只存"最后一位操作人"），于是"当初是谁在何时结的账"在状态列上**不留痕迹**，
    /// 只留在这条审计的旧值里。 要求反结账必须留痕，靠的就是它。
    /// 三次调用点（引擎路径 / 无副作用路径 / 自动批核）共用本方法，前后值形态一致。
    /// </remarks>
    private static IReadOnlyList<AuditFieldChange>? ConfirmStateChanges(
        (bool? Tag, string? Person, DateTime? Date)? before,
        (bool? Tag, string? Person, DateTime? Date)? after)
    {
        if (before is not { } old || after is not { } now)
        {
            return null;
        }
        return
        [
            new("CONFIRM_TAG", FlagText(old.Tag), FlagText(now.Tag), null),
            // CONFIRM_PERSON 是定长 NCHAR：读出来带尾随空格；写入时本服务先 Trim()，
            // 故新旧值都按去空格记，前后值口径与写入口径一致。
            new("CONFIRM_PERSON", old.Person?.Trim(), now.Person?.Trim(), null),
            new("CONFIRM_DATE", DateText(old.Date), DateText(now.Date), null)
        ];
    }

    private static string? FlagText(bool? value) => value is null ? null : value.Value ? "1" : "0";

    private static string? DateText(DateTime? value) => value?.ToString("yyyy-MM-dd HH:mm:ss");

    private static async Task<(bool? Tag, string? Person, DateTime? Date)?> ReadConfirmStateAsync(
        SqlConnection connection, string table, IReadOnlyList<string> pkColumns, IReadOnlyList<string> keyValues,
        CancellationToken token, SqlTransaction? transaction = null)
    {
        var keyWhere = WorkbenchSql.BuildKeyWhere(pkColumns, keyValues);
        var sql = $"SELECT CONFIRM_TAG,CONFIRM_PERSON,CONFIRM_DATE FROM dbo.[{table}] WITH (NOLOCK) WHERE {keyWhere};";
        await using var command = new SqlCommand(sql, connection, transaction);
        WorkbenchSql.AddKeyParameters(command, pkColumns, keyValues);
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
