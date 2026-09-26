using System.Data;
using System.Text.Json;
using EOS.API.Data.Effects;
using EOS.API.Data.Inventory;
using EOS.API.Models;
using EOS.API.Telemetry;
using Microsoft.Data.SqlClient;

namespace EOS.API.Data;

/// <summary>
/// 工作台命令处理器：
/// 记录读取/新增/修改/删除 + 主子表事务 + 服务端必填/审计列/金额复算 + 幂等键。
/// 删除路径先经 WorkbenchApprovalService.EnsureDeletionAllowedAsync 补偿守卫；
/// 审批（批核/结案/自动批核）委托 WorkbenchApprovalService；
/// 审计统一经 WorkbenchAuditWriter；数据范围统一经 WorkbenchScopeFilter（fail-closed）。
/// </summary>
public sealed class WorkbenchCommandHandler(
    DbConnectionFactory connections,
    WorkbenchAuditWriter auditWriter,
    WorkbenchApprovalService approvalService,
    WorkbenchScopeFilter scopeFilter,
    EffectEngineInvoker effectEngine,
    WorkbenchIdempotency idempotency,
    DepotStockPolicyService depotPolicies,
    WorkbenchVirtualColumnResolver virtualColumns,
    ILogger<WorkbenchCommandHandler> logger)
{
    public async Task<RecordReadResult> GetRecordAsync(
        WorkbenchDefinition definition,
        FormDefinition form,
        IReadOnlyList<string> keyValues,
        string? dataFilter,
        CancellationToken token)
    {
        using var timing = DbTimingCollector.Instance.Measure();
        await using var connection = CreateConnection();
        await connection.OpenAsync(token);
        var pkColumns = await WorkbenchSql.GetPrimaryKeyColumnsAsync(connection, null, definition.MasterTable, token);
        if (pkColumns.Count != keyValues.Count)
        {
            return new(RecordAccessStatus.KeyMismatch, null);
        }
        var masterFields = form.MasterFields.Where(field => !field.DisplayOnly && !field.IsVirtual).Select(field => field.Key).Concat(pkColumns).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        // 批核/结案状态列：表单不可编辑，但记录读取契约必须返回
        foreach (var statusColumn in WorkflowStates.RecordStatusColumns)
        {
            if (!masterFields.Contains(statusColumn, StringComparer.OrdinalIgnoreCase)
                && await WorkbenchSql.ColumnExistsAsync(connection, null, definition.MasterTable, statusColumn, token))
            {
                masterFields.Add(statusColumn);
            }
        }
        // 虚拟列（客户名称/业务员/仓库名…）本表没有物理列，靠 VIRTUAL_EXP 连表取值；
        // 表达式从定义侧取——ReadFields 已按 QUERY_RELATION 白名单筛过一遍，这里只取表单用到的那些。
        var masterVirtual = await ResolveVirtualColumnsAsync(
            connection, definition.MasterTable, VirtualFieldsFor(definition.MasterFields, form.MasterFields), token);
        var current = await WorkbenchSql.ReadRowAsync(
            connection, null, definition.MasterTable, pkColumns, keyValues,
            masterFields, masterVirtual.SelectFragments, masterVirtual.JoinFragment, token);
        if (current is null)
        {
            return new(RecordAccessStatus.NotFound, null);
        }
        if (!scopeFilter.TryBuildRecordScopePredicate(definition, dataFilter, out var scopePredicate, out var scopeParameters))
        {
            return new(RecordAccessStatus.FilterUnsupported, null);
        }
        if (!string.IsNullOrWhiteSpace(scopePredicate))
        {
            if (!await WorkbenchSql.RecordInScopeAsync(connection, null, definition.MasterTable, pkColumns, keyValues, scopePredicate, scopeParameters, token))
            {
                return new(RecordAccessStatus.OutOfScope, null);
            }
        }
        await ResolveChooserDisplaysAsync(connection, null, definition.ModuleId, definition.MasterTable, form.MasterFields, current, keyValues, token);
        var detailRows = new List<Dictionary<string, object?>>();
        if (definition.DetailTable is not null && form.DetailFields.Count > 0)
        {
            var detailFields = form.DetailFields.Where(field => !field.DisplayOnly && !field.IsVirtual).Select(field => field.Key).Concat(pkColumns).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var detailVirtual = await ResolveVirtualColumnsAsync(
                connection, definition.DetailTable, VirtualFieldsFor(definition.DetailFields, form.DetailFields), token);
            detailRows.AddRange(await WorkbenchSql.ReadRowsAsync(
                connection, null, definition.DetailTable, pkColumns, keyValues,
                detailFields, detailVirtual.SelectFragments, detailVirtual.JoinFragment, token));
            await ResolveDetailChooserDisplaysAsync(connection, null, definition.ModuleId, definition.DetailTable, form.DetailFields, detailRows, keyValues, token);
        }
        var flowState = await ReadFlowStateAsync(connection, definition.ModuleId, WorkbenchKeyCondition.Build(pkColumns, keyValues), token);
        return new(RecordAccessStatus.Ok, new RecordBundle(current, detailRows), flowState);
    }

    /// <summary>
    /// 表单里出现、且定义侧带表达式的虚拟列。
    /// 取定义侧（而非直接查 FIELDS）是因为它已经在 ReadFields 里按 QUERY_RELATION 白名单解析过一遍，
    /// 解析不了的虚拟列压根不在定义里——这里再筛一次，避免把不可能取到值的字段带上。
    /// </summary>
    internal static List<WorkbenchField> VirtualFieldsFor(
        IReadOnlyList<WorkbenchField> definitionFields, IReadOnlyList<FormFieldDefinition> formFields)
    {
        var wanted = formFields
            .Where(field => field.IsVirtual)
            .Select(field => field.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return wanted.Count == 0
            ? []
            : definitionFields.Where(field => field.IsVirtual && wanted.Contains(field.Key)).ToList();
    }

    /// <summary>
    /// 虚拟列受控解析：返回的 <c>SelectFragments</c> 直接拼进 SELECT、<c>JoinFragment</c> 直接拼进 FROM。
    /// 表名/列名只来自 QUERY_RELATION 白名单与物理存在性校验；解析不了的字段进 UnresolvedKeys
    /// （读取时该字段值为空），不影响同一次读取里的其它字段。
    /// </summary>
    private async Task<VirtualColumnResolution> ResolveVirtualColumnsAsync(
        SqlConnection connection, string table, IReadOnlyList<WorkbenchField> virtualFields, CancellationToken token)
        => virtualFields.Count == 0
            ? new VirtualColumnResolution([], string.Empty, [], [], [])
            : await virtualColumns.ResolveDefinitionAsync(connection, table, virtualFields, token, baseAlias: "__base");

    /// <summary>读取单据在途流程状态（WF_MONITOR.WF_STATE → FlowState 投影；无实例=None）。</summary>
    private static async Task<FlowState> ReadFlowStateAsync(
        SqlConnection connection, int moduleId, string keyCondition, CancellationToken token)
    {
        const string sql = """
            SELECT TOP 1 LTRIM(RTRIM(ISNULL(WF_STATE,''))) FROM dbo.WF_MONITOR WITH (NOLOCK)
            WHERE WF_M_IDX=@ModuleId AND KEY_VALUE=@KeyValue;
            """;
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@ModuleId", SqlDbType.Int).Value = moduleId;
        command.Parameters.Add("@KeyValue", SqlDbType.VarChar, 200).Value = keyCondition;
        var state = await command.ExecuteScalarAsync(token) as string;
        return state switch
        {
            "0" => FlowState.InProgress,
            "1" => FlowState.Completed,
            "2" => FlowState.Withdrawn,
            _ => FlowState.None,
        };
    }

    public async Task<RecordSaveResult> CreateRecordAsync(
        WorkbenchDefinition definition,
        FormDefinition form,
        SaveRecordRequest request,
        string employeeName,
        string userId,
        string? dataFilter,
        CancellationToken token)
    {
        var idempotencyKey = NormalizeIdempotencyKey(request.IdempotencyKey);
        await using var connection = CreateConnection();
        await connection.OpenAsync(token);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(token);
        if (idempotencyKey is not null)
        {
            var existing = await idempotency.TryClaimAsync(connection, transaction, idempotencyKey, definition.ModuleId, "INSERT", token);
            if (existing is { ResultKey: not null })
            {
                await transaction.CommitAsync(token);
                return RecordSaveResult.Success(ParseResultKey(existing.ResultKey));
            }
        }

        var pkColumns = await WorkbenchSql.GetPrimaryKeyColumnsAsync(connection, transaction, definition.MasterTable, token);
        if (pkColumns.Count == 0)
        {
            return RecordSaveResult.Failed(RecordAccessStatus.ValidationFailed, "NO_PRIMARY_KEY", "模块主表缺少主键定义。");
        }
        var masterIdentity = await WorkbenchSql.GetIdentityColumnsAsync(connection, transaction, definition.MasterTable, token);

        var validation = RecordPayloadValidator.ValidateSubmitted(form.MasterFields, request.Values);
        if (validation.Errors.Count > 0)
        {
            return RecordSaveResult.Failed(RecordAccessStatus.ValidationFailed, "VALIDATION_FAILED", "数据校验未通过。", validation.Errors);
        }
        var values = new Dictionary<string, object?>(validation.Converted, StringComparer.OrdinalIgnoreCase);
        RecordPayloadValidator.ApplyDefaults(form.MasterFields, values);
        FormDefaultRules.Apply(definition.ModuleId, form.MasterFields, values);

        // 数据归属三列服务端独占写入：新建一律按当前会话覆盖（客户端提交值直接丢弃，
        // 此前 TryAdd 会让伪造的 OWNER 生效，属归属伪造缺口）；更新时保持创建归属不变。
        if (await WorkbenchSql.ColumnExistsAsync(connection, transaction, definition.MasterTable, "OWNER", token))
        {
            values["OWNER"] = userId;
        }
        if (await WorkbenchSql.ColumnExistsAsync(connection, transaction, definition.MasterTable, "OWNER_G", token))
        {
            var primaryGroup = await WorkbenchSql.GetPrimaryGroupAsync(connection, transaction, userId, token);
            if (primaryGroup is not null)
            {
                values["OWNER_G"] = primaryGroup;
            }
            else
            {
                values.Remove("OWNER_G");
            }
        }
        if (await WorkbenchSql.ColumnExistsAsync(connection, transaction, definition.MasterTable, "CI", token))
        {
            var company = await WorkbenchSql.GetUserCompanyAsync(connection, transaction, userId, token);
            if (company is null)
            {
                logger.LogWarning("用户无公司归属，回填默认公司 userId={UserId}", userId);
                company = WorkflowStates.DefaultCompanyId;
            }
            values["CI"] = company;
        }

        // 领域规则：自动单号 + 默认单别
        var businessRule = definition.BusinessRule;
        if (businessRule is { AutoBillNo: true, BillNoField: not null, BillTypeField: not null })
        {
            var existingNo = values.GetValueOrDefault(businessRule.BillNoField);
            if (existingNo is null || string.IsNullOrWhiteSpace(ValueToString(existingNo)))
            {
                var newNo = await BillNoGenerator.GenerateAsync(connection, transaction, definition.ModuleId, token);
                if (newNo is not null)
                {
                    values[businessRule.BillNoField] = newNo;
                }
            }
            var existingType = values.GetValueOrDefault(businessRule.BillTypeField);
            if (existingType is null || string.IsNullOrWhiteSpace(ValueToString(existingType)))
            {
                var billCode = await BillNoGenerator.GetDefaultBillCodeAsync(connection, transaction, definition.ModuleId, token);
                if (billCode is not null)
                {
                    values[businessRule.BillTypeField] = billCode;
                }
            }
        }

        var finalErrors = RecordPayloadValidator.CheckRequiredAndRegex(form.MasterFields, values);
        if (finalErrors.Count > 0)
        {
            return RecordSaveResult.Failed(RecordAccessStatus.ValidationFailed, "VALIDATION_FAILED", "数据校验未通过。", finalErrors);
        }
        var fillErrors = await FillServerColumnsAsync(connection, transaction, definition.MasterTable, form.MasterFields, values, employeeName, true, token);
        if (fillErrors.Count > 0)
        {
            return RecordSaveResult.Failed(RecordAccessStatus.ValidationFailed, "SERVER_FILL_MISSING", "存在服务端必填字段未登记填充规则。", fillErrors);
        }
        // 只读联动列（汇率等）前端不会提交：在落库前补齐，INSERT 与明细判据读的是同一份值
        var derived = await MasterDerivedColumnFiller.FillAsync(
            connection, transaction, definition, form.MasterFields, values, request.Details, token);
        if (derived.Error is { } derivedError)
        {
            return RecordSaveResult.Failed(RecordAccessStatus.ValidationFailed, derivedError.Code, derivedError.Message, [derivedError]);
        }

        foreach (var pk in pkColumns)
        {
            if (masterIdentity.Contains(pk))
            {
                continue;
            }
            if (!values.ContainsKey(pk) || values[pk] is null)
            {
                return RecordSaveResult.Failed(RecordAccessStatus.ValidationFailed, "REQUIRED_FIELD_MISSING", $"主键字段 {pk} 不能为空。", [new FieldError(pk, "主键字段不能为空。", "REQUIRED_FIELD_MISSING")]);
            }
        }

        var keyValues = pkColumns.Select(column => ValueToString(values.GetValueOrDefault(column))).ToList();

        var formKeys = form.MasterFields.Where(field => !field.IsVirtual && values.ContainsKey(field.Key) && !masterIdentity.Contains(field.Key)).Select(field => field.Key).ToList();
        // 服务端持有值（审计/归属回填）可能不在表单定义内（表单隐藏），按物理存在补回；
        // 非物理键一律排除，阻断幽灵列进入 INSERT。
        var masterPhysical = await WorkbenchSql.GetPhysicalColumnsAsync(connection, transaction, definition.MasterTable, token);
        var insertColumns = BuildInsertColumns(formKeys, values, masterPhysical, masterIdentity);
        if (insertColumns.Count == 0)
        {
            return RecordSaveResult.Failed(RecordAccessStatus.ValidationFailed, "NO_WRITABLE_FIELDS", "没有可写入的字段。");
        }
        decimal? identityValue;
        try
        {
            identityValue = await InsertRowAsync(connection, transaction, definition.MasterTable, insertColumns, values, masterIdentity, token);
        }
        // 唯一键冲突（2601 唯一索引 / 2627 主键）不是异常状态，而是用户可预期的输入——编号撞车。
        // 一律转成可读的 400，原始 SqlException 进日志，不再让它穿透成 500 并把库错误原文透出。
        // 自动单号模块的单号冲突语义不同：单号是服务端预生成的预览值，重新保存即可拿到新号，
        // 因此保留 BILL_NO_CONFLICT 的专门文案。
        catch (SqlException ex) when (ex.Number is 2601 or 2627)
        {
            var parsed = TryParseDuplicateIndexName(ex.Message, out var duplicateIndexName);
            var conflict = parsed
                ? await ReadConflictIndexColumnsAsync(connection, transaction, definition.MasterTable, duplicateIndexName, token)
                : new ConflictIndex(Array.Empty<string>(), false);

            if (businessRule is { AutoBillNo: true, BillNoField: not null }
                && IndexCoversBillNo(conflict, pkColumns, businessRule.BillNoField))
            {
                return RecordSaveResult.Failed(RecordAccessStatus.ValidationFailed, "BILL_NO_CONFLICT",
                    "单号已被占用，请重新保存以获取新单号。");
            }

            var duplicatedKeys = pkColumns
                .Where(pk => conflict.Columns.Contains(pk, StringComparer.OrdinalIgnoreCase))
                .ToArray();
            logger.LogWarning(ex, "统一表单新增唯一键冲突 module={ModuleId} table={Table} index={Index} key={Key}",
                definition.ModuleId, definition.MasterTable, duplicateIndexName, string.Join(',', keyValues));
            return duplicatedKeys.Length > 0
                ? RecordSaveResult.Failed(RecordAccessStatus.ValidationFailed, "DUPLICATE_RECORD_KEY",
                    $"该编号已存在：{string.Join('、', keyValues)}。请更换后重试。",
                    duplicatedKeys.Select(pk => new FieldError(pk, "该值已存在，请更换后重试。", "DUPLICATE_RECORD_KEY")).ToArray())
                : RecordSaveResult.Failed(RecordAccessStatus.ValidationFailed, "DUPLICATE_RECORD_KEY",
                    "该记录已存在（唯一键冲突），请检查编号等唯一字段后重试。");
        }
        // 必填列为空、内容超长、外键不存在：同属用户可预期的输入问题，一律转成带字段的 400。
        catch (SqlException ex) when (WriteFailureTranslator.IsExpectedWriteFailure(ex))
        {
            var masterFailure = WriteFailureTranslator.Translate(ex, definition.MasterTable);
            logger.LogWarning(ex, "统一表单新增写入被拒 module={ModuleId} table={Table} code={Code}",
                definition.ModuleId, definition.MasterTable, masterFailure.Code);
            return RecordSaveResult.Failed(RecordAccessStatus.ValidationFailed, masterFailure.Code, masterFailure.Message, [masterFailure]);
        }
        if (identityValue is not null && masterIdentity.Count > 0)
        {
            values[masterIdentity[0]] = identityValue;
            keyValues = pkColumns.Select(column => ValueToString(values.GetValueOrDefault(column))).ToList();
        }

        // After insert, the record must stay within the module contract (module FILTER + DATA_FILTER + EXEC_TAG).
        // 谓词在这里先构建（配置不支持即早失败、与记录内容无关），但**判定后移到 SAVE 效果链之后**：
        // 有些模块的过滤条件依赖"判别字段"（如托外/补料标志、模具性质、报关方式），而这些字段是
        // 模块身份、由服务端在保存期写入（旧系统亦然）。早期判定会把这类新建一律拒掉——效果再写也来不及。
        // 修改路径本来就是"改完 + 跑完效果再判"，这里与它对齐。
        if (!scopeFilter.TryBuildRecordScopePredicate(definition, dataFilter, out var scopePredicate, out var scopeParameters))
        {
            return RecordSaveResult.Failed(RecordAccessStatus.FilterUnsupported, "DATA_FILTER_UNSUPPORTED", "当前数据过滤条件尚不支持，已拒绝执行。");
        }

        // ADR-020 §9.1 D1-d（WS-16）：档 2「建议」在**保存期**就给出并写入建议位置——草稿上看得见、改得动；
        // 档 3「强制」不在这里处理（保存期拒绝会让草稿存不下来，刻意不做），档 0/1 位置由人定。
        var suggestion = await DepotLocationSuggestionService.FillAsync(
            connection, transaction, definition, request.Details, depotPolicies, token);
        var detailSave = await SaveDetailsAsync(connection, transaction, definition, form, pkColumns, keyValues, values, suggestion.Details, request.DetailSerials, employeeName, true, token);
        if (detailSave.Errors is not null)
        {
            return DetailFailure(detailSave.Errors);
        }
        await WriteChooserSourceMemoAsync(connection, transaction, definition, form, keyValues, detailSave.RowKeys, request, employeeName, token);
        await SavePrepayOffsetsAsync(connection, transaction, businessRule, pkColumns, keyValues, request.PrepayOffsets, token);
        // 目录校验（SAVE 阶段）是模块的声明式校验目录，独立于"谁负责保存后行为"：
        // 无论该模块走效果动作还是遗留钩子，都必须先过这道闸。
        if (await effectEngine.ValidateStageAsync(connection, transaction, definition, EffectEvent.Save, keyValues, token) is { } saveValidation)
        {
            return RecordSaveResult.Failed(RecordAccessStatus.ValidationFailed, "BUSINESS_VALIDATION_FAILED", saveValidation);
        }
        // 保存后行为来自效果目录（SAVE 阶段动作链）。遗留保存后过程钩子（MODULES.AFTERSAVE_SP）
        // 已从库内物理删除，故没有"未配动作就回落调过程"这一分支了。
        var effectRun = await effectEngine.TryRunAsync(
            connection, transaction, definition, EffectEvent.Save, keyValues, userId, token);
        if (effectRun.Ran && effectRun.Error is not null)
        {
            return RecordSaveResult.Failed(RecordAccessStatus.ValidationFailed, "BUSINESS_VALIDATION_FAILED", effectRun.Error);
        }
        if (await DetailRequirementAsync(connection, transaction, definition, pkColumns, keyValues, request.Details, token) is { } detailRequirement)
        {
            return RecordSaveResult.Failed(RecordAccessStatus.ValidationFailed, "VALIDATION_FAILED", "明细数据校验未通过。", detailRequirement);
        }
        await RecalculateMasterAmountsAsync(connection, transaction, definition, token);
        // 与修改路径同一落点：跑完 SAVE 效果链之后再判"记录是否仍在模块范围内"。
        if (!string.IsNullOrWhiteSpace(scopePredicate)
            && !await WorkbenchSql.RecordInScopeAsync(connection, transaction, definition.MasterTable, pkColumns, keyValues, scopePredicate, scopeParameters, token))
        {
            return RecordSaveResult.Failed(RecordAccessStatus.ValidationFailed, "RECORD_OUT_OF_MODULE_FILTER", "新建记录不满足模块过滤条件，无法保存。");
        }
        await auditWriter.WriteEventAsync(connection, transaction, definition.ModuleId, string.Join(',', keyValues),
            "INSERT", "新增记录", userId, "WORKBENCH_RECORD", result: 1, fieldChanges: null, token);
        if (idempotencyKey is not null)
        {
            await idempotency.CompleteAsync(connection, transaction, idempotencyKey, SerializeResultKey(keyValues), false, token);
        }
        await transaction.CommitAsync(token);
        logger.LogInformation("统一表单新增 module={ModuleId} master={Master} key={Key}", definition.ModuleId, definition.MasterTable, string.Join(',', keyValues));
        // 建议位置是"已保存但有说明"的一类结果：写在告警里回传前端，别让它悄悄发生
        IReadOnlyList<SaveWarning>? warnings = suggestion.Warnings.Count > 0 ? suggestion.Warnings : null;
        if (definition.AutoApprove)
        {
            // 自动批核模块：保存成功后立即进入批核生效（保存事务提交后执行，生效链自带事务）；
            // 经办人取当前保存人——谁保存的这条记录，就是谁让它生效的；
            // 失败不回滚保存：以 warnings 回传前端提示「已保存，但自动批核失败」，单据停在未批核态可重试。
            var autoResult = await approvalService.AutoApproveAsync(definition, keyValues, employeeName, userId, token);
            if (autoResult.Status != RecordAccessStatus.Ok)
            {
                logger.LogWarning("自动批核失败 module={ModuleId} key={Key} code={Code} message={Message}",
                    definition.ModuleId, string.Join(',', keyValues), autoResult.ErrorCode, autoResult.ErrorMessage);
                warnings = [.. warnings ?? [], new SaveWarning("AUTO_APPROVE_FAILED",
                    $"已保存，但自动批核失败：{autoResult.ErrorMessage ?? autoResult.ErrorCode ?? "未知原因"}")];
            }
        }
        return RecordSaveResult.Success(keyValues, warnings);
    }

    public async Task<RecordSaveResult> UpdateRecordAsync(
        WorkbenchDefinition definition,
        FormDefinition form,
        IReadOnlyList<string> keyValues,
        SaveRecordRequest request,
        string employeeName,
        string userId,
        string? dataFilter,
        CancellationToken token)
    {
        var idempotencyKey = NormalizeIdempotencyKey(request.IdempotencyKey);
        await using var connection = CreateConnection();
        await connection.OpenAsync(token);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(token);
        if (idempotencyKey is not null)
        {
            var existing = await idempotency.TryClaimAsync(connection, transaction, idempotencyKey, definition.ModuleId, "UPDATE", token);
            if (existing is { ResultKey: not null })
            {
                await transaction.CommitAsync(token);
                return RecordSaveResult.Success(ParseResultKey(existing.ResultKey));
            }
        }

        var pkColumns = await WorkbenchSql.GetPrimaryKeyColumnsAsync(connection, transaction, definition.MasterTable, token);
        if (pkColumns.Count != keyValues.Count)
        {
            return RecordSaveResult.Failed(RecordAccessStatus.KeyMismatch, "RECORD_KEY_MISMATCH", "主键数量与模块主键不匹配。");
        }
        var masterFields = form.MasterFields.Where(field => !field.DisplayOnly && !field.IsVirtual).Select(field => field.Key).Concat(pkColumns).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        // 批核/结案状态列：表单不可编辑，但记录读取契约必须返回（用于编辑前状态校验）
        foreach (var statusColumn in new[] { "CONFIRM_TAG", "FINISHED_TAG" })
        {
            if (!masterFields.Contains(statusColumn, StringComparer.OrdinalIgnoreCase)
                && await WorkbenchSql.ColumnExistsAsync(connection, transaction, definition.MasterTable, statusColumn, token))
            {
                masterFields.Add(statusColumn);
            }
        }
        var current = await WorkbenchSql.ReadRowAsync(connection, transaction, definition.MasterTable, pkColumns, keyValues, masterFields, token);
        if (current is null)
        {
            return RecordSaveResult.Failed(RecordAccessStatus.NotFound, "RECORD_NOT_FOUND", "记录不存在。");
        }
        // 状态校验：已结案 / 已批核的单据禁止编辑
        if (IsStatusTrue(current, "FINISHED_TAG"))
        {
            return RecordSaveResult.Failed(RecordAccessStatus.ValidationFailed, "FINISHED_EDIT_FORBIDDEN", "记录已结案，禁止编辑（请先取消结案）。");
        }
        if (IsStatusTrue(current, "CONFIRM_TAG"))
        {
            return RecordSaveResult.Failed(RecordAccessStatus.ValidationFailed, "CONFIRMED_EDIT_FORBIDDEN", "记录已批核，禁止编辑（请先解批）。");
        }
        // 在途流程守卫：流程审批中的单据禁止编辑（审批人批的是送审时快照，改后需先撤回再重提）
        var editKeyCondition = WorkbenchKeyCondition.Build(pkColumns, keyValues);
        if (await WorkflowEngine.HasActiveFlowAsync(connection, transaction, definition.ModuleId, editKeyCondition, token))
        {
            return RecordSaveResult.Failed(RecordAccessStatus.ValidationFailed, "FLOW_IN_PROGRESS_EDIT_FORBIDDEN",
                "记录流程正在审批中，禁止编辑（请先撤回流程）。");
        }
        // Target record must be within the module contract before editing
        if (!scopeFilter.TryBuildRecordScopePredicate(definition, dataFilter, out var scopePredicate, out var scopeParameters))
        {
            return RecordSaveResult.Failed(RecordAccessStatus.FilterUnsupported, "DATA_FILTER_UNSUPPORTED", "当前数据过滤条件尚不支持，已拒绝执行。");
        }
        if (!string.IsNullOrWhiteSpace(scopePredicate)
            && !await WorkbenchSql.RecordInScopeAsync(connection, transaction, definition.MasterTable, pkColumns, keyValues, scopePredicate, scopeParameters, token))
        {
            return RecordSaveResult.Failed(RecordAccessStatus.OutOfScope, "RECORD_OUT_OF_SCOPE", "目标记录不在当前用户数据范围内。");
        }

        if (request.Original is not null)
        {
            var writable = form.MasterFields.Where(field => !field.IsReadonly && !field.IsVirtual && !field.ServerFilled).ToList();
            string? conflictingField = null;
            foreach (var (key, raw) in request.Original)
            {
                var field = writable.FirstOrDefault(item => item.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
                if (field is null || !RecordPayloadValidator.TryConvert(field.DataType, raw, out var expected))
                {
                    continue;
                }
                if (!ValuesEqual(expected, current.GetValueOrDefault(field.Key)))
                {
                    logger.LogDebug("并发冲突诊断 field={Field} expected={Expected} current={Current}", field.Key, expected, current.GetValueOrDefault(field.Key));
                    conflictingField ??= field.Key;
                }
            }
            if (conflictingField is not null)
            {
                return RecordSaveResult.Failed(RecordAccessStatus.ConcurrentModified, "CONCURRENT_MODIFIED", "字段内容已被他人修改，请刷新后重试！", [new FieldError(conflictingField, "字段内容已被他人修改，请刷新后重试！", "CONCURRENT_MODIFIED")]);
            }
        }

        var validation = RecordPayloadValidator.ValidateSubmitted(form.MasterFields, request.Values);
        if (validation.Errors.Count > 0)
        {
            return RecordSaveResult.Failed(RecordAccessStatus.ValidationFailed, "VALIDATION_FAILED", "数据校验未通过。", validation.Errors);
        }
        var merged = new Dictionary<string, object?>(current, StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in validation.Converted)
        {
            merged[key] = value;
        }
        // 与新增路径同一落点、同一口径：只读联动列在写主表前补齐（记录里既有的非空值不覆盖）
        var derivedUpdate = await MasterDerivedColumnFiller.FillAsync(
            connection, transaction, definition, form.MasterFields, merged, request.Details, token);
        if (derivedUpdate.Error is { } derivedUpdateError)
        {
            return RecordSaveResult.Failed(RecordAccessStatus.ValidationFailed, derivedUpdateError.Code, derivedUpdateError.Message, [derivedUpdateError]);
        }
        var finalErrors = RecordPayloadValidator.CheckRequiredAndRegex(form.MasterFields, merged);
        if (finalErrors.Count > 0)
        {
            return RecordSaveResult.Failed(RecordAccessStatus.ValidationFailed, "VALIDATION_FAILED", "数据校验未通过。", finalErrors);
        }

        var sets = new List<(string Column, object? Value)>();
        foreach (var (key, value) in validation.Converted)
        {
            if (pkColumns.Contains(key, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }
            sets.Add((key, value));
        }
        // 补齐出来的派生列（原值为空）随本次提交一并落库
        foreach (var (column, value) in derivedUpdate.Filled)
        {
            if (pkColumns.Contains(column, StringComparer.OrdinalIgnoreCase)) continue;
            if (sets.Any(item => item.Column.Equals(column, StringComparison.OrdinalIgnoreCase))) continue;
            sets.Add((column, value));
        }
        if (await WorkbenchSql.ColumnExistsAsync(connection, transaction, definition.MasterTable, "LAST_UPDATE_BY", token))
        {
            sets.Add(("LAST_UPDATE_BY", employeeName));
        }
        if (await WorkbenchSql.ColumnExistsAsync(connection, transaction, definition.MasterTable, "LAST_UPDATE_DATE", token))
        {
            sets.Add(("LAST_UPDATE_DATE", DateTime.Now));
        }
        if (sets.Count > 0)
        {
            var setSql = string.Join(',', sets.Select((item, index) => $"[{item.Column}]=@s{index}"));
            var where = string.Join(" AND ", pkColumns.Select((column, index) => $"[{column}]=@k{index}"));
            await using var command = new SqlCommand($"UPDATE dbo.[{definition.MasterTable}] SET {setSql} WHERE {where};", connection, transaction);
            for (var i = 0; i < sets.Count; i++)
            {
                command.Parameters.AddWithValue($"@s{i}", WorkbenchSql.NormalizeDbValue(sets[i].Value));
            }
            WorkbenchSql.AddKeyParameters(command, pkColumns, keyValues);
            int affected;
            try
            {
                affected = await command.ExecuteNonQueryAsync(token);
            }
            // 修改把必填列改空、或把值改超长：同属输入问题，转成字段级 400（与新增同口径）。
            catch (SqlException ex) when (WriteFailureTranslator.IsExpectedWriteFailure(ex))
            {
                var updateFailure = WriteFailureTranslator.Translate(ex, definition.MasterTable);
                logger.LogWarning(ex, "统一表单修改写入被拒 module={ModuleId} table={Table} code={Code}",
                    definition.ModuleId, definition.MasterTable, updateFailure.Code);
                return RecordSaveResult.Failed(RecordAccessStatus.ValidationFailed, updateFailure.Code, updateFailure.Message, [updateFailure]);
            }
            if (affected == 0)
            {
                return RecordSaveResult.Failed(RecordAccessStatus.NotFound, "RECORD_NOT_FOUND", "记录不存在。");
            }
        }

        // 与新增路径同一落点、同一口径：档 2 的建议位置在保存期写入（见 DepotLocationSuggestionService）
        var suggestion = await DepotLocationSuggestionService.FillAsync(
            connection, transaction, definition, request.Details, depotPolicies, token);
        var detailSave = await SaveDetailsAsync(connection, transaction, definition, form, pkColumns, keyValues, merged, suggestion.Details, request.DetailSerials, employeeName, false, token);
        if (detailSave.Errors is not null)
        {
            return DetailFailure(detailSave.Errors);
        }
        await WriteChooserSourceMemoAsync(connection, transaction, definition, form, keyValues, detailSave.RowKeys, request, employeeName, token);
        var businessRule = definition.BusinessRule;
        await SavePrepayOffsetsAsync(connection, transaction, businessRule, pkColumns, keyValues, request.PrepayOffsets, token);
        // 目录校验（SAVE 阶段）是模块的声明式校验目录，独立于"谁负责保存后行为"：
        // 无论该模块走效果动作还是遗留钩子，都必须先过这道闸。
        if (await effectEngine.ValidateStageAsync(connection, transaction, definition, EffectEvent.Save, keyValues, token) is { } saveValidation)
        {
            return RecordSaveResult.Failed(RecordAccessStatus.ValidationFailed, "BUSINESS_VALIDATION_FAILED", saveValidation);
        }
        // 同新增路径：执行 SAVE 阶段动作链（遗留保存后过程钩子已物理删除，无回落分支）。
        var effectRun = await effectEngine.TryRunAsync(
            connection, transaction, definition, EffectEvent.Save, keyValues, userId, token);
        if (effectRun.Ran && effectRun.Error is not null)
        {
            return RecordSaveResult.Failed(RecordAccessStatus.ValidationFailed, "BUSINESS_VALIDATION_FAILED", effectRun.Error);
        }
        if (await DetailRequirementAsync(connection, transaction, definition, pkColumns, keyValues, request.Details, token) is { } detailRequirement)
        {
            return RecordSaveResult.Failed(RecordAccessStatus.ValidationFailed, "VALIDATION_FAILED", "明细数据校验未通过。", detailRequirement);
        }
        await RecalculateMasterAmountsAsync(connection, transaction, definition, token);
        // 收紧：修改后记录仍须满足模块契约（防止把记录改出过滤范围）
        if (!string.IsNullOrWhiteSpace(scopePredicate)
            && !await WorkbenchSql.RecordInScopeAsync(connection, transaction, definition.MasterTable, pkColumns, keyValues, scopePredicate, scopeParameters, token))
        {
            return RecordSaveResult.Failed(RecordAccessStatus.ValidationFailed, "RECORD_OUT_OF_MODULE_FILTER", "修改后记录不满足模块过滤条件，无法保存。");
        }
        var changes = new List<string>();
        var fieldChanges = new List<AuditFieldChange>();
        foreach (var (key, value) in validation.Converted)
        {
            var field = form.MasterFields.FirstOrDefault(item => item.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
            var oldValue = current.GetValueOrDefault(key);
            if (field is not null && !ValuesEqual(oldValue, value))
            {
                changes.Add($"{field.Label}：{ValueToString(oldValue)}-->{ValueToString(value)}<BR>");
                fieldChanges.Add(new AuditFieldChange(field.Key, ValueToString(oldValue), ValueToString(value), null));
            }
        }
        await auditWriter.WriteEventAsync(connection, transaction, definition.ModuleId, string.Join(',', keyValues), "UPDATE",
            changes.Count > 0 ? string.Join("", changes) : "修改记录", userId, "WORKBENCH_RECORD", result: 1, fieldChanges, token);
        if (idempotencyKey is not null)
        {
            await idempotency.CompleteAsync(connection, transaction, idempotencyKey, SerializeResultKey(keyValues), false, token);
        }
        await transaction.CommitAsync(token);
        logger.LogInformation("统一表单修改 module={ModuleId} master={Master} key={Key}", definition.ModuleId, definition.MasterTable, string.Join(',', keyValues));
        IReadOnlyList<SaveWarning>? updateWarnings = suggestion.Warnings.Count > 0 ? suggestion.Warnings : null;
        if (definition.AutoApprove)
        {
            // 「保存即批核」对新增与修改同口径：未批核的单据（例如上次自动批核失败、
            // 或启用自动批核之前建立的草稿）在下一次保存时补上批核生效；
            // 经办人取当前编辑人，已批核单据本就被编辑守卫挡住，故这里不会重复累计效果。
            var autoResult = await approvalService.AutoApproveAsync(definition, keyValues, employeeName, userId, token);
            if (autoResult.Status != RecordAccessStatus.Ok)
            {
                logger.LogWarning("自动批核失败（修改） module={ModuleId} key={Key} code={Code} message={Message}",
                    definition.ModuleId, string.Join(',', keyValues), autoResult.ErrorCode, autoResult.ErrorMessage);
                updateWarnings = [.. updateWarnings ?? [], new SaveWarning("AUTO_APPROVE_FAILED",
                    $"已保存，但自动批核失败：{autoResult.ErrorMessage ?? autoResult.ErrorCode ?? "未知原因"}")];
            }
        }
        return RecordSaveResult.Success(keyValues, updateWarnings);
    }

    public async Task<RecordSaveResult> DeleteRecordAsync(
        WorkbenchDefinition definition,
        FormDefinition form,
        IReadOnlyList<string> keyValues,
        string userId,
        string? dataFilter,
        string? idempotencyKey,
        CancellationToken token)
    {
        idempotencyKey = NormalizeIdempotencyKey(idempotencyKey);
        await using var connection = CreateConnection();
        await connection.OpenAsync(token);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(token);
        if (idempotencyKey is not null)
        {
            var existing = await idempotency.TryClaimAsync(connection, transaction, idempotencyKey, definition.ModuleId, "DELETE", token);
            if (existing is { ResultKey: not null })
            {
                await transaction.CommitAsync(token);
                return RecordSaveResult.Success(ParseResultKey(existing.ResultKey));
            }
        }

        var pkColumns = await WorkbenchSql.GetPrimaryKeyColumnsAsync(connection, transaction, definition.MasterTable, token);
        if (pkColumns.Count != keyValues.Count)
        {
            return RecordSaveResult.Failed(RecordAccessStatus.KeyMismatch, "RECORD_KEY_MISMATCH", "主键数量与模块主键不匹配。");
        }
        if (!await WorkbenchSql.RowExistsAsync(connection, transaction, definition.MasterTable, pkColumns, keyValues, token))
        {
            return RecordSaveResult.Failed(RecordAccessStatus.NotFound, "RECORD_NOT_FOUND", "记录不存在。");
        }
        // Target record must be within the module contract before deletion
        if (!scopeFilter.TryBuildRecordScopePredicate(definition, dataFilter, out var scopePredicate, out var scopeParameters))
        {
            return RecordSaveResult.Failed(RecordAccessStatus.FilterUnsupported, "DATA_FILTER_UNSUPPORTED", "当前数据过滤条件尚不支持，已拒绝执行。");
        }
        if (!string.IsNullOrWhiteSpace(scopePredicate)
            && !await WorkbenchSql.RecordInScopeAsync(connection, transaction, definition.MasterTable, pkColumns, keyValues, scopePredicate, scopeParameters, token))
        {
            return RecordSaveResult.Failed(RecordAccessStatus.OutOfScope, "RECORD_OUT_OF_SCOPE", "目标记录不在当前用户数据范围内。");
        }
        // 删除补偿守卫：结案单据/已批核有副作用单据禁止删除
        var guard = await approvalService.EnsureDeletionAllowedAsync(connection, transaction, definition, keyValues, token);
        if (guard is not null)
        {
            return guard;
        }
        // 在途流程守卫：流程审批中的单据禁止删除（防止留下孤儿流程实例与在途任务）
        var deleteKeyCondition = WorkbenchKeyCondition.Build(pkColumns, keyValues);
        if (await WorkflowEngine.HasActiveFlowAsync(connection, transaction, definition.ModuleId, deleteKeyCondition, token))
        {
            return RecordSaveResult.Failed(RecordAccessStatus.ValidationFailed, "FLOW_IN_PROGRESS_DELETE_FORBIDDEN",
                "记录流程正在审批中，禁止删除（请先撤回流程）。");
        }
        // 删除前校验目录（DELETE 阶段）：主档里的受保护行（如库位哨兵行）要在这里拦下。
        if (await effectEngine.ValidateStageAsync(connection, transaction, definition, EffectEvent.Delete, keyValues, token) is { } deleteValidation)
        {
            return RecordSaveResult.Failed(RecordAccessStatus.ValidationFailed, "BUSINESS_VALIDATION_FAILED", deleteValidation);
        }
        if (definition.DetailTable is not null)
        {
            await WorkbenchSql.DeleteDetailRowsAsync(connection, transaction, definition.DetailTable, pkColumns, keyValues, token);
        }
        // 单据删除时一并清掉来源记忆，避免留下指向已删单据的孤儿行。
        await FormChooserSourceMemo.DeleteDocumentAsync(connection, transaction, definition.ModuleId, FormChooserSourceMemo.SerializeKey(keyValues), token);
        var where = string.Join(" AND ", pkColumns.Select((column, index) => $"[{column}]=@k{index}"));
        await using var command = new SqlCommand($"DELETE FROM dbo.[{definition.MasterTable}] WHERE {where};", connection, transaction);
        WorkbenchSql.AddKeyParameters(command, pkColumns, keyValues);
        var affected = await command.ExecuteNonQueryAsync(token);
        if (affected == 0)
        {
            return RecordSaveResult.Failed(RecordAccessStatus.NotFound, "RECORD_NOT_FOUND", "记录不存在。");
        }
        await auditWriter.WriteEventAsync(connection, transaction, definition.ModuleId, string.Join(',', keyValues),
            "DELETE", "删除记录", userId, "WORKBENCH_RECORD", result: 1, fieldChanges: null, token);
        if (idempotencyKey is not null)
        {
            await idempotency.CompleteAsync(connection, transaction, idempotencyKey, SerializeResultKey(keyValues), false, token);
        }
        await transaction.CommitAsync(token);
        logger.LogInformation("统一表单删除 module={ModuleId} master={Master} key={Key}", definition.ModuleId, definition.MasterTable, string.Join(',', keyValues));
        return RecordSaveResult.Success(keyValues);
    }

    /// <summary>
    /// 保存期效果的明细兜底判定：模块声明"无明细不可保存"且调用方提交了空明细时，效果动作
    /// 可能已经把明细派生出来了（见 DetailGeneratorKeys）。效果跑完后明细表仍为空才判失败，
    /// 语义是"保存结束时这张单据必须有明细"，而不是"调用方必须提交明细"。
    /// 仅对声明了明细生成者的模块生效，其它模块仍在 SaveDetailsAsync 里就地拒绝。
    /// </summary>
    private async Task<IReadOnlyList<FieldError>?> DetailRequirementAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        WorkbenchDefinition definition,
        IReadOnlyList<string> pkColumns,
        IReadOnlyList<string> keyValues,
        IReadOnlyList<IReadOnlyDictionary<string, string?>>? submittedDetails,
        CancellationToken token)
    {
        if (definition.DetailTable is null || submittedDetails is not { Count: 0 })
            return null;
        if (EmptyDetailPolicyFor(definition.DetailNoSave, effectEngine.GeneratesDetailRows(definition))
            != EmptyDetailPolicy.RejectAfterEffects)
            return null;
        if (await WorkbenchSql.HasDetailRowsAsync(connection, transaction, definition.DetailTable, pkColumns, keyValues, token))
            return null;
        return [new FieldError("", "该模块无明细资料不可保存。", "DETAIL_REQUIRED")];
    }

    /// <summary>提交了空明细时，模块的"无明细不可保存"该如何处置。</summary>
    internal enum EmptyDetailPolicy
    {
        /// <summary>模块不要求明细：按显式清空处理。</summary>
        Clear,

        /// <summary>要求明细，且没有任何东西会派生明细：就地拒绝。</summary>
        Reject,

        /// <summary>要求明细，但保存期效果会派生明细：把判定推到效果执行之后。</summary>
        RejectAfterEffects,
    }

    /// <summary>
    /// "无明细不可保存"的判定时点。声明了明细生成者的模块不能就地拒绝，否则生成者永远
    /// 跑不到（保存路径先于效果链判定）；此时判据后移为"保存结束时明细表必须有行"。
    /// </summary>
    internal static EmptyDetailPolicy EmptyDetailPolicyFor(bool detailNoSave, bool generatesDetailRows) =>
        !detailNoSave ? EmptyDetailPolicy.Clear
        : generatesDetailRows ? EmptyDetailPolicy.RejectAfterEffects
        : EmptyDetailPolicy.Reject;

    /// <summary>
    /// 明细侧失败的统一出口。"模块根本收不下明细"与"明细逐行校验没过"是两类问题：
    /// 前者是结构不匹配（多半是模块改配或调用方用错端点），后者是数据本身不合法。
    /// 用不同错误码区分，调用方不必解析明细列表就知道该找谁。
    /// </summary>
    private static RecordSaveResult DetailFailure(IReadOnlyList<FieldError> errors) =>
        errors.Count == 1 && errors[0].Code == "DETAIL_NOT_SUPPORTED"
            ? RecordSaveResult.Failed(RecordAccessStatus.ValidationFailed, "DETAIL_NOT_SUPPORTED",
                errors[0].Message, errors)
            : RecordSaveResult.Failed(RecordAccessStatus.ValidationFailed, "VALIDATION_FAILED",
                "明细数据校验未通过。", errors);

    /// <summary>
    /// 明细保存结果：Errors 非空即失败；RowKeys 与提交的明细行**按下标一一对应**
    /// （未写入的行占位 null），来源记忆据此把来源记到正确的行上。
    /// null 表示"本次未提交明细"（既有明细行未被触碰，相关记忆保持原样）。
    /// </summary>
    private sealed record DetailSaveOutcome(IReadOnlyList<FieldError>? Errors, IReadOnlyList<string?>? RowKeys);

    private async Task<DetailSaveOutcome> SaveDetailsAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        WorkbenchDefinition definition,
        FormDefinition form,
        IReadOnlyList<string> pkColumns,
        IReadOnlyList<string> keyValues,
        IReadOnlyDictionary<string, object?> masterValues,
        IReadOnlyList<IReadOnlyDictionary<string, string?>>? details,
        IReadOnlyList<string?>? submittedSerials,
        string employeeName,
        bool isNew,
        CancellationToken token)
    {
        // Absent details and an explicitly empty detail list mean different things: an
        // update that only touches master fields must leave existing detail rows alone,
        // while an empty list is the caller asking to clear (or, on modules that require
        // details, a rejected save). Collapsing the two would silently wipe details.
        if (details is null)
        {
            return new(null, null);
        }
        // 模块收不下明细时，把提交上来的明细静默丢掉会返回一个"成功"的假象——调用方以为
        // 明细已保存，实际什么都没写。纯主表模块允许省略明细（null 或空数组），但不允许
        // 提交了明细还被无声丢弃。
        if (definition.DetailTable is null)
        {
            return details.Count == 0
                ? new(null, [])
                : new([new FieldError("", "该模块没有明细资料，不能提交明细。", "DETAIL_NOT_SUPPORTED")], null);
        }
        if (form.DetailFields.Count == 0)
        {
            return details.Count == 0
                ? new(null, [])
                : new([new FieldError("", "该模块的明细字段未开放，无法保存明细。", "DETAIL_NOT_SUPPORTED")], null);
        }
        if (details.Count == 0)
        {
            // 明细可以由保存期的效果动作自行派生（如盘点单按库区范围展开明细）。此时
            // "调用方没提交明细"不等于"这张单据最终没有明细"，不能在这里直接拒绝——
            // 真正的判据是效果跑完后明细表里有没有行，由 DetailRequirementAsync 复核。
            if (EmptyDetailPolicyFor(definition.DetailNoSave, effectEngine.GeneratesDetailRows(definition))
                == EmptyDetailPolicy.Reject)
            {
                return new([new FieldError("", "该模块无明细资料不可保存。", "DETAIL_REQUIRED")], null);
            }
            await WorkbenchSql.DeleteDetailRowsAsync(connection, transaction, definition.DetailTable, pkColumns, keyValues, token);
            return new(null, []);
        }

        // 判据读的是**补齐后的主表值**（调用方在此之前已按 MasterDerivedColumnFiller 补齐只读联动列）：
        // 这些列前端不会提交，但"有明细就必须有值"的语义不变，确实没有值的仍在这里拒绝。
        if (!string.IsNullOrWhiteSpace(definition.DetailNoFields))
        {
            var missing = definition.DetailNoFields.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Where(field => !masterValues.TryGetValue(field, out var value) || value is null || value is string text && string.IsNullOrWhiteSpace(text))
                .ToList();
            if (missing.Count > 0)
            {
                return new(missing.Select(field => new FieldError(field, "新增明细前必须填写该主表字段。", "DETAIL_NO_FIELDS_MISSING")).ToList(), null);
            }
        }

        var detailIdentity = await WorkbenchSql.GetIdentityColumnsAsync(connection, transaction, definition.DetailTable, token);
        var dfVerify = await WorkbenchSql.GetDfVerifyAsync(connection, transaction, definition.DetailTable, token);
        var dfFields = dfVerify?.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries) ?? [];
        var errors = new List<FieldError>();
        var rows = new List<Dictionary<string, object?>>();
        for (var rowIndex = 0; rowIndex < details.Count; rowIndex++)
        {
            var validation = RecordPayloadValidator.ValidateSubmitted(form.DetailFields, details[rowIndex]);
            // Detail row errors carry the row index so the front end can locate the exact row
            errors.AddRange(validation.Errors.Select(error => error with { RowIndex = rowIndex }));
            var row = new Dictionary<string, object?>(validation.Converted, StringComparer.OrdinalIgnoreCase);
            RecordPayloadValidator.ApplyDefaults(form.DetailFields, row);
            // 明细中未提供的字段，若主表存在同名值（如 CURR_ID/CURR_RATE/TAX_ID），由服务端从主表带入
            foreach (var detailField in form.DetailFields)
            {
                if (row.ContainsKey(detailField.Key))
                {
                    continue;
                }
                if (masterValues.TryGetValue(detailField.Key, out var masterValue) && masterValue is not null)
                {
                    row[detailField.Key] = masterValue;
                }
            }
            for (var i = 0; i < pkColumns.Count; i++)
            {
                var pkField = form.DetailFields.FirstOrDefault(field => field.Key.Equals(pkColumns[i], StringComparison.OrdinalIgnoreCase));
                if (pkField is null)
                {
                    errors.Add(new FieldError(pkColumns[i], "明细表缺少主表关联字段。", "MASTER_KEY_NOT_IN_DETAIL", rowIndex));
                    continue;
                }
                if (RecordPayloadValidator.TryConvert(pkField.DataType, keyValues[i], out var keyValue))
                {
                    row[pkColumns[i]] = keyValue;
                }
            }
            // 服务端复算明细行金额（金额以服务端为权威）
            RecalculateDetailAmounts(form.DetailFields, row, masterValues);
            rows.Add(row);
        }
        // 回传的既有项次必须互不相同：同一行身份被用两次会让第二行撞主键（库层拒绝外，先给可读的 400）
        if (submittedSerials is not null)
        {
            var seenSerials = new HashSet<int>();
            for (var index = 0; index < submittedSerials.Count && index < rows.Count; index++)
            {
                if (!int.TryParse(submittedSerials[index]?.Trim(), out var serial) || serial <= 0)
                {
                    continue;
                }
                if (!seenSerials.Add(serial))
                {
                    return new([new FieldError("SERIAL_NO", "明细项次重复，无法保存。", "DUPLICATE_DETAIL_SERIAL", index)], null);
                }
            }
        }
        RecordPayloadValidator.AssignSerialNumbers(rows, form.DetailFields, submittedSerials);
        // 应收货款单（170101）等引用单据的明细：金额/数量从送货（退货）单明细带出
        await FillReferencedAmountsAsync(connection, transaction, form.DetailFields, rows, token);
        for (var rowIndex = 0; rowIndex < rows.Count; rowIndex++)
        {
            errors.AddRange(RecordPayloadValidator.CheckRequiredAndRegex(form.DetailFields, rows[rowIndex])
                .Select(error => error with { RowIndex = rowIndex }));
        }
        if (errors.Count > 0)
        {
            return new(errors, null);
        }

        if (dfFields.Length > 0)
        {
            var groups = rows.GroupBy(row => string.Join('\u0001', dfFields.Select(field => Convert.ToString(row.GetValueOrDefault(field) ?? string.Empty, System.Globalization.CultureInfo.InvariantCulture))));
            var duplicate = groups.FirstOrDefault(group => group.Count() > 1);
            if (duplicate is not null)
            {
                return new([new FieldError(string.Join(';', dfFields), $"明细表资料重复：{string.Join(';', dfFields)}。", "DF_VERIFY_DUPLICATE")], null);
            }
        }

        await WorkbenchSql.DeleteDetailRowsAsync(connection, transaction, definition.DetailTable, pkColumns, keyValues, token);
        var detailPhysical = await WorkbenchSql.GetPhysicalColumnsAsync(connection, transaction, definition.DetailTable, token);
        // 明细行键取自明细表主键列（含主表键与行号列）：来源记忆按它定位，行增删后不会串行。
        // 行键**按提交下标对齐**（未写入的行占位 null），否则跳过的行会让后续行的记忆记到别人身上。
        var detailPkColumns = await WorkbenchSql.GetPrimaryKeyColumnsAsync(connection, transaction, definition.DetailTable, token);
        var rowKeys = new List<string?>(rows.Count);
        for (var rowIndex = 0; rowIndex < rows.Count; rowIndex++)
        {
            var row = rows[rowIndex];
            var fillErrors = await FillServerColumnsAsync(connection, transaction, definition.DetailTable, form.DetailFields, row, employeeName, isNew, token);
            if (fillErrors.Count > 0)
            {
                return new(fillErrors.Select(error => error with { RowIndex = rowIndex }).ToList(), null);
            }
            var detailFormKeys = form.DetailFields.Where(field => !field.IsVirtual && row.ContainsKey(field.Key) && !detailIdentity.Contains(field.Key)).Select(field => field.Key).ToList();
            var detailColumns = BuildInsertColumns(detailFormKeys, row, detailPhysical, detailIdentity);
            if (detailColumns.Count == 0)
            {
                rowKeys.Add(null);
                continue;
            }
            try
            {
                await InsertRowAsync(connection, transaction, definition.DetailTable, detailColumns, row, detailIdentity, token);
            }
            // 明细行被库拒绝（必填列为空 / 编号重复 / 超长 / 外键不存在）属用户可预期的输入问题：
            // 转成带行号的字段级 400，不再穿透成 500，也不把库错误原文透出。
            catch (SqlException ex) when (WriteFailureTranslator.IsExpectedWriteFailure(ex))
            {
                var detailFailure = WriteFailureTranslator.Translate(ex, definition.DetailTable, rowIndex);
                logger.LogWarning(ex, "明细写入被拒 module={ModuleId} table={Table} row={Row} code={Code}",
                    definition.ModuleId, definition.DetailTable, rowIndex, detailFailure.Code);
                return new([detailFailure], null);
            }
            rowKeys.Add(detailPkColumns.Count == 0
                ? null
                : FormChooserSourceMemo.SerializeKey(detailPkColumns.Select(column => ValueToString(row.GetValueOrDefault(column)))));
        }
        return new(null, rowKeys);
    }

    /// <summary>引用单据金额带出：明细含 S_R_TYPE/S_R_NO/S_R_SERIAL_NO 时从 COP_SEND_D 读取金额填充。</summary>
    private static async Task FillReferencedAmountsAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        IReadOnlyList<FormFieldDefinition> fields,
        IReadOnlyList<Dictionary<string, object?>> rows,
        CancellationToken token)
    {
        var hasRef = fields.Any(field => field.Key.Equals("S_R_TYPE", StringComparison.OrdinalIgnoreCase))
            && fields.Any(field => field.Key.Equals("S_R_NO", StringComparison.OrdinalIgnoreCase))
            && fields.Any(field => field.Key.Equals("S_R_SERIAL_NO", StringComparison.OrdinalIgnoreCase));
        if (!hasRef)
        {
            return;
        }
        var fieldSet = fields.Select(field => field.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            if (!row.TryGetValue("S_R_TYPE", out var rawType) || !row.TryGetValue("S_R_NO", out var rawNo)
                || !row.TryGetValue("S_R_SERIAL_NO", out var rawSerial))
            {
                continue;
            }
            var type = Convert.ToString(rawType) ?? "";
            var no = Convert.ToString(rawNo) ?? "";
            var serial = Convert.ToString(rawSerial) ?? "";
            if (type.Length == 0 || no.Length == 0 || serial.Length == 0)
            {
                continue;
            }
            const string sql = """
                SELECT TOP 1 QTY,AMOUNT,AMOUNT_TAX,TAX_SUM
                FROM dbo.COP_SEND_D WITH (NOLOCK)
                WHERE SEND_TYPE=@t AND SEND_NO=@n AND SERIAL_NO=@s;
                """;
            await using var command = new SqlCommand(sql, connection, transaction);
            command.Parameters.Add("@t", SqlDbType.NVarChar, 50).Value = type;
            command.Parameters.Add("@n", SqlDbType.NVarChar, 50).Value = no;
            command.Parameters.Add("@s", SqlDbType.Int).Value = int.TryParse(serial, out var parsed) ? parsed : 0;
            await using var reader = await command.ExecuteReaderAsync(token);
            if (!await reader.ReadAsync(token))
            {
                continue;
            }
            void Fill(string column)
            {
                if (!fieldSet.Contains(column))
                {
                    return;
                }
                row[column] = reader.IsDBNull(reader.GetOrdinal(column)) ? null : reader.GetValue(reader.GetOrdinal(column));
            }
            Fill("QTY");
            Fill("AMOUNT");
            Fill("AMOUNT_TAX");
            Fill("TAX_SUM");
        }
    }

    /// <summary>服务端复算明细行金额（AMOUNT/TAX_SUM/AMOUNT_TAX），覆盖客户端提交值。</summary>
    private static void RecalculateDetailAmounts(
        IReadOnlyList<FormFieldDefinition> fields,
        IDictionary<string, object?> row,
        IReadOnlyDictionary<string, object?> masterValues)
    {
        var hasAmount = fields.Any(field => field.Key.Equals("AMOUNT", StringComparison.OrdinalIgnoreCase));
        var hasAmountTax = fields.Any(field => field.Key.Equals("AMOUNT_TAX", StringComparison.OrdinalIgnoreCase));
        var hasTaxSum = fields.Any(field => field.Key.Equals("TAX_SUM", StringComparison.OrdinalIgnoreCase));
        if (!hasAmount && !hasAmountTax && !hasTaxSum)
        {
            return;
        }
        var qty = GetDecimal(row, "QTY");
        var price = GetDecimal(row, "PRICE");
        // 无数量/单价的明细行（如预收/预付单按订单冲抵金额）不参与金额重算
        if (qty is null && price is null)
        {
            return;
        }
        var result = AmountCalculator.Calculate(
            qty,
            price,
            GetDecimal(row, "TAX_RATE") ?? GetDecimal(masterValues, "TAX_RATE") ?? 0m,
            row.TryGetValue("TAX_TYPE", out var taxType)
                ? Convert.ToString(taxType)
                : masterValues.TryGetValue("TAX_TYPE", out var masterTaxType) ? Convert.ToString(masterTaxType) : null,
            GetDecimal(row, "REBATE") ?? 100m);
        if (hasAmount)
        {
            row["AMOUNT"] = result.Amount;
        }
        if (hasTaxSum)
        {
            row["TAX_SUM"] = result.TaxSum;
        }
        if (hasAmountTax)
        {
            row["AMOUNT_TAX"] = result.AmountTax;
        }
    }

    private static decimal? GetDecimal(IReadOnlyDictionary<string, object?> row, string key)
    {
        return GetDecimal((IDictionary<string, object?>)row, key);
    }

    private static decimal? GetDecimal(IDictionary<string, object?> row, string key)
    {
        if (!row.TryGetValue(key, out var value) || value is null)
        {
            return null;
        }
        return value switch
        {
            decimal d => d,
            double dbl => Convert.ToDecimal(dbl),
            float f => Convert.ToDecimal(f),
            int i => i,
            long l => l,
            _ => decimal.TryParse(Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture),
                System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var parsed) ? parsed : null,
        };
    }

    /// <summary>
    /// 主表金额汇总与余额字段初始化（明细聚合 + PREPAY/RECEIVE 余额）。
    /// 主表 AMOUNT/AMOUNT_TAX/TAX_SUM 以本汇总为权威（对齐单据明细金额的通用口径），
    /// 在保存后处理之后执行、会覆盖领域规则写入的同名列；明细行异币别折算未实现
    /// （明细币别与主表一致时 SUM 与折算等价）。
    /// </summary>
    private static async Task RecalculateMasterAmountsAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        WorkbenchDefinition definition,
        CancellationToken token)
    {
        if (definition.DetailTable is null || definition.MasterPkOrder.Count < 2)
        {
            return;
        }
        var typeColumn = definition.MasterPkOrder[0];
        var noColumn = definition.MasterPkOrder[1];
        foreach (var column in new[] { "AMOUNT", "AMOUNT_TAX", "TAX_SUM" })
        {
            if (!await WorkbenchSql.ColumnExistsAsync(connection, transaction, definition.DetailTable, column, token))
            {
                continue;
            }
            if (!await WorkbenchSql.ColumnExistsAsync(connection, transaction, definition.MasterTable, column, token))
            {
                continue;
            }
            await using var command = new SqlCommand(
                $"""
                UPDATE m SET [{column}]=d.[{column}]
                FROM dbo.[{definition.MasterTable}] m
                INNER JOIN (SELECT [{typeColumn}],[{noColumn}],SUM([{column}]) AS [{column}]
                            FROM dbo.[{definition.DetailTable}]
                            GROUP BY [{typeColumn}],[{noColumn}]) d
                  ON m.[{typeColumn}]=d.[{typeColumn}] AND m.[{noColumn}]=d.[{noColumn}];
                """, connection, transaction);
            await command.ExecuteNonQueryAsync(token);
        }
        if (await WorkbenchSql.ColumnExistsAsync(connection, transaction, definition.MasterTable, "PREPAY_AMOUNT", token)
            && await WorkbenchSql.ColumnExistsAsync(connection, transaction, definition.MasterTable, "AMOUNT", token))
        {
            await using var prepay = new SqlCommand(
                $"UPDATE dbo.[{definition.MasterTable}] SET PREPAY_AMOUNT=ISNULL(PREPAY_AMOUNT,ISNULL(AMOUNT,0));",
                connection, transaction);
            await prepay.ExecuteNonQueryAsync(token);
        }
        if (await WorkbenchSql.ColumnExistsAsync(connection, transaction, definition.MasterTable, "RECEIVE_AMOUNT", token))
        {
            await using var receive = new SqlCommand(
                $"UPDATE dbo.[{definition.MasterTable}] SET RECEIVE_AMOUNT=ISNULL(RECEIVE_AMOUNT,0);",
                connection, transaction);
            await receive.ExecuteNonQueryAsync(token);
        }
    }

    private static async Task<int?> InsertRowAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        string table,
        IReadOnlyList<string> columns,
        IReadOnlyDictionary<string, object?> values,
        IReadOnlyList<string> identityColumns,
        CancellationToken token)
    {
        var columnList = string.Join(',', columns.Select(column => $"[{column}]"));
        var parameters = string.Join(',', columns.Select((_, index) => $"@v{index}"));
        var sql = $"INSERT INTO dbo.[{table}] ({columnList}) VALUES ({parameters});";
        await using var command = new SqlCommand(sql, connection, transaction);
        for (var i = 0; i < columns.Count; i++)
        {
            command.Parameters.AddWithValue($"@v{i}", WorkbenchSql.NormalizeDbValue(values[columns[i]]));
        }
        if (identityColumns.Count > 0)
        {
            command.CommandText += " SELECT SCOPE_IDENTITY();";
            return Convert.ToInt32(await command.ExecuteScalarAsync(token));
        }
        await command.ExecuteNonQueryAsync(token);
        return null;
    }

    /// <summary>
    /// INSERT 列清单 = 表单字段（维持有序）∪ 服务端持有值的物理列。
    /// 表单隐藏的审计/归属列（FillServerColumns/回填已写入 values）按物理存在补回，
    /// 否则 legacy-NOT NULL 列保存即 500；非表单、非物理的键（幽灵/串表）一律排除；
    /// 自增列排除；附加列按名排序保证语句稳定可测。
    /// </summary>
    internal static IReadOnlyList<string> BuildInsertColumns(
        IReadOnlyList<string> formKeys,
        IReadOnlyDictionary<string, object?> values,
        ISet<string> physicalColumns,
        IReadOnlyList<string> identityColumns)
    {
        var ordered = new List<string>(formKeys.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in formKeys)
        {
            if (seen.Add(key))
            {
                ordered.Add(key);
            }
        }
        var extras = values.Keys
            .Where(key => !seen.Contains(key)
                && !identityColumns.Contains(key, StringComparer.OrdinalIgnoreCase)
                && physicalColumns.Contains(key)
                && WorkbenchSql.Identifier.IsMatch(key))
            .OrderBy(key => key, StringComparer.OrdinalIgnoreCase)
            .ToList();
        ordered.AddRange(extras);
        return ordered;
    }

    private async Task<IReadOnlyList<FieldError>> FillServerColumnsAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        string table,
        IReadOnlyList<FormFieldDefinition> fields,
        IDictionary<string, object?> values,
        string employeeName,
        bool isNew,
        CancellationToken token)
    {
        var now = DateTime.Now;
        var audit = isNew
            ? new (string Name, object Value)[] { ("CREATE_PERSON", employeeName), ("CREATE_DATE", now), ("LAST_UPDATE_BY", employeeName), ("LAST_UPDATE_DATE", now) }
            : new (string Name, object Value)[] { ("LAST_UPDATE_BY", employeeName), ("LAST_UPDATE_DATE", now) };
        foreach (var item in audit)
        {
            if (await WorkbenchSql.ColumnExistsAsync(connection, transaction, table, item.Name, token))
            {
                values[item.Name] = item.Value;
            }
        }
        var errors = new List<FieldError>();
        foreach (var field in fields.Where(field => field.ServerFilled && !values.ContainsKey(field.Key) && !RecordPayloadValidator.IsAuditColumn(field.Key)))
        {
            if (!string.IsNullOrWhiteSpace(field.DefaultValue) && RecordPayloadValidator.TryConvert(field.DataType, field.DefaultValue, out var defaulted))
            {
                values[field.Key] = defaulted;
                continue;
            }
            logger.LogWarning("服务端必填字段无填充规则且无默认值 table={Table} field={Field}", table, field.Key);
        }
        return errors;
    }

    /// <summary>保存收款/付款单的预收/预付冲抵关联表（COP_RECEIPT_PREPAY / PUR_PAY_PREPAY）。</summary>
    private static async Task SavePrepayOffsetsAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        ModuleBusinessRule? rule,
        IReadOnlyList<string> pkColumns,
        IReadOnlyList<string> keyValues,
        IReadOnlyList<PrepayOffsetRequest>? offsets,
        CancellationToken token)
    {
        if (rule?.PrepayOffsetTable is not { } table || pkColumns.Count < 2)
        {
            return;
        }
        var typeColumn = pkColumns[0];
        var noColumn = pkColumns[1];
        await using var delete = new SqlCommand(
            $"DELETE FROM dbo.[{table}] WHERE [{typeColumn}]=@t AND [{noColumn}]=@n", connection, transaction);
        delete.Parameters.Add("@t", SqlDbType.NVarChar, 50).Value = keyValues[0];
        delete.Parameters.Add("@n", SqlDbType.NVarChar, 50).Value = keyValues[1];
        await delete.ExecuteNonQueryAsync(token);
        if (offsets is null)
        {
            return;
        }
        var serial = 1;
        foreach (var offset in offsets)
        {
            await using var insert = new SqlCommand(
                $"""
                INSERT INTO dbo.[{table}] ([{typeColumn}],[{noColumn}],SERIAL_NO,PREPAY_TYPE,PREPAY_NO,AMOUNT,PREPAY_AMOUNT)
                VALUES (@t,@n,@serial,@pt,@pn,@amount,@prepayAmount);
                """, connection, transaction);
            insert.Parameters.Add("@t", SqlDbType.NVarChar, 50).Value = keyValues[0];
            insert.Parameters.Add("@n", SqlDbType.NVarChar, 50).Value = keyValues[1];
            insert.Parameters.Add("@serial", SqlDbType.Int).Value = serial;
            insert.Parameters.Add("@pt", SqlDbType.NVarChar, 50).Value = offset.Type;
            insert.Parameters.Add("@pn", SqlDbType.NVarChar, 50).Value = offset.No;
            insert.Parameters.Add("@amount", SqlDbType.Decimal).Value = (object?)offset.Amount ?? DBNull.Value;
            insert.Parameters.Add("@prepayAmount", SqlDbType.Decimal).Value = offset.PrepayAmount;
            await insert.ExecuteNonQueryAsync(token);
            serial++;
        }
    }

    /// <summary>
    /// 记录「本次提交里用户选过的来源」，供读取回显决定用哪个来源解析同组伴生字段。
    /// 只写本次提交涉及的字段：主表逐字段替换；明细按行键增删（行已不存在的记忆一并清理）。
    /// 未携带来源时不触碰既有记忆——其它调用方与未重选来源的编辑保存行为不变。
    /// </summary>
    private async Task WriteChooserSourceMemoAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        WorkbenchDefinition definition,
        FormDefinition form,
        IReadOnlyList<string> keyValues,
        IReadOnlyList<string?>? detailRowKeys,
        SaveRecordRequest request,
        string employeeName,
        CancellationToken token)
    {
        if (request.ChooserSources is null && request.DetailChooserSources is null)
        {
            return;
        }
        var masterKeyValues = FormChooserSourceMemo.SerializeKey(keyValues);
        if (!FormChooserSourceMemo.KeyFits(masterKeyValues))
        {
            logger.LogWarning("来源记忆跳过：单据键超出列宽 module={ModuleId} key={Key}", definition.ModuleId, masterKeyValues);
            return;
        }
        var masterEntries = new List<(string Field, string KeyValues, int Serial)>();
        if (request.ChooserSources is not null)
        {
            foreach (var (fieldKey, serial) in request.ChooserSources)
            {
                if (TryResolveRememberedSource(form.MasterFields, fieldKey, serial, out var field))
                {
                    masterEntries.Add((field!.Key, masterKeyValues, serial));
                }
            }
        }
        var detailEntries = new List<(string Field, string KeyValues, int Serial)>();
        if (request.DetailChooserSources is not null && detailRowKeys is not null)
        {
            for (var index = 0; index < request.DetailChooserSources.Count && index < detailRowKeys.Count; index++)
            {
                // 未写入的行（占位 null）没有行键可记，跳过；下标仍与提交行对齐
                if (request.DetailChooserSources[index] is not { } rowSources || detailRowKeys[index] is not { } rowKey
                    || !FormChooserSourceMemo.KeyFits(rowKey))
                {
                    continue;
                }
                foreach (var (fieldKey, serial) in rowSources)
                {
                    if (TryResolveRememberedSource(form.DetailFields, fieldKey, serial, out var field))
                    {
                        detailEntries.Add((field!.Key, rowKey, serial));
                    }
                }
            }
            // 明细行整体重写：清理已不存在行的记忆，避免记忆指向不存在的明细行。
            if (definition.DetailTable is not null)
            {
                var live = detailRowKeys.Where(key => key is not null).Select(key => key!).ToHashSet(StringComparer.Ordinal);
                var existing = await FormChooserSourceMemo.ReadAsync(connection, transaction, definition.ModuleId, definition.DetailTable, masterKeyValues, token);
                var stale = existing.Keys
                    .Select(FormChooserSourceMemo.ParseEntryKey)
                    .Where(entry => !live.Contains(entry.KeyValues))
                    .ToList();
                if (stale.Count > 0)
                {
                    await FormChooserSourceMemo.DeleteEntriesAsync(connection, transaction, definition.ModuleId, definition.DetailTable, stale, token);
                }
            }
        }

        await ReplaceMemoEntriesAsync(connection, transaction, definition.ModuleId, definition.MasterTable, masterEntries, masterKeyValues, employeeName, token);
        if (definition.DetailTable is not null)
        {
            await ReplaceMemoEntriesAsync(connection, transaction, definition.ModuleId, definition.DetailTable, detailEntries, masterKeyValues, employeeName, token);
        }
    }

    /// <summary>先删同键旧值再写入：同一 (表, 字段, 行键) 只保留最近一次选择的来源。</summary>
    private static async Task ReplaceMemoEntriesAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        int moduleId,
        string table,
        IReadOnlyList<(string Field, string KeyValues, int Serial)> entries,
        string masterKeyValues,
        string employeeName,
        CancellationToken token)
    {
        if (entries.Count == 0)
        {
            return;
        }
        await FormChooserSourceMemo.DeleteEntriesAsync(connection, transaction, moduleId, table,
            entries.Select(entry => (entry.Field, entry.KeyValues)).ToList(), token);
        foreach (var (field, entryKeyValues, serial) in entries)
        {
            await FormChooserSourceMemo.InsertAsync(connection, transaction, moduleId, table, field, entryKeyValues, masterKeyValues, serial, employeeName, token);
        }
    }

    /// <summary>提交的来源必须确属该字段的启用来源，且该字段值得记忆（复合格主字段、多来源）。</summary>
    private static bool TryResolveRememberedSource(
        IReadOnlyList<FormFieldDefinition> fields,
        string fieldKey,
        int serial,
        out FormFieldDefinition? field)
    {
        field = fields.FirstOrDefault(item => item.Key.Equals(fieldKey, StringComparison.OrdinalIgnoreCase));
        if (field is null || !FormChooserSourceMemo.ShouldRemember(field))
        {
            field = null;
            return false;
        }
        if (!field.Choosers.Any(source => source.SerialNo == serial && source.Active && !string.IsNullOrWhiteSpace(source.Table)))
        {
            field = null;
            return false;
        }
        return true;
    }

    /// <summary>选择器同组展示字段回填（DisplayOnly CellRole=2 伴随主 CellRole=1）。</summary>
    internal static async Task ResolveChooserDisplaysAsync(
        SqlConnection connection,
        SqlTransaction? transaction,
        int moduleId,
        string masterTable,
        IReadOnlyList<FormFieldDefinition> fields,
        Dictionary<string, object?> row,
        IReadOnlyList<string> keyValues,
        CancellationToken token)
    {
        var companionsByGroup = GroupCompanions(fields);
        var mains = MainFieldsWithChoosers(fields, companionsByGroup);
        if (mains.Count == 0)
        {
            return;
        }
        // 来源按「这张单当初选的是哪个来源」解析；无记忆时回退首个启用来源。
        var rowKey = FormChooserSourceMemo.SerializeKey(keyValues);
        var memory = await FormChooserSourceMemo.ReadAsync(connection, transaction, moduleId, masterTable, rowKey, token);
        foreach (var main in mains)
        {
            if (!row.TryGetValue(main.Key, out var raw) || raw is null || string.IsNullOrWhiteSpace(raw.ToString()))
            {
                continue;
            }
            var remembered = memory.TryGetValue(FormChooserSourceMemo.EntryKey(main.Key, rowKey), out var serial) ? serial : (int?)null;
            var source = FormChooserSourceMemo.SelectSource(main, remembered);
            if (source is null)
            {
                continue;
            }
            await ResolveChooserGroupAsync(connection, transaction, main, source, companionsByGroup[main.CellGroup!], row, raw.ToString()!, token);
        }
    }

    /// <summary>
    /// 明细行的同组展示字段回填：与主表同一口径，按「明细行主键」定位记忆。
    /// 行键取自明细表主键列（含主表键与行号列），行增删后记忆自然失效，不会串到别行。
    /// </summary>
    internal static async Task ResolveDetailChooserDisplaysAsync(
        SqlConnection connection,
        SqlTransaction? transaction,
        int moduleId,
        string detailTable,
        IReadOnlyList<FormFieldDefinition> fields,
        IReadOnlyList<Dictionary<string, object?>> rows,
        IReadOnlyList<string> keyValues,
        CancellationToken token)
    {
        if (rows.Count == 0)
        {
            return;
        }
        var companionsByGroup = GroupCompanions(fields);
        var mains = MainFieldsWithChoosers(fields, companionsByGroup);
        if (mains.Count == 0)
        {
            return;
        }
        var detailPkColumns = await WorkbenchSql.GetPrimaryKeyColumnsAsync(connection, transaction, detailTable, token);
        if (detailPkColumns.Count == 0)
        {
            return;
        }
        var masterKey = FormChooserSourceMemo.SerializeKey(keyValues);
        var memory = await FormChooserSourceMemo.ReadAsync(connection, transaction, moduleId, detailTable, masterKey, token);
        foreach (var row in rows)
        {
            var rowKey = FormChooserSourceMemo.SerializeKey(detailPkColumns.Select(column => ValueToString(row.GetValueOrDefault(column))));
            foreach (var main in mains)
            {
                if (!row.TryGetValue(main.Key, out var raw) || raw is null || string.IsNullOrWhiteSpace(raw.ToString()))
                {
                    continue;
                }
                var remembered = memory.TryGetValue(FormChooserSourceMemo.EntryKey(main.Key, rowKey), out var serial) ? serial : (int?)null;
                var source = FormChooserSourceMemo.SelectSource(main, remembered);
                if (source is null)
                {
                    continue;
                }
                await ResolveChooserGroupAsync(connection, transaction, main, source, companionsByGroup[main.CellGroup!], row, raw.ToString()!, token);
            }
        }
    }

    /// <summary>
    /// 复合格从字段（同组展示列）：只认版式给出的 CellRole/CellGroup。
    ///
    /// 这里不再要求 DisplayOnly：该标记源自字段级的 FORM_CELL_ROLE（那一列已退役、读取时恒为 0），
    /// 而版式的 CellRole 是在它之后才施加的，卡 DisplayOnly 会让所有从字段都进不来，
    /// 于是"选择器回写出来的名称列"在重新打开单据时永远回填不上。
    /// </summary>
    private static Dictionary<string, List<FormFieldDefinition>> GroupCompanions(IReadOnlyList<FormFieldDefinition> fields) =>
        fields
            .Where(field => field.CellRole == 2 && !string.IsNullOrWhiteSpace(field.CellGroup))
            .GroupBy(field => field.CellGroup!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.OrdinalIgnoreCase);

    /// <summary>有同组展示从字段、且存在可用来源的复合格主字段（无可用来源时无需解析）。</summary>
    private static List<FormFieldDefinition> MainFieldsWithChoosers(
        IReadOnlyList<FormFieldDefinition> fields,
        Dictionary<string, List<FormFieldDefinition>> companionsByGroup) =>
        fields
            .Where(field => field.CellRole == 1
                && !string.IsNullOrWhiteSpace(field.CellGroup)
                && companionsByGroup.ContainsKey(field.CellGroup!)
                && FormChooserSourceMemo.SelectSource(field, null) is not null)
            .ToList();

    private static async Task ResolveChooserGroupAsync(
        SqlConnection connection,
        SqlTransaction? transaction,
        FormFieldDefinition main,
        FieldChooserSource source,
        IReadOnlyList<FormFieldDefinition> companions,
        Dictionary<string, object?> row,
        string value,
        CancellationToken token)
    {
        var table = source.Table!.Trim();
        if (!WorkbenchSql.Identifier.IsMatch(table) || !await WorkbenchSql.TableExistsAsync(connection, table, token, transaction))
        {
            return;
        }

        // Return mapping consumed as ordered RETURN_ITEMS JSON
        var mapping = ChooserReturnItems.Parse(source.ReturnMapping) ?? [];
        var keyColumn = mapping.FirstOrDefault(pair => string.Equals(FormFieldSelector.NormalizeChooserTarget(pair.Target), main.Key, StringComparison.OrdinalIgnoreCase))?.Column;
        if (string.IsNullOrWhiteSpace(keyColumn))
        {
            keyColumn = main.Key;
        }
        if (!WorkbenchSql.Identifier.IsMatch(keyColumn))
        {
            return;
        }

        var selected = new List<(FormFieldDefinition Field, string Column)>();
        foreach (var companion in companions)
        {
            var column = mapping.FirstOrDefault(pair => string.Equals(FormFieldSelector.NormalizeChooserTarget(pair.Target), companion.Key, StringComparison.OrdinalIgnoreCase))?.Column;
            if (string.IsNullOrWhiteSpace(column))
            {
                column = companion.Key;
            }
            if (!WorkbenchSql.Identifier.IsMatch(column))
            {
                return;
            }
            selected.Add((companion, column));
        }

        var columns = selected.Select(item => item.Column).Append(keyColumn).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (!await WorkbenchSql.ColumnsExistAsync(connection, table, columns, token, transaction))
        {
            return;
        }

        var select = string.Join(",", columns.Select(column => $"[{column}]"));
        await using var command = new SqlCommand($"SELECT TOP 1 {select} FROM dbo.[{table}] WHERE [{keyColumn}]=@Value;", connection, transaction);
        command.Parameters.Add("@Value", SqlDbType.NVarChar, 256).Value = value;
        await using var reader = await command.ExecuteReaderAsync(token);
        if (!await reader.ReadAsync(token))
        {
            return;
        }
        foreach (var (field, column) in selected)
        {
            row[field.Key] = reader.IsDBNull(reader.GetOrdinal(column)) ? null : reader.GetValue(reader.GetOrdinal(column));
        }
    }

    private static string? NormalizeIdempotencyKey(string? key)
    {
        var normalized = key?.Trim();
        return string.IsNullOrWhiteSpace(normalized) || normalized.Length > 128 ? null : normalized;
    }

    /// <summary>从唯一键冲突异常消息解析索引名（英文与中文两种消息句式）。解析失败时返回 false。</summary>
    private static bool TryParseDuplicateIndexName(string message, out string indexName)
    {
        // 英文：Cannot insert duplicate key row in object 'dbo.X' with unique index 'IX_NAME'.
        //        Violation of PRIMARY KEY constraint 'PK_X'. Cannot insert duplicate key in object 'dbo.X'.
        // 中文：违反了 PRIMARY KEY 约束“PK_X”。不能在对象“dbo.X”中插入重复键。
        //        违反了 UNIQUE KEY 约束“UQ_X”。不能在对象“dbo.X”中插入重复键。
        // 2627（主键冲突）与 2601（唯一索引冲突）消息结构一致。两种句式都要认：
        // 只认英文时，中文实例上解析必然失败，"单号冲突"那条分支会静默退化成上抛。
        var match = System.Text.RegularExpressions.Regex.Match(
            message,
            @"(?:with\s+(?:unique\s+index|PRIMARY\s+KEY)|(?:PRIMARY|UNIQUE)\s+KEY\s*约束)\s*['\u201C\u201D](?<name>[^'\u201C\u201D]+)['\u201C\u201D]",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));
        indexName = match.Success ? match.Groups["name"].Value : string.Empty;
        return match.Success;
    }

    /// <summary>冲突索引的列清单与是否为主键。</summary>
    internal sealed record ConflictIndex(IReadOnlyList<string> Columns, bool IsPrimaryKey);

    /// <summary>冲突索引覆盖到了该列，或冲突索引就是主键且覆盖到某个主键列。</summary>
    internal static bool IndexCoversBillNo(ConflictIndex conflict, IReadOnlyList<string> pkColumns, string billNoField) =>
        conflict.Columns.Any(column => column.Equals(billNoField, StringComparison.OrdinalIgnoreCase))
        || (conflict.IsPrimaryKey && pkColumns.Any(pk => conflict.Columns.Contains(pk, StringComparer.OrdinalIgnoreCase)));

    /// <summary>
    /// 读出冲突索引的列：判定"撞的是哪一列"用，从而决定把字段错误挂到哪个主键上。
    /// </summary>
    private static async Task<ConflictIndex> ReadConflictIndexColumnsAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        string table,
        string indexName,
        CancellationToken token)
    {
        const string sql = """
            SELECT c.name AS COLUMN_NAME,i.is_primary_key AS IS_PK
            FROM sys.indexes i
            JOIN sys.index_columns ic ON i.object_id=ic.object_id AND i.index_id=ic.index_id
            JOIN sys.columns c ON ic.object_id=c.object_id AND ic.column_id=c.column_id
            WHERE i.name=@IndexName AND i.object_id=OBJECT_ID(N'dbo.' + @Table);
            """;
        await using var command = new SqlCommand(sql, connection, transaction);
        command.Parameters.Add("@IndexName", SqlDbType.NVarChar, 256).Value = indexName;
        command.Parameters.Add("@Table", SqlDbType.NVarChar, 256).Value = table;
        var columns = new List<string>();
        var isPrimaryKey = false;
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
        {
            columns.Add(reader.GetString(reader.GetOrdinal("COLUMN_NAME")));
            isPrimaryKey |= reader.GetBoolean(reader.GetOrdinal("IS_PK"));
        }
        return new ConflictIndex(columns, isPrimaryKey);
    }

    private static string SerializeResultKey(IReadOnlyList<string> keyValues) => JsonSerializer.Serialize(keyValues);

    private static IReadOnlyList<string> ParseResultKey(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<string[]>(json) ?? [];
        }
        catch
        {
            return json.Split(',', StringSplitOptions.RemoveEmptyEntries);
        }
    }

    private static string ValueToString(object? value) => WorkbenchSql.ValueToString(value);

    /// <summary>判断状态位列是否为真：兼容 bit 列返回的 bool，以及历史遗留的 1/0 数值或字符串。</summary>
    private static bool IsStatusTrue(IReadOnlyDictionary<string, object?> row, string column)
    {
        if (!row.TryGetValue(column, out var value) || value is null) return false;
        return value switch
        {
            bool b => b,
            string s => s.Trim() == "1" || s.Trim().Equals("true", StringComparison.OrdinalIgnoreCase),
            int i => i != 0,
            long l => l != 0,
            byte b2 => b2 != 0,
            short s2 => s2 != 0,
            decimal d => d != 0,
            _ => false,
        };
    }

    private static bool ValuesEqual(object? left, object? right) => WorkbenchSql.ValuesEqual(left, right);

    private SqlConnection CreateConnection() => connections.Create();
}
