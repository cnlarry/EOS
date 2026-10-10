using System.Data;
using EOS.API.Data;
using EOS.API.Data.Workbench;
using EOS.API.Models;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;

namespace EOS.API.Features.Import;

/// <summary>请求本身不合法（映射缺主键、字段不在定义里、行数超限）。</summary>
internal sealed class ImportRequestException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

/// <summary>
/// 基本资料导入：把表格文件里的行，按目标**模块**逐行走统一表单的新增写路径。
///
/// <para>
/// 这里不自己校验、不自己拼 INSERT。每一行都提交给 <see cref="DocumentWorkbenchRepository.CreateRecordAsync"/>，
/// 于是字段级"禁新增"限制、类型/长度/精度/正则、元数据默认值、自动单号、服务端填充列、
/// 只读联动列、SAVE 效果链、模块过滤判定、审计全部与手工录入逐字一致——导入不是旁路，
/// 只是另一个调用方。
/// </para>
/// <para>
/// 预演 = <c>dryRun</c>：同一段代码在真实事务里跑完再无条件回滚，因此"预演通过"与
/// "执行通过"是同一条判据，不是两份实现。
/// </para>
/// <para>
/// **逐行独立提交**：每行的成败互不影响（每行自带事务）。失败行已成功落库的部分不会被回滚，
/// 因此结果必须逐行回报、由使用者在界面上看到哪几行进去了。
/// </para>
/// </summary>
public sealed class ImportService(
    DbConnectionFactory connections,
    DocumentWorkbenchRepository workbench,
    WorkbenchAuditWriter auditWriter,
    ImportMappingStore mappings,
    IOptions<UnifiedFormEditorSettings> formSettings,
    ILogger<ImportService> logger)
{
    /// <summary>
    /// 可导入的目标模块 = 统一表单写名单 ∩ 统一工作台模块（有主表且按工作台承载）。
    ///
    /// <para>
    /// 判据只此两处，没有第三处"允许导入"的人工标记：能写就能导——导入与手工录入是同一个
    /// 权限面，多一处开关只会多一处能配错且必须与权限对齐的东西。
    /// </para>
    /// <para>
    /// 这里**不按当前用户过滤**：与其它选择器数据源同一口径（候选是目录，能否动由目标端点裁决）。
    /// 用户选到没有新增权限的模块时，定义端点会明说缺哪个动作位。
    /// </para>
    /// </summary>
    public async Task<IReadOnlyList<ImportTarget>> GetTargetsAsync(CancellationToken token)
    {
        var writeList = formSettings.Value.EnabledModuleIds;
        if (writeList.Length == 0)
        {
            return [];
        }
        var placeholders = writeList.Select((_, index) => $"@m{index}").ToArray();
        var sql = $"""
            SELECT m.M_IDX, LTRIM(RTRIM(ISNULL(m.M_DESC,''))), LTRIM(RTRIM(ISNULL(m.MASTER_TABLE,'')))
            FROM dbo.MODULES m WITH (NOLOCK)
            INNER JOIN dbo.V_MODULE_NODE n WITH (NOLOCK) ON n.M_IDX=m.M_IDX
            WHERE n.NODE_KIND=N'WORKBENCH' AND m.M_IDX IN ({string.Join(',', placeholders)})
            ORDER BY LTRIM(RTRIM(ISNULL(m.M_DESC,''))), m.M_IDX;
            """;
        await using var connection = connections.Create();
        await connection.OpenAsync(token);
        await using var command = new SqlCommand(sql, connection);
        for (var index = 0; index < writeList.Length; index++)
        {
            command.Parameters.Add($"@m{index}", SqlDbType.Int).Value = writeList[index];
        }
        await using var reader = await command.ExecuteReaderAsync(token);
        var targets = new List<ImportTarget>();
        while (await reader.ReadAsync(token))
        {
            targets.Add(new ImportTarget(reader.GetInt32(0), reader.GetString(1), reader.GetString(2)));
        }
        return targets;
    }

    /// <summary>
    /// 由服务端表单定义（新增态）投影出导入定义：可填列、必填、主键、必须映射的键。
    /// 被"禁新增"的字段不在这里出现——它压根不在表单定义里，提交它会被拒（无需另加开关）。
    /// </summary>
    public ImportDefinitionInfo GetDefinition(WorkbenchFormAccess access)
    {
        var form = access.Form;
        var fields = form.MasterFields.Where(IsMappable).Select(field => new ImportFieldInfo(
            field.Key,
            field.Label,
            field.DataType,
            field.DisplayFormat,
            field.IsRequired,
            field.IsPrimaryKey,
            field.IsAutoIncrement,
            field.MaxLength)).ToList();
        return new ImportDefinitionInfo(
            form.ModuleId,
            form.Title,
            form.MasterTable,
            fields,
            ResolveRequiredKeys(access, fields));
    }

    /// <summary>
    /// 必须由数据文件提供的主键列：主键里不由数据库自增、也不由服务端生成的那些。
    /// 服务端持有的（自增列、自动单号）不算——把它们列成必填，用户就得填一个自己也不知道的值。
    /// </summary>
    private static IReadOnlyList<string> ResolveRequiredKeys(WorkbenchFormAccess access, IReadOnlyList<ImportFieldInfo> fields)
    {
        var generatedBillNo = access.Definition.BusinessRule is { AutoBillNo: true, BillNoField: { } billNoField }
            ? billNoField
            : null;
        return fields
            .Where(field => field.IsPrimaryKey
                && !field.IsAutoIncrement
                && (generatedBillNo is null || !generatedBillNo.Equals(field.Key, StringComparison.OrdinalIgnoreCase)))
            .Select(field => field.Key)
            .ToList();
    }

    /// <summary>可由数据文件提供的列：虚拟列、服务端填充列、展示专用列、自增列都不参与映射。</summary>
    private static bool IsMappable(FormFieldDefinition field) =>
        !field.IsVirtual
        && !field.ServerFilled
        && !field.DisplayOnly
        && !field.IsAutoIncrement
        && (!field.IsReadonly || field.IsPrimaryKey);

    public async Task<ImportRunResult> RunAsync(
        WorkbenchFormAccess access,
        ImportRunRequest request,
        string employeeName,
        string userId,
        bool dryRun,
        CancellationToken token)
    {
        var form = access.Form;
        var byKey = new Dictionary<string, FormFieldDefinition>(StringComparer.OrdinalIgnoreCase);
        foreach (var field in form.MasterFields)
        {
            byKey.TryAdd(field.Key, field);
        }
        var definition = GetDefinition(access);
        var mapping = ResolveMapping(request, byKey, definition.RequiredKeys);
        var rows = request.Rows ?? [];
        if (rows.Count > ImportLimits.MaxRows)
        {
            throw new ImportRequestException("IMPORT_TOO_MANY_ROWS",
                $"单次导入不能超过 {ImportLimits.MaxRows} 行，请拆分后重试。");
        }

        var outcomes = new List<ImportRowOutcome>(rows.Count);
        var succeeded = 0;
        for (var index = 0; index < rows.Count; index++)
        {
            var rowNumber = index + 2;
            var values = BuildValues(mapping, rows[index]);
            var outcome = await SaveRowAsync(access, values, employeeName, userId, dryRun, rowNumber, token);
            if (outcome.Ok)
            {
                succeeded++;
            }
            outcomes.Add(outcome);
        }

        var failed = outcomes.Count - succeeded;
        logger.LogInformation("导入{Mode} module={ModuleId} table={Master} rows={Rows} succeeded={Succeeded} failed={Failed}",
            dryRun ? "预演" : "执行", form.ModuleId, form.MasterTable, rows.Count, succeeded, failed);
        if (!dryRun)
        {
            await WriteBatchAuditAsync(access, request, userId, rows.Count, succeeded, failed, token);
        }
        return new ImportRunResult(dryRun, rows.Count, succeeded, failed, outcomes);
    }

    /// <summary>取该（用户 + 模块）记住的列映射；没记住过返回 null，由界面回落到按字段标签自动匹配。</summary>
    public Task<ImportMappingSnapshot?> GetMappingAsync(string userId, int moduleId, CancellationToken token) =>
        mappings.GetAsync(userId, moduleId, token);

    /// <summary>记住该（用户 + 模块）的列映射：存的是**列名**而非列下标，补导换列序也不会串位。</summary>
    public Task SaveMappingAsync(
        string userId,
        string employeeName,
        int moduleId,
        ImportMappingSaveRequest request,
        CancellationToken token) =>
        mappings.SaveAsync(userId, employeeName, moduleId, request, token);

    /// <summary>
    /// 前置资料就绪度：该模块的**必填字段**里，有数据来源而来源表还是空表的那些。
    ///
    /// <para>
    /// 判据取 `FIELD_DATASOURCE`（字段 → 来源表）而不是外键——本库刻意不用外键表达引用关系
    /// （全库仅 16 个外键），引用记在字段数据来源里。为空的来源表会被排除在外（自引用不该报）。
    /// </para>
    /// <para>
    /// 只报"来源表一行都没有"的情形：这类引用在预演期未必被判出来，但真落库会整行失败。
    /// 提前说出来，实施就能按依赖顺序先把被引用的那张表导进去。
    /// </para>
    /// </summary>
    public async Task<IReadOnlyList<ImportReadinessItem>> GetReadinessAsync(string masterTable, CancellationToken token)
    {
        if (!WorkbenchSql.Identifier.IsMatch(masterTable))
        {
            return [];
        }
        await using var connection = connections.Create();
        await connection.OpenAsync(token);
        const string sql = """
            SELECT LTRIM(RTRIM(f.F_ID)),
                   COALESCE(NULLIF(NULLIF(LTRIM(RTRIM(f.F_DESC)),''),'&nbsp;'),LTRIM(RTRIM(f.F_ID))),
                   LTRIM(RTRIM(d.SOURCE_T_ID)),
                   LTRIM(RTRIM(ISNULL(d.SOURCE_DESC,'')))
            FROM dbo.FIELDS f WITH (NOLOCK)
            JOIN dbo.FIELD_DATASOURCE d WITH (NOLOCK) ON d.T_ID=f.T_ID AND d.F_ID=f.F_ID
            WHERE f.T_ID=@Table AND COALESCE(f.IS_VERIFY,0)=1 AND COALESCE(f.IS_VIRTUAL,0)=0
              AND COALESCE(d.ACTIVE_TAG,0)=1 AND LTRIM(RTRIM(ISNULL(d.SOURCE_T_ID,'')))<>''
            ORDER BY f.F_ID;
            """;
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@Table", SqlDbType.NVarChar, 200).Value = masterTable;
        await using var reader = await command.ExecuteReaderAsync(token);
        var candidates = new List<(string Field, string Label, string Source, string SourceLabel)>();
        var seenFields = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (await reader.ReadAsync(token))
        {
            // 一个字段可以配多行来源（多来源择一），取第一行即可
            if (!seenFields.Add(reader.GetString(0))) continue;
            candidates.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3)));
        }
        await reader.DisposeAsync();
        if (candidates.Count == 0)
        {
            return [];
        }

        var items = new List<ImportReadinessItem>();
        var counted = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in candidates)
        {
            var source = candidate.Source;
            // 表名只来自库内元数据，且要真实存在（别名视图、已退役对象一律跳过）
            if (!WorkbenchSql.Identifier.IsMatch(source)) continue;
            if (!await WorkbenchSql.TableExistsAsync(connection, source, token)) continue;
            if (counted.ContainsKey(source))
            {
                continue;
            }
            await using var countCommand = new SqlCommand($"SELECT COUNT_BIG(1) FROM dbo.[{source}] WITH (NOLOCK);", connection);
            counted[source] = Convert.ToInt64(
                await countCommand.ExecuteScalarAsync(token) ?? 0L,
                System.Globalization.CultureInfo.InvariantCulture);
            items.Add(new ImportReadinessItem(
                candidate.Field,
                candidate.Label,
                source,
                candidate.SourceLabel.Length > 0 ? candidate.SourceLabel : source,
                counted[source]));
        }

        var empty = SelectEmptySources(items, masterTable);
        if (empty.Count > 0)
        {
            logger.LogInformation("导入前置资料未就绪 table={Table} emptySources={Count}", masterTable, empty.Count);
        }
        return empty;
    }

    /// <summary>
    /// 从候选里挑出"来源表一行都没有"的那些，按来源表去重（首个字段作为归属），并排除自引用。
    ///
    /// <para>
    /// 抽成纯函数是为了能单测：这段逻辑错了只会表现为"该提醒的没提醒"——界面上看不出来，
    /// 也正是这类提示最容易悄悄失效的地方。
    /// </para>
    /// </summary>
    internal static IReadOnlyList<ImportReadinessItem> SelectEmptySources(
        IReadOnlyList<ImportReadinessItem> candidates,
        string masterTable)
    {
        var result = new List<ImportReadinessItem>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in candidates)
        {
            if (candidate.Rows > 0) continue;
            // 自引用（如 CLIENT_ID → CLIENT）不算"前置资料没导"：导的就是它自己
            if (candidate.SourceTable.Equals(masterTable, StringComparison.OrdinalIgnoreCase)) continue;
            if (!seen.Add(candidate.SourceTable)) continue;
            result.Add(candidate);
            if (result.Count >= 20) break;
        }
        return result;
    }

    private async Task<ImportRowOutcome> SaveRowAsync(
        WorkbenchFormAccess access,
        IReadOnlyDictionary<string, string?> values,
        string employeeName,
        string userId,
        bool dryRun,
        int rowNumber,
        CancellationToken token)
    {
        try
        {
            // 幂等键不逐行下发：同一批所有行共用一个键只会让第一行生效。
            // 重复导入的天然防线是主键冲突——它由写路径翻译成 DUPLICATE_RECORD_KEY。
            var result = await workbench.CreateRecordAsync(
                access.Definition,
                access.Form,
                new SaveRecordRequest(values),
                employeeName,
                userId,
                access.Rights.DataFilter,
                token,
                dryRun);
            return result.Status == RecordAccessStatus.Ok
                ? new ImportRowOutcome(rowNumber, true, null, null, null)
                : new ImportRowOutcome(
                    rowNumber,
                    false,
                    result.ErrorCode ?? result.Status.ToString(),
                    result.ErrorMessage ?? "该行未通过校验。",
                    MapFieldErrors(result.FieldErrors));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (SqlException ex)
        {
            // 库错误原文可能带表名/列名/约束名：只记日志，回给用户一句可自查的说明。
            logger.LogWarning(ex, "导入行写入失败 module={ModuleId} row={Row}", access.Form.ModuleId, rowNumber);
            return new ImportRowOutcome(rowNumber, false, "IMPORT_ROW_FAILED",
                "该行写入失败，请检查数据是否符合该模块的字段要求。", null);
        }
    }

    private static IReadOnlyList<ImportRowIssue>? MapFieldErrors(IReadOnlyList<FieldError>? fieldErrors) =>
        fieldErrors is not { Count: > 0 }
            ? null
            : fieldErrors
                .Take(ImportLimits.MaxFieldErrorsPerRow)
                .Select(error => new ImportRowIssue(error.Field, error.Message, error.Code))
                .ToList();

    /// <summary>
    /// 把「列下标 → 目标字段键」解析成服务端可用的映射，并在此一次性做完结构性校验：
    /// 字段必须在该模块的可填列里、必须映射的键必须被映射。
    /// </summary>
    private static string?[] ResolveMapping(
        ImportRunRequest request,
        IReadOnlyDictionary<string, FormFieldDefinition> byKey,
        IReadOnlyList<string> requiredKeys)
    {
        var columns = request.Columns ?? [];
        var submitted = request.Mapping ?? [];
        var length = Math.Max(columns.Count, submitted.Count);
        var mapping = new string?[length];
        for (var index = 0; index < length; index++)
        {
            var requested = index < submitted.Count ? submitted[index]?.Trim() : null;
            if (string.IsNullOrEmpty(requested))
            {
                continue;
            }
            if (!byKey.TryGetValue(requested, out var field) || !IsMappable(field))
            {
                var header = index < columns.Count ? columns[index] : $"第 {index + 1} 列";
                throw new ImportRequestException("IMPORT_MAPPING_INVALID",
                    $"「{header}」映射到的字段 {requested} 不属于该模块的可填字段。");
            }
            mapping[index] = field.Key;
        }
        var mapped = mapping.Where(key => key is not null).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var missing = requiredKeys.Where(key => !mapped.Contains(key)).ToList();
        if (missing.Count > 0)
        {
            throw new ImportRequestException("IMPORT_KEY_NOT_MAPPED",
                $"必须映射主键列：{string.Join('、', missing)}。");
        }
        return mapping;
    }

    /// <summary>
    /// 空白单元格**不出现在提交里**：默认值、自动单号与服务端填充只对"没提交的值"生效，
    /// 若把空串当值提交，就会把库里的默认值顶掉（`ApplyDefaults` 只补缺失的键）。
    /// </summary>
    private static Dictionary<string, string?> BuildValues(string?[] mapping, IReadOnlyList<string> row)
    {
        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        for (var column = 0; column < mapping.Length; column++)
        {
            if (mapping[column] is not { } key)
            {
                continue;
            }
            var raw = column < row.Count ? row[column] : null;
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }
            values[key] = raw;
        }
        return values;
    }

    private async Task WriteBatchAuditAsync(
        WorkbenchFormAccess access,
        ImportRunRequest request,
        string userId,
        int total,
        int succeeded,
        int failed,
        CancellationToken token)
    {
        var source = string.IsNullOrWhiteSpace(request.SourceName) ? "(未命名)" : request.SourceName!.Trim();
        var summary = $"导入 {access.Form.Title}（{access.Form.MasterTable}）：成功 {succeeded} 行 / 失败 {failed} 行，共 {total} 行；来源 {source}";
        await auditWriter.WriteBestEffortAsync(
            access.Form.ModuleId,
            source,
            "IMPORT",
            summary,
            userId,
            "IMPORT",
            succeeded > 0 ? (byte)1 : (byte)0,
            fieldChanges: null,
            token);
    }
}
