using System.Data;
using System.Text.Json;
using EOS.API.Models;
using EOS.API.Telemetry;
using Microsoft.Data.SqlClient;

namespace EOS.API.Data;

/// <summary>
/// 工作台命令处理器（ADR-005 §2 组件表 WorkbenchCommandHandler，阶段 3）：
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
    DomainRuleService domainRules,
    ControlledSprocInvoker controlledSprocs,
    WorkbenchIdempotency idempotency,
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
        foreach (var statusColumn in new[] { "CONFIRM_TAG", "CONFIRM_PERSON", "CONFIRM_DATE", "FINISHED_TAG" })
        {
            if (!masterFields.Contains(statusColumn, StringComparer.OrdinalIgnoreCase)
                && await WorkbenchSql.ColumnExistsAsync(connection, null, definition.MasterTable, statusColumn, token))
            {
                masterFields.Add(statusColumn);
            }
        }
        var current = await WorkbenchSql.ReadRowAsync(connection, null, definition.MasterTable, pkColumns, keyValues, masterFields, token);
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
        await ResolveChooserDisplaysAsync(connection, form.MasterFields, current, token);
        var detailRows = new List<IReadOnlyDictionary<string, object?>>();
        if (definition.DetailTable is not null && form.DetailFields.Count > 0)
        {
            var detailFields = form.DetailFields.Where(field => !field.DisplayOnly && !field.IsVirtual).Select(field => field.Key).Concat(pkColumns).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            detailRows.AddRange(await WorkbenchSql.ReadRowsAsync(connection, null, definition.DetailTable, pkColumns, keyValues, detailFields, token));
        }
        return new(RecordAccessStatus.Ok, new RecordBundle(current, detailRows));
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

        // ADR-005 §7 配套：主表存在 OWNER/OWNER_G 时回填制单人/主组
        if (await WorkbenchSql.ColumnExistsAsync(connection, transaction, definition.MasterTable, "OWNER", token))
        {
            values.TryAdd("OWNER", userId);
        }
        if (await WorkbenchSql.ColumnExistsAsync(connection, transaction, definition.MasterTable, "OWNER_G", token))
        {
            var primaryGroup = await WorkbenchSql.GetPrimaryGroupAsync(connection, transaction, userId, token);
            if (primaryGroup is not null)
            {
                values.TryAdd("OWNER_G", primaryGroup);
            }
        }

        // 领域规则：自动单号 + 默认单别（等价旧 GetNewBillNo / GetDefaultBillInfo）
        var businessRule = definition.BusinessRule;
        if (businessRule?.SprocPendingPorting == true)
        {
            return RecordSaveResult.Failed(RecordAccessStatus.ValidationFailed, "SP_NOT_PORTED",
                "该模块的存盘后处理逻辑尚未移植，禁止保存。");
        }
        if (businessRule is { AutoBillNo: true, BillNoField: not null, BillTypeField: not null })
        {
            var existingNo = values.GetValueOrDefault(businessRule.BillNoField);
            if (existingNo is null || string.IsNullOrWhiteSpace(ValueToString(existingNo)))
            {
                var newNo = await BillNoGenerator.GenerateAsync(connection, transaction, definition.ModuleId,
                    definition.MasterTable, businessRule.BillNoField, businessRule.BillTypeField, token);
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

        var insertFields = form.MasterFields.Where(field => !field.IsVirtual && values.ContainsKey(field.Key) && !masterIdentity.Contains(field.Key)).ToList();
        if (insertFields.Count == 0)
        {
            return RecordSaveResult.Failed(RecordAccessStatus.ValidationFailed, "NO_WRITABLE_FIELDS", "没有可写入的字段。");
        }
        decimal? identityValue;
        try
        {
            identityValue = await InsertRowAsync(connection, transaction, definition.MasterTable, insertFields, values, masterIdentity, token);
        }
        // ADR-006 决策 2.8（单号冲突友好化）：自动单号模块的默认单号只是预览号，并发开单会撞唯一键；
        // 仅当冲突索引命中主键/单号列时映射为 BILL_NO_CONFLICT（400），其余唯一键冲突保持原样上抛。
        catch (SqlException ex) when (ex.Number is 2601 or 2627)
        {
            if (businessRule is { AutoBillNo: true, BillNoField: not null }
                && TryParseDuplicateIndexName(ex.Message, out var duplicateIndexName)
                && await IsBillNoUniqueConflictAsync(connection, transaction, definition.MasterTable, duplicateIndexName, pkColumns, businessRule.BillNoField, token))
            {
                return RecordSaveResult.Failed(RecordAccessStatus.ValidationFailed, "BILL_NO_CONFLICT",
                    "单号已被占用，请重新保存以获取新单号。");
            }
            throw;
        }
        if (identityValue is not null && masterIdentity.Count > 0)
        {
            values[masterIdentity[0]] = identityValue;
            keyValues = pkColumns.Select(column => ValueToString(values.GetValueOrDefault(column))).ToList();
        }

        // ADR-005 §7 收紧（2026-08-23）：建单后记录必须处于模块契约内（模块 FILTER + DATA_FILTER + EXEC_TAG）
        if (!scopeFilter.TryBuildRecordScopePredicate(definition, dataFilter, out var scopePredicate, out var scopeParameters))
        {
            return RecordSaveResult.Failed(RecordAccessStatus.FilterUnsupported, "DATA_FILTER_UNSUPPORTED", "当前数据过滤条件尚不支持，已拒绝执行。");
        }
        if (!string.IsNullOrWhiteSpace(scopePredicate)
            && !await WorkbenchSql.RecordInScopeAsync(connection, transaction, definition.MasterTable, pkColumns, keyValues, scopePredicate, scopeParameters, token))
        {
            return RecordSaveResult.Failed(RecordAccessStatus.ValidationFailed, "RECORD_OUT_OF_MODULE_FILTER", "新建记录不满足模块过滤条件，无法保存。");
        }

        var detailErrors = await SaveDetailsAsync(connection, transaction, definition, form, pkColumns, keyValues, values, request.Details ?? [], employeeName, true, token);
        if (detailErrors is not null)
        {
            return RecordSaveResult.Failed(RecordAccessStatus.ValidationFailed, "VALIDATION_FAILED", "明细数据校验未通过。", detailErrors);
        }
        await SavePrepayOffsetsAsync(connection, transaction, businessRule, pkColumns, keyValues, request.PrepayOffsets, token);
        if (businessRule?.DomainRule is { } domainRule)
        {
            var domainResult = await domainRules.RunAfterSaveAsync(domainRule, connection, transaction, definition, pkColumns, keyValues, token);
            if (!domainResult.Success)
            {
                return RecordSaveResult.Failed(RecordAccessStatus.ValidationFailed, "BUSINESS_VALIDATION_FAILED",
                    domainResult.Message ?? "保存后业务校验未通过。");
            }
        }
        else if (businessRule?.AfterSaveSproc is { } afterSaveSproc)
        {
            var sprocResult = await controlledSprocs.RunAfterSaveAsync(definition.ModuleId, afterSaveSproc, pkColumns, keyValues, connection, transaction, token);
            if (!sprocResult.Success)
            {
                return RecordSaveResult.Failed(RecordAccessStatus.ValidationFailed, "BUSINESS_VALIDATION_FAILED",
                    sprocResult.Message ?? "保存后业务校验未通过。");
            }
        }
        await RecalculateMasterAmountsAsync(connection, transaction, definition, token);
        await auditWriter.WriteEventAsync(connection, transaction, definition.ModuleId, string.Join(',', keyValues),
            "INSERT", "新增记录", userId, "WORKBENCH_RECORD", result: 1, fieldChanges: null, token);
        if (idempotencyKey is not null)
        {
            await idempotency.CompleteAsync(connection, transaction, idempotencyKey, SerializeResultKey(keyValues), false, token);
        }
        await transaction.CommitAsync(token);
        logger.LogInformation("统一表单新增 module={ModuleId} master={Master} key={Key}", definition.ModuleId, definition.MasterTable, string.Join(',', keyValues));
        IReadOnlyList<SaveWarning>? warnings = null;
        if (definition.AutoApprove)
        {
            // 自动批核模块：新增成功后立即进入批核状态（保存事务提交后执行，SP 自带事务）；
            // 失败不回滚保存（ADR-006 决策 2.7）：以 warnings 回传前端提示「已保存，但自动批核失败」。
            var autoResult = await approvalService.AutoApproveAsync(connection, definition, keyValues, userId, token);
            if (autoResult.Status != RecordAccessStatus.Ok)
            {
                logger.LogWarning("自动批核失败 module={ModuleId} key={Key} code={Code} message={Message}",
                    definition.ModuleId, string.Join(',', keyValues), autoResult.ErrorCode, autoResult.ErrorMessage);
                warnings = [new SaveWarning("AUTO_APPROVE_FAILED",
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

        if (definition.BusinessRule?.SprocPendingPorting == true)
        {
            return RecordSaveResult.Failed(RecordAccessStatus.ValidationFailed, "SP_NOT_PORTED",
                "该模块的存盘后处理逻辑尚未移植，禁止保存。");
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
        // 状态校验（对齐删除补偿守卫）：已结案 / 已批核的单据禁止编辑
        if (IsStatusTrue(current, "FINISHED_TAG"))
        {
            return RecordSaveResult.Failed(RecordAccessStatus.ValidationFailed, "FINISHED_EDIT_FORBIDDEN", "记录已结案，禁止编辑（请先取消结案）。");
        }
        if (IsStatusTrue(current, "CONFIRM_TAG"))
        {
            return RecordSaveResult.Failed(RecordAccessStatus.ValidationFailed, "CONFIRMED_EDIT_FORBIDDEN", "记录已批核，禁止编辑（请先解批）。");
        }
        // ADR-005 §7 收紧：编辑前目标记录必须处于模块契约内
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
            var affected = await command.ExecuteNonQueryAsync(token);
            if (affected == 0)
            {
                return RecordSaveResult.Failed(RecordAccessStatus.NotFound, "RECORD_NOT_FOUND", "记录不存在。");
            }
        }

        var detailErrors = await SaveDetailsAsync(connection, transaction, definition, form, pkColumns, keyValues, merged, request.Details ?? [], employeeName, false, token);
        if (detailErrors is not null)
        {
            return RecordSaveResult.Failed(RecordAccessStatus.ValidationFailed, "VALIDATION_FAILED", "明细数据校验未通过。", detailErrors);
        }
        var businessRule = definition.BusinessRule;
        await SavePrepayOffsetsAsync(connection, transaction, businessRule, pkColumns, keyValues, request.PrepayOffsets, token);
        if (businessRule?.DomainRule is { } domainRule)
        {
            var domainResult = await domainRules.RunAfterSaveAsync(domainRule, connection, transaction, definition, pkColumns, keyValues, token);
            if (!domainResult.Success)
            {
                return RecordSaveResult.Failed(RecordAccessStatus.ValidationFailed, "BUSINESS_VALIDATION_FAILED",
                    domainResult.Message ?? "保存后业务校验未通过。");
            }
        }
        else if (businessRule?.AfterSaveSproc is { } afterSaveSproc)
        {
            var sprocResult = await controlledSprocs.RunAfterSaveAsync(definition.ModuleId, afterSaveSproc, pkColumns, keyValues, connection, transaction, token);
            if (!sprocResult.Success)
            {
                return RecordSaveResult.Failed(RecordAccessStatus.ValidationFailed, "BUSINESS_VALIDATION_FAILED",
                    sprocResult.Message ?? "保存后业务校验未通过。");
            }
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
        return RecordSaveResult.Success(keyValues);
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
        // ADR-005 §7 收紧：删除前目标记录必须处于模块契约内
        if (!scopeFilter.TryBuildRecordScopePredicate(definition, dataFilter, out var scopePredicate, out var scopeParameters))
        {
            return RecordSaveResult.Failed(RecordAccessStatus.FilterUnsupported, "DATA_FILTER_UNSUPPORTED", "当前数据过滤条件尚不支持，已拒绝执行。");
        }
        if (!string.IsNullOrWhiteSpace(scopePredicate)
            && !await WorkbenchSql.RecordInScopeAsync(connection, transaction, definition.MasterTable, pkColumns, keyValues, scopePredicate, scopeParameters, token))
        {
            return RecordSaveResult.Failed(RecordAccessStatus.OutOfScope, "RECORD_OUT_OF_SCOPE", "目标记录不在当前用户数据范围内。");
        }
        // 删除补偿守卫（ADR-005 §2）：结案单据/已批核有副作用单据禁止删除
        var guard = await approvalService.EnsureDeletionAllowedAsync(connection, transaction, definition, keyValues, token);
        if (guard is not null)
        {
            return guard;
        }
        if (definition.DetailTable is not null)
        {
            await WorkbenchSql.DeleteDetailRowsAsync(connection, transaction, definition.DetailTable, pkColumns, keyValues, token);
        }
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

    private async Task<IReadOnlyList<FieldError>?> SaveDetailsAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        WorkbenchDefinition definition,
        FormDefinition form,
        IReadOnlyList<string> pkColumns,
        IReadOnlyList<string> keyValues,
        IReadOnlyDictionary<string, object?> masterValues,
        IReadOnlyList<IReadOnlyDictionary<string, string?>> details,
        string employeeName,
        bool isNew,
        CancellationToken token)
    {
        if (definition.DetailTable is null || form.DetailFields.Count == 0)
        {
            return null;
        }
        if (details.Count == 0)
        {
            if (definition.DetailNoSave)
            {
                return [new FieldError("", "该模块无明细资料不可保存。", "DETAIL_REQUIRED")];
            }
            await WorkbenchSql.DeleteDetailRowsAsync(connection, transaction, definition.DetailTable, pkColumns, keyValues, token);
            return null;
        }

        if (!string.IsNullOrWhiteSpace(definition.DetailNoFields))
        {
            var missing = definition.DetailNoFields.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Where(field => !masterValues.TryGetValue(field, out var value) || value is null || value is string text && string.IsNullOrWhiteSpace(text))
                .ToList();
            if (missing.Count > 0)
            {
                return missing.Select(field => new FieldError(field, "新增明细前必须填写该主表字段。", "DETAIL_NO_FIELDS_MISSING")).ToList();
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
            // ADR-006 决策 2.2：明细行错误携带行号，前端按行精确定位（不再全部挂到第一行）
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
        RecordPayloadValidator.AssignSerialNumbers(rows, form.DetailFields);
        // 应收货款单（170101）等引用单据的明细：金额/数量从送货（退货）单明细带出
        await FillReferencedAmountsAsync(connection, transaction, form.DetailFields, rows, token);
        for (var rowIndex = 0; rowIndex < rows.Count; rowIndex++)
        {
            errors.AddRange(RecordPayloadValidator.CheckRequiredAndRegex(form.DetailFields, rows[rowIndex])
                .Select(error => error with { RowIndex = rowIndex }));
        }
        if (errors.Count > 0)
        {
            return errors;
        }

        if (dfFields.Length > 0)
        {
            var groups = rows.GroupBy(row => string.Join('\u0001', dfFields.Select(field => Convert.ToString(row.GetValueOrDefault(field) ?? string.Empty, System.Globalization.CultureInfo.InvariantCulture))));
            var duplicate = groups.FirstOrDefault(group => group.Count() > 1);
            if (duplicate is not null)
            {
                return [new FieldError(string.Join(';', dfFields), $"明细表资料重复：{string.Join(';', dfFields)}。", "DF_VERIFY_DUPLICATE")];
            }
        }

        await WorkbenchSql.DeleteDetailRowsAsync(connection, transaction, definition.DetailTable, pkColumns, keyValues, token);
        for (var rowIndex = 0; rowIndex < rows.Count; rowIndex++)
        {
            var row = rows[rowIndex];
            var fillErrors = await FillServerColumnsAsync(connection, transaction, definition.DetailTable, form.DetailFields, row, employeeName, isNew, token);
            if (fillErrors.Count > 0)
            {
                return fillErrors.Select(error => error with { RowIndex = rowIndex }).ToList();
            }
            var insertFields = form.DetailFields.Where(field => !field.IsVirtual && row.ContainsKey(field.Key) && !detailIdentity.Contains(field.Key)).ToList();
            if (insertFields.Count == 0)
            {
                continue;
            }
            await InsertRowAsync(connection, transaction, definition.DetailTable, insertFields, row, detailIdentity, token);
        }
        return null;
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

    /// <summary>主表金额汇总与余额字段初始化（明细聚合 + PREPAY/RECEIVE 余额）。</summary>
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
        IReadOnlyList<FormFieldDefinition> insertFields,
        IReadOnlyDictionary<string, object?> values,
        IReadOnlyList<string> identityColumns,
        CancellationToken token)
    {
        var columns = string.Join(',', insertFields.Select(field => $"[{field.Key}]"));
        var parameters = string.Join(',', insertFields.Select((_, index) => $"@v{index}"));
        var sql = $"INSERT INTO dbo.[{table}] ({columns}) VALUES ({parameters});";
        await using var command = new SqlCommand(sql, connection, transaction);
        for (var i = 0; i < insertFields.Count; i++)
        {
            command.Parameters.AddWithValue($"@v{i}", WorkbenchSql.NormalizeDbValue(values[insertFields[i].Key]));
        }
        if (identityColumns.Count > 0)
        {
            command.CommandText += " SELECT SCOPE_IDENTITY();";
            return Convert.ToInt32(await command.ExecuteScalarAsync(token));
        }
        await command.ExecuteNonQueryAsync(token);
        return null;
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

    /// <summary>选择器同组展示字段回填（DisplayOnly CellRole=2 伴随主 CellRole=1）。</summary>
    private static async Task ResolveChooserDisplaysAsync(
        SqlConnection connection,
        IReadOnlyList<FormFieldDefinition> fields,
        Dictionary<string, object?> row,
        CancellationToken token)
    {
        var companionsByGroup = fields
            .Where(field => field.DisplayOnly && field.CellRole == 2 && !string.IsNullOrWhiteSpace(field.CellGroup))
            .GroupBy(field => field.CellGroup!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.OrdinalIgnoreCase);
        if (companionsByGroup.Count == 0)
        {
            return;
        }

        foreach (var main in fields.Where(field =>
                     field.CellRole == 1 && !string.IsNullOrWhiteSpace(field.CellGroup)
                     && field.Choosers.Any(source => source.Active && !string.IsNullOrWhiteSpace(source.Table))))
        {
            if (!companionsByGroup.TryGetValue(main.CellGroup!, out var companions))
            {
                continue;
            }
            if (!row.TryGetValue(main.Key, out var raw) || raw is null || string.IsNullOrWhiteSpace(raw.ToString()))
            {
                continue;
            }
            var source = main.Choosers.First(item => item.Active && !string.IsNullOrWhiteSpace(item.Table));
            await ResolveChooserGroupAsync(connection, main, source, companions, row, raw.ToString()!, token);
        }
    }

    private static async Task ResolveChooserGroupAsync(
        SqlConnection connection,
        FormFieldDefinition main,
        FieldChooserSource source,
        IReadOnlyList<FormFieldDefinition> companions,
        Dictionary<string, object?> row,
        string value,
        CancellationToken token)
    {
        var table = source.Table!.Trim();
        if (!WorkbenchSql.Identifier.IsMatch(table) || !await WorkbenchSql.TableExistsAsync(connection, table, token))
        {
            return;
        }

        var mapping = FormFieldSelector.ParseReturnMapping(source.ReturnMapping);
        var keyColumn = mapping.FirstOrDefault(pair => string.Equals(FormFieldSelector.NormalizeChooserTarget(pair.Target), main.Key, StringComparison.OrdinalIgnoreCase)).Column;
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
            var column = mapping.FirstOrDefault(pair => string.Equals(FormFieldSelector.NormalizeChooserTarget(pair.Target), companion.Key, StringComparison.OrdinalIgnoreCase)).Column;
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
        if (!await WorkbenchSql.ColumnsExistAsync(connection, table, columns, token))
        {
            return;
        }

        var select = string.Join(",", columns.Select(column => $"[{column}]"));
        await using var command = new SqlCommand($"SELECT TOP 1 {select} FROM dbo.[{table}] WHERE [{keyColumn}]=@Value;", connection);
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

    /// <summary>从唯一键冲突异常消息解析索引名（ADR-006 决策 2.8）。消息本地化导致解析失败时返回 false（保持原样上抛）。</summary>
    private static bool TryParseDuplicateIndexName(string message, out string indexName)
    {
        // 英文：Cannot insert duplicate key row in object 'dbo.X' with unique index 'IX_NAME'.
        // 2627（主键冲突）与 2601（唯一索引冲突）消息结构一致。
        var match = System.Text.RegularExpressions.Regex.Match(
            message, @"with\s+(unique\s+index|PRIMARY\s+KEY)\s+'(?<name>[^']+)'",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));
        indexName = match.Success ? match.Groups["name"].Value : string.Empty;
        return match.Success;
    }

    /// <summary>
    /// 判定唯一键冲突是否为「单号冲突」（ADR-006 决策 2.8 范围限定）：
    /// 冲突索引的列命中单号列，或冲突索引即主键且主键含主键列（自动单号模块主键含 单别+单号）。
    /// </summary>
    private static async Task<bool> IsBillNoUniqueConflictAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        string table,
        string indexName,
        IReadOnlyList<string> pkColumns,
        string billNoField,
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
        if (columns.Count == 0)
        {
            return false;
        }
        if (columns.Any(column => column.Equals(billNoField, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }
        return isPrimaryKey && pkColumns.Any(pk => columns.Contains(pk, StringComparer.OrdinalIgnoreCase));
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