using System.Data;
using System.Text.Json;
using EOS.API.Models;
using Microsoft.Data.SqlClient;

namespace EOS.API.Data.Forms;

/// <summary>
/// 模块级表单版式的读写仓储（设计态）。
///
/// 读：当前版式（表里无行则推导默认）+ 字段池（已注册但未排进本模块表单的字段）。
/// 写：一次保存 = 整份版式的**全量替换**（页签 + 主表行 + 明细行），同一事务 DELETE+INSERT；
/// 提交后**同请求内重发布该模块快照**——版式保存即生效（无需人工发布）。
///
/// 库对象退役与代码改动互不感知，故写入只经本仓储；发布校验器任一项失败即
/// **整笔回滚**（连同脏标记一起恢复到保存前），避免"写进去却没发布出去"的半截状态。
/// </summary>
public sealed class FormLayoutRepository(
    DbConnectionFactory connections,
    WorkbenchDirtyMarker dirtyMarker,
    WorkbenchAuditWriter auditWriter,
    WorkbenchIdempotency idempotency,
    WorkbenchDefinitionSnapshotService snapshots,
    ILogger<FormLayoutRepository> logger)
{
    private const string ResourceType = "FORM_LAYOUT";
    private const string SaveAction = "FORM_LAYOUT_SAVE";
    private const string ResetAction = "FORM_LAYOUT_RESET";

    /// <summary>该用户在该模块上是否有完整版式设计权（服务端最终边界，不依赖前端是否渲染入口）。</summary>
    public async Task<bool> CanDesignAsync(string userId, int moduleId, CancellationToken token)
    {
        await using var connection = connections.Create();
        await connection.OpenAsync(token);
        var mode = await FormDesignPermissionResolver.ResolveAsync(connection, userId, moduleId, token);
        return mode.CanDesign;
    }

    public async Task<FormLayoutDesignState?> ReadDesignStateAsync(
        int moduleId, ModuleRights rights, CancellationToken token)
    {
        await using var connection = connections.Create();
        await connection.OpenAsync(token);
        var module = await ReadModuleAsync(connection, moduleId, token);
        if (module is null)
        {
            return null;
        }

        var columns = FormLayoutDerivation.DefaultColumns;
        var layout = await FormLayoutReader.ReadAsync(
            connection, moduleId, module.MasterTable, module.DetailTable, columns, token);
        var masterFields = await WorkbenchDefinitionBuilder.ReadFormFieldRows(
            connection, module.MasterTable, module.MasterTable, token, includeVirtual: false);
        var masterFacts = await FormLayoutFactsBuilder.BuildAsync(
            connection, module.MasterTable, module.MasterTable, token);

        IReadOnlyList<FormFieldRow> detailFields = [];
        IReadOnlyDictionary<string, FormLayoutFieldFact> detailFacts =
            new Dictionary<string, FormLayoutFieldFact>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(module.DetailTable))
        {
            detailFields = await WorkbenchDefinitionBuilder.ReadFormFieldRows(
                connection, module.MasterTable, module.DetailTable, token, includeVirtual: true);
            detailFacts = await FormLayoutFactsBuilder.BuildAsync(
                connection, module.MasterTable, module.DetailTable, token);
        }

        var stamp = await ReadStampAsync(connection, null, moduleId, token);
        return new FormLayoutDesignState(
            moduleId,
            module.Title,
            module.MasterTable,
            string.IsNullOrWhiteSpace(module.DetailTable) ? null : module.DetailTable,
            columns,
            EffectiveTabs(module, layout),
            BuildTableDesign(module.MasterTable, layout.Master, masterFields, masterFacts, rights,
                isDetail: false, customized: layout.MasterCustomized),
            BuildTableDesign(
                module.DetailTable ?? string.Empty,
                layout.Detail.Select(row => new FormLayoutRow(
                    row.Key, 1, row.OrderNo, 1, 1, false, null, null, 0, row.Hidden)).ToList(),
                detailFields, detailFacts, rights, isDetail: true, customized: layout.DetailCustomized),
            stamp);
    }

    /// <summary>可套用来源：只列共用同一主表的模块（跨主表套用会把业务上不该出现的同名字段排进表单）。</summary>
    public async Task<IReadOnlyList<FormLayoutTemplate>> ReadTemplatesAsync(int moduleId, CancellationToken token)
    {
        await using var connection = connections.Create();
        await connection.OpenAsync(token);
        var module = await ReadModuleAsync(connection, moduleId, token);
        if (module is null)
        {
            return [];
        }
        const string sql = """
            SELECT m.M_IDX, LTRIM(RTRIM(ISNULL(m.M_DESC,''))),
                   CAST(ISNULL(m.FORM_COLUMNS,0) AS int),
                   CASE WHEN LTRIM(RTRIM(ISNULL(m.DETAIL_TABLE,''))) = '' THEN 0 ELSE 1 END
            FROM dbo.MODULES m WITH (NOLOCK)
            WHERE LTRIM(RTRIM(ISNULL(m.MASTER_TABLE,''))) = @MasterTable AND m.M_IDX <> @ModuleId
            ORDER BY m.M_IDX;
            """;
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@MasterTable", SqlDbType.VarChar, 100).Value = module.MasterTable;
        command.Parameters.Add("@ModuleId", SqlDbType.Int).Value = moduleId;
        await using var reader = await command.ExecuteReaderAsync(token);
        var templates = new List<FormLayoutTemplate>();
        while (await reader.ReadAsync(token))
        {
            templates.Add(new FormLayoutTemplate(
                reader.GetInt32(0), reader.GetString(1), module.MasterTable,
                reader.GetInt32(2), reader.GetInt32(3) == 1));
        }
        return templates;
    }

    /// <summary>保存整份版式：校验 → 全量替换 → 标脏 → 重发布（任一步失败整笔回滚）。</summary>
    public async Task<FormLayoutSaveOutcome> SaveAsync(
        int moduleId,
        FormLayoutSaveRequest request,
        string userId,
        string executor,
        CancellationToken token)
    {
        await using var connection = connections.Create();
        await connection.OpenAsync(token);
        var module = await ReadModuleAsync(connection, moduleId, token);
        if (module is null)
        {
            return new FormLayoutSaveOutcome(FormLayoutSaveStatus.ModuleNotFound, "模块不存在或未配置主表。");
        }

        var columns = FormLayoutDerivation.DefaultColumns;
        var layout = FormLayoutSubmission.Normalize(request, columns);
        var masterFacts = await FormLayoutFactsBuilder.BuildAsync(
            connection, module.MasterTable, module.MasterTable, token);
        var detailFacts = string.IsNullOrWhiteSpace(module.DetailTable)
            ? new Dictionary<string, FormLayoutFieldFact>(StringComparer.OrdinalIgnoreCase)
            : await FormLayoutFactsBuilder.BuildAsync(connection, module.MasterTable, module.DetailTable!, token);

        var issues = FormLayoutValidator.Validate(layout, masterFacts, detailFacts);
        if (issues.Count > 0)
        {
            return new FormLayoutSaveOutcome(
                FormLayoutSaveStatus.LayoutInvalid,
                $"版式有 {issues.Count} 处不合规，未保存。",
                LayoutIssues: issues);
        }

        return await ApplyAsync(
            module, layout, request.IdempotencyKey, request.BaseUpdatedAt, userId, executor,
            SaveAction, isReset: false, token);
    }

    /// <summary>重置为默认版式：删除该模块两表全部行，回到按字段级配置推导。</summary>
    public async Task<FormLayoutSaveOutcome> ResetAsync(
        int moduleId, string? idempotencyKey, string? baseUpdatedAt, string userId, string executor, CancellationToken token)
    {
        await using var connection = connections.Create();
        await connection.OpenAsync(token);
        var module = await ReadModuleAsync(connection, moduleId, token);
        if (module is null)
        {
            return new FormLayoutSaveOutcome(FormLayoutSaveStatus.ModuleNotFound, "模块不存在或未配置主表。");
        }
        return await ApplyAsync(
            module, null, idempotencyKey, baseUpdatedAt, userId, executor, ResetAction, isReset: true, token);
    }

    private async Task<FormLayoutSaveOutcome> ApplyAsync(
        ModuleInfo module,
        FormLayoutDefinition? layout,
        string? idempotencyKey,
        string? baseUpdatedAt,
        string userId,
        string executor,
        string action,
        bool isReset,
        CancellationToken token)
    {
        var moduleId = module.ModuleId;
        await using var connection = connections.Create();
        await connection.OpenAsync(token);
        var backup = await ReadBackupAsync(connection, moduleId, token);

        await using (var transaction = (SqlTransaction)await connection.BeginTransactionAsync(token))
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(idempotencyKey))
                {
                    var claim = await idempotency.TryClaimAsync(
                        connection, transaction, idempotencyKey.Trim(), moduleId, action, token);
                    if (claim is not null)
                    {
                        await transaction.RollbackAsync(token);
                        return new FormLayoutSaveOutcome(
                            FormLayoutSaveStatus.Replayed, "同一提交已处理过，未重复写入。", ResultKey: claim.ResultKey);
                    }
                }

                if (!string.IsNullOrWhiteSpace(baseUpdatedAt))
                {
                    var current = await ReadStampAsync(connection, transaction, moduleId, token);
                    if (!string.Equals(current, baseUpdatedAt, StringComparison.Ordinal))
                    {
                        await transaction.RollbackAsync(token);
                        return new FormLayoutSaveOutcome(
                            FormLayoutSaveStatus.Conflict, "版式已被他人修改，请刷新查看最新版式后重试。");
                    }
                }

                await DeleteRowsAsync(connection, transaction, moduleId, token);
                if (layout is not null)
                {
                    await InsertLayoutAsync(connection, transaction, module, layout, userId, token);
                }
                await dirtyMarker.MarkDirtyAsync(connection, transaction, moduleId, executor, token);
                await auditWriter.WriteEventAsync(
                    connection, transaction, moduleId, $"module-{moduleId}", action,
                    isReset
                        ? $"重置模块 {moduleId} 的表单版式为默认版式。"
                        : $"保存模块 {moduleId} 的表单版式（页签 {layout!.Tabs.Count} 个、主表 {layout.Master.Count} 行、明细 {layout.Detail.Count} 行）。",
                    executor, ResourceType, result: 1, fieldChanges: null, token,
                    detailJson: layout is null ? null : JsonSerializer.Serialize(layout));
                await transaction.CommitAsync(token);
            }
            catch
            {
                await transaction.RollbackAsync(token);
                throw;
            }
        }

        var publish = await snapshots.PublishAsync([moduleId], userId, token);
        var result = publish.FirstOrDefault();
        if (result is null || !result.Passed)
        {
            logger.LogWarning("版式保存后重发布被拦截，回滚版式 module={ModuleId} checks={Checks}",
                moduleId, result?.Checks.Count(check => !check.Passed) ?? 0);
            await RestoreBackupAsync(moduleId, backup, userId, token);
            if (!string.IsNullOrWhiteSpace(idempotencyKey))
            {
                await ReleaseIdempotencyAsync(idempotencyKey.Trim(), token);
            }
            return new FormLayoutSaveOutcome(
                FormLayoutSaveStatus.PublishFailed,
                "版式已通过保存校验，但定义重发布被拦截，改动已回滚（原因见 checks）。",
                PublishChecks: result?.Checks ?? []);
        }

        if (!string.IsNullOrWhiteSpace(idempotencyKey))
        {
            await CompleteIdempotencyAsync(idempotencyKey.Trim(), result.DefinitionVersion, token);
        }

        return new FormLayoutSaveOutcome(
            FormLayoutSaveStatus.Saved,
            isReset ? "已重置为默认版式并生效。" : "已保存并生效（所有人可见）。",
            DefinitionVersion: result.DefinitionVersion,
            ResultKey: result.DefinitionVersion);
    }

    private async Task InsertLayoutAsync(
        SqlConnection connection, SqlTransaction transaction, ModuleInfo module,
        FormLayoutDefinition layout, string userId, CancellationToken token)
    {
        const string tabSql = """
            INSERT INTO dbo.MODULE_FORM_TAB (M_IDX, TAB_NO, TAB_TITLE) VALUES (@ModuleId, @TabNo, @Title);
            """;
        foreach (var tab in layout.Tabs)
        {
            await using var command = new SqlCommand(tabSql, connection, transaction);
            command.Parameters.Add("@ModuleId", SqlDbType.Int).Value = module.ModuleId;
            command.Parameters.Add("@TabNo", SqlDbType.Int).Value = tab.No;
            command.Parameters.Add("@Title", SqlDbType.NVarChar, 50).Value = tab.Title.Length == 0 ? string.Empty : tab.Title;
            await command.ExecuteNonQueryAsync(token);
        }

        const string rowSql = """
            INSERT INTO dbo.MODULE_FORM_LAYOUT
                (M_IDX, T_ID, F_ID, TAB_NO, ORDER_NO, SPAN, ROW_SPAN, NEW_LINE, SECTION_ID, CELL_GROUP, CELL_ROLE,
                 IS_HIDDEN, UPDATED_BY, UPDATED_AT)
            VALUES (@ModuleId, @Table, @Field, @TabNo, @OrderNo, @Span, @RowSpan, @NewLine, @SectionId, @CellGroup,
                    @CellRole, @Hidden, @UpdatedBy, SYSUTCDATETIME());
            """;
        foreach (var row in layout.Master)
        {
            await using var command = new SqlCommand(rowSql, connection, transaction);
            command.Parameters.Add("@ModuleId", SqlDbType.Int).Value = module.ModuleId;
            command.Parameters.Add("@Table", SqlDbType.NVarChar, 100).Value = module.MasterTable;
            command.Parameters.Add("@Field", SqlDbType.NVarChar, 100).Value = row.Key;
            command.Parameters.Add("@TabNo", SqlDbType.Int).Value = row.TabNo;
            command.Parameters.Add("@OrderNo", SqlDbType.Int).Value = row.OrderNo;
            command.Parameters.Add("@Span", SqlDbType.TinyInt).Value = (byte)row.Span;
            command.Parameters.Add("@RowSpan", SqlDbType.TinyInt).Value = (byte)row.RowSpan;
            command.Parameters.Add("@NewLine", SqlDbType.Bit).Value = row.NewLine;
            command.Parameters.Add("@SectionId", SqlDbType.NVarChar, 50).Value = (object?)row.SectionId ?? DBNull.Value;
            command.Parameters.Add("@CellGroup", SqlDbType.NVarChar, 50).Value = (object?)row.CellGroup ?? DBNull.Value;
            command.Parameters.Add("@CellRole", SqlDbType.TinyInt).Value = (byte)row.CellRole;
            command.Parameters.Add("@Hidden", SqlDbType.Bit).Value = row.Hidden;
            command.Parameters.Add("@UpdatedBy", SqlDbType.NVarChar, 50).Value = (object?)userId ?? DBNull.Value;
            await command.ExecuteNonQueryAsync(token);
        }

        if (string.IsNullOrWhiteSpace(module.DetailTable))
        {
            return;
        }
        foreach (var row in layout.Detail)
        {
            await using var command = new SqlCommand(rowSql, connection, transaction);
            command.Parameters.Add("@ModuleId", SqlDbType.Int).Value = module.ModuleId;
            command.Parameters.Add("@Table", SqlDbType.NVarChar, 100).Value = module.DetailTable;
            command.Parameters.Add("@Field", SqlDbType.NVarChar, 100).Value = row.Key;
            command.Parameters.Add("@TabNo", SqlDbType.Int).Value = 1;
            command.Parameters.Add("@OrderNo", SqlDbType.Int).Value = row.OrderNo;
            command.Parameters.Add("@Span", SqlDbType.TinyInt).Value = (byte)1;
            command.Parameters.Add("@RowSpan", SqlDbType.TinyInt).Value = (byte)1;
            command.Parameters.Add("@NewLine", SqlDbType.Bit).Value = false;
            command.Parameters.Add("@SectionId", SqlDbType.NVarChar, 50).Value = DBNull.Value;
            command.Parameters.Add("@CellGroup", SqlDbType.NVarChar, 50).Value = DBNull.Value;
            command.Parameters.Add("@CellRole", SqlDbType.TinyInt).Value = (byte)0;
            command.Parameters.Add("@Hidden", SqlDbType.Bit).Value = row.Hidden;
            command.Parameters.Add("@UpdatedBy", SqlDbType.NVarChar, 50).Value = (object?)userId ?? DBNull.Value;
            await command.ExecuteNonQueryAsync(token);
        }
    }

    private static async Task DeleteRowsAsync(
        SqlConnection connection, SqlTransaction? transaction, int moduleId, CancellationToken token)
    {
        foreach (var table in new[] { "dbo.MODULE_FORM_LAYOUT", "dbo.MODULE_FORM_TAB" })
        {
            await using var command = new SqlCommand($"DELETE FROM {table} WHERE M_IDX=@ModuleId;", connection, transaction);
            command.Parameters.Add("@ModuleId", SqlDbType.Int).Value = moduleId;
            await command.ExecuteNonQueryAsync(token);
        }
    }

    /// <summary>保存前快照：行 + 页签 + 脏标记，供发布失败时整笔恢复。</summary>
    private async Task<LayoutBackup> ReadBackupAsync(SqlConnection connection, int moduleId, CancellationToken token)
    {
        var tabs = new List<FormTabDefinition>();
        var rows = new List<(string Table, FormLayoutRow Row)>();
        const string tabSql = """
            SELECT TAB_NO, LTRIM(RTRIM(ISNULL(TAB_TITLE,''))) FROM dbo.MODULE_FORM_TAB WHERE M_IDX=@ModuleId;
            """;
        await using (var command = new SqlCommand(tabSql, connection))
        {
            command.Parameters.Add("@ModuleId", SqlDbType.Int).Value = moduleId;
            await using var reader = await command.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token))
            {
                tabs.Add(new FormTabDefinition(reader.GetInt32(0), reader.GetString(1)));
            }
        }
        const string rowSql = """
            SELECT LTRIM(RTRIM(T_ID)), LTRIM(RTRIM(F_ID)), TAB_NO, ORDER_NO, SPAN, ROW_SPAN, NEW_LINE,
                   SECTION_ID, CELL_GROUP, CELL_ROLE, IS_HIDDEN
            FROM dbo.MODULE_FORM_LAYOUT WHERE M_IDX=@ModuleId;
            """;
        await using (var command = new SqlCommand(rowSql, connection))
        {
            command.Parameters.Add("@ModuleId", SqlDbType.Int).Value = moduleId;
            await using var reader = await command.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token))
            {
                rows.Add((reader.GetString(0), new FormLayoutRow(
                    reader.GetString(1), reader.GetInt32(2), reader.GetInt32(3),
                    reader.GetByte(4), reader.GetByte(5), reader.GetBoolean(6),
                    reader.IsDBNull(7) ? null : reader.GetString(7).Trim(),
                    reader.IsDBNull(8) ? null : reader.GetString(8).Trim(),
                    reader.GetByte(9), reader.GetBoolean(10))));
            }
        }

        bool? dirty = null;
        const string dirtySql = "SELECT DIRTY_TAG FROM dbo.WORKBENCH_MODULE_DIRTY WHERE MODULE_ID=@ModuleId;";
        await using (var command = new SqlCommand(dirtySql, connection))
        {
            command.Parameters.Add("@ModuleId", SqlDbType.Int).Value = moduleId;
            var value = await command.ExecuteScalarAsync(token);
            if (value is not null and not DBNull)
            {
                dirty = Convert.ToBoolean(value);
            }
        }
        return new LayoutBackup(tabs, rows, dirty);
    }

    private async Task RestoreBackupAsync(int moduleId, LayoutBackup backup, string userId, CancellationToken token)
    {
        await using var connection = connections.Create();
        await connection.OpenAsync(token);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(token);
        try
        {
            await DeleteRowsAsync(connection, transaction, moduleId, token);
            const string tabSql = """
                INSERT INTO dbo.MODULE_FORM_TAB (M_IDX, TAB_NO, TAB_TITLE) VALUES (@ModuleId, @TabNo, @Title);
                """;
            foreach (var tab in backup.Tabs)
            {
                await using var command = new SqlCommand(tabSql, connection, transaction);
                command.Parameters.Add("@ModuleId", SqlDbType.Int).Value = moduleId;
                command.Parameters.Add("@TabNo", SqlDbType.Int).Value = tab.No;
                command.Parameters.Add("@Title", SqlDbType.NVarChar, 50).Value = tab.Title;
                await command.ExecuteNonQueryAsync(token);
            }
            const string rowSql = """
                INSERT INTO dbo.MODULE_FORM_LAYOUT
                    (M_IDX, T_ID, F_ID, TAB_NO, ORDER_NO, SPAN, ROW_SPAN, NEW_LINE, SECTION_ID, CELL_GROUP,
                     CELL_ROLE, IS_HIDDEN, UPDATED_BY, UPDATED_AT)
                VALUES (@ModuleId, @Table, @Field, @TabNo, @OrderNo, @Span, @RowSpan, @NewLine, @SectionId,
                        @CellGroup, @CellRole, @Hidden, @UpdatedBy, SYSUTCDATETIME());
                """;
            foreach (var (table, row) in backup.Rows)
            {
                await using var command = new SqlCommand(rowSql, connection, transaction);
                command.Parameters.Add("@ModuleId", SqlDbType.Int).Value = moduleId;
                command.Parameters.Add("@Table", SqlDbType.NVarChar, 100).Value = table;
                command.Parameters.Add("@Field", SqlDbType.NVarChar, 100).Value = row.Key;
                command.Parameters.Add("@TabNo", SqlDbType.Int).Value = row.TabNo;
                command.Parameters.Add("@OrderNo", SqlDbType.Int).Value = row.OrderNo;
                command.Parameters.Add("@Span", SqlDbType.TinyInt).Value = (byte)row.Span;
                command.Parameters.Add("@RowSpan", SqlDbType.TinyInt).Value = (byte)row.RowSpan;
                command.Parameters.Add("@NewLine", SqlDbType.Bit).Value = row.NewLine;
                command.Parameters.Add("@SectionId", SqlDbType.NVarChar, 50).Value = (object?)row.SectionId ?? DBNull.Value;
                command.Parameters.Add("@CellGroup", SqlDbType.NVarChar, 50).Value = (object?)row.CellGroup ?? DBNull.Value;
                command.Parameters.Add("@CellRole", SqlDbType.TinyInt).Value = (byte)row.CellRole;
                command.Parameters.Add("@Hidden", SqlDbType.Bit).Value = row.Hidden;
                command.Parameters.Add("@UpdatedBy", SqlDbType.NVarChar, 50).Value = (object?)userId ?? DBNull.Value;
                await command.ExecuteNonQueryAsync(token);
            }

            // 脏标记也回到保存前：改动已撤销，不该留下"待发布"的假积压
            if (backup.Dirty is null)
            {
                const string clearSql = "DELETE FROM dbo.WORKBENCH_MODULE_DIRTY WHERE MODULE_ID=@ModuleId;";
                await using var clear = new SqlCommand(clearSql, connection, transaction);
                clear.Parameters.Add("@ModuleId", SqlDbType.Int).Value = moduleId;
                await clear.ExecuteNonQueryAsync(token);
            }
            else if (backup.Dirty == true)
            {
                await dirtyMarker.MarkDirtyAsync(connection, transaction, moduleId, userId, token);
            }
            await transaction.CommitAsync(token);
        }
        catch
        {
            await transaction.RollbackAsync(token);
            throw;
        }
    }

    private async Task CompleteIdempotencyAsync(string key, string? resultKey, CancellationToken token)
    {
        await using var connection = connections.Create();
        await connection.OpenAsync(token);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(token);
        try
        {
            await idempotency.CompleteAsync(connection, transaction, key, resultKey, flowStarted: false, token);
            await transaction.CommitAsync(token);
        }
        catch
        {
            await transaction.RollbackAsync(token);
            throw;
        }
    }

    private async Task ReleaseIdempotencyAsync(string key, CancellationToken token)
    {
        await using var connection = connections.Create();
        await connection.OpenAsync(token);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(token);
        try
        {
            await idempotency.ReleaseAsync(connection, transaction, key, token);
            await transaction.CommitAsync(token);
        }
        catch
        {
            await transaction.RollbackAsync(token);
            throw;
        }
    }

    /// <summary>版式读态的乐观锁基准：两表最近一次写入时间（UTC，格式由库侧统一产出，避免时区漂移）。</summary>
    private static async Task<string?> ReadStampAsync(
        SqlConnection connection, SqlTransaction? transaction, int moduleId, CancellationToken token)
    {
        const string sql = """
            SELECT CONVERT(nvarchar(23), MAX(STAMP), 121)
            FROM (SELECT UPDATED_AT AS STAMP FROM dbo.MODULE_FORM_LAYOUT WHERE M_IDX=@ModuleId
                  UNION ALL
                  SELECT NULL AS STAMP FROM dbo.MODULE_FORM_TAB WHERE M_IDX=@ModuleId) t;
            """;
        await using var command = new SqlCommand(sql, connection, transaction);
        command.Parameters.Add("@ModuleId", SqlDbType.Int).Value = moduleId;
        var value = await command.ExecuteScalarAsync(token);
        return value is null or DBNull ? null : Convert.ToString(value);
    }

    private static async Task<ModuleInfo?> ReadModuleAsync(
        SqlConnection connection, int moduleId, CancellationToken token)
    {
        const string sql = """
            SELECT LTRIM(RTRIM(ISNULL(M_DESC,''))), LTRIM(RTRIM(ISNULL(MASTER_TABLE,''))),
                   LTRIM(RTRIM(ISNULL(DETAIL_TABLE,''))), CAST(ISNULL(FORM_COLUMNS,0) AS int),
                   ISNULL(FORM_TABS, N'')
            FROM dbo.MODULES WITH (NOLOCK) WHERE M_IDX=@ModuleId;
            """;
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@ModuleId", SqlDbType.Int).Value = moduleId;
        await using var reader = await command.ExecuteReaderAsync(token);
        if (!await reader.ReadAsync(token))
        {
            return null;
        }
        var masterTable = reader.GetString(1);
        if (masterTable.Length == 0)
        {
            return null;
        }
        var detailTable = reader.GetString(2);
        return new ModuleInfo(moduleId, reader.GetString(0), masterTable,
            detailTable.Length == 0 ? null : detailTable, reader.GetInt32(3), reader.GetString(4));
    }

    /// <summary>
    /// 设计态看到的页签必须与运行态一致：版式表有页签则用它，否则回落到既有的
    /// <c>MODULES.FORM_TABS</c>，再兜底为常驻的 1 号页签（该列退役后只剩后两者）。
    /// </summary>
    private static IReadOnlyList<FormTabDefinition> EffectiveTabs(ModuleInfo module, FormLayoutDefinition layout)
    {
        var tabs = layout.Tabs.Count > 0
            ? layout.Tabs.ToList()
            : WorkbenchDefinitionBuilder.ParseFormTabs(module.FormTabs).ToList();
        if (tabs.All(tab => tab.No != 1))
        {
            tabs.Insert(0, new FormTabDefinition(1, string.Empty));
        }
        return tabs.OrderBy(tab => tab.No).ToList();
    }

    private static FormLayoutTableDesign BuildTableDesign(
        string table,
        IReadOnlyList<FormLayoutRow> rows,
        IReadOnlyList<FormFieldRow> fields,
        IReadOnlyDictionary<string, FormLayoutFieldFact> facts,
        ModuleRights rights,
        bool isDetail,
        bool customized = false)
    {
        var byKey = fields.ToDictionary(field => field.Key, StringComparer.OrdinalIgnoreCase);
        var layout = new List<FormLayoutDesignRow>();
        var placed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows.OrderBy(item => item.TabNo).ThenBy(item => item.OrderNo))
        {
            placed.Add(row.Key);
            byKey.TryGetValue(row.Key, out var field);
            var fact = facts.TryGetValue(row.Key, out var known) ? known : null;
            var (locked, reason) = LockState(fact);
            layout.Add(new FormLayoutDesignRow(
                row.Key,
                field?.Label ?? row.Key,
                field?.DataType ?? "nvarchar",
                row.TabNo,
                row.OrderNo,
                row.Span,
                row.RowSpan,
                row.NewLine,
                row.SectionId,
                row.CellGroup,
                row.CellRole,
                row.Hidden,
                locked,
                reason,
                field is not null && IsUserVisible(field, rights, isDetail),
                field?.IsRequired ?? false,
                fact?.IsPrimaryKey ?? false,
                fact?.HasActiveChooser ?? false,
                field?.IsVirtual ?? false));
        }

        var pool = new List<FormLayoutPoolField>();
        foreach (var field in fields.Where(field => !placed.Contains(field.Key)))
        {
            var fact = facts.TryGetValue(field.Key, out var known) ? known : null;
            var (locked, reason) = LockState(fact);
            pool.Add(new FormLayoutPoolField(
                field.Key, field.Label, field.DataType,
                IsUserVisible(field, rights, isDetail),
                field.IsRequired,
                fact?.IsPrimaryKey ?? false,
                fact?.HasActiveChooser ?? false,
                field.IsVirtual,
                locked,
                reason));
        }

        return new FormLayoutTableDesign(table, layout, pool, customized);
    }

    /// <summary>不可移除：主键、单据系统列、用户可填的必填列（服务端自填字段仍可隐藏）。</summary>
    private static (bool Locked, string? Reason) LockState(FormLayoutFieldFact? fact)
    {
        if (fact is null)
        {
            return (false, null);
        }
        if (fact.IsPrimaryKey)
        {
            return (true, "主键列，始终显示");
        }
        if (fact.IsLifecycleSystemColumn)
        {
            return (true, "单据系统列，始终显示");
        }
        return fact.UserFillableRequired ? (true, "必填字段，始终显示") : (false, null);
    }

    private static bool IsUserVisible(FormFieldRow field, ModuleRights rights, bool isDetail)
    {
        if (field.IsCost && !rights.CanViewCost)
        {
            return false;
        }
        if (field.IsSecrecy && !rights.CanViewSecrecy)
        {
            return false;
        }
        var denied = isDetail ? rights.DeniedDetailFields : rights.DeniedMasterFields;
        return !denied.Contains(field.Key);
    }

    private sealed record ModuleInfo(
        int ModuleId, string Title, string MasterTable, string? DetailTable, int Columns, string FormTabs);

    private sealed record LayoutBackup(
        IReadOnlyList<FormTabDefinition> Tabs,
        IReadOnlyList<(string Table, FormLayoutRow Row)> Rows,
        bool? Dirty);
}
