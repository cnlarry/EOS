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

        var layout = await FormLayoutReader.ReadAsync(
            connection, moduleId, module.MasterTable, module.DetailTable, token);
        // 主表字段池含虚拟列：它们是选择器回写的伴生显示列，能排进版式（运行态按只读字段渲染）
        var masterFields = await WorkbenchDefinitionBuilder.ReadFormFieldRows(
            connection, module.MasterTable, module.MasterTable, token, includeVirtual: true);
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
        // 呈现配置与运行态同口径下发：非法/未配置回落本页签；宽高只在弹窗方式下有意义，
        // 非弹窗方式下不给画板尺寸（画板照旧铺满可用区域）
        var openMode = FormOpenModes.Normalize(module.OpenMode);
        var isDialog = FormOpenModes.IsDialog(openMode);
        return new FormLayoutDesignState(
            moduleId,
            module.Title,
            module.MasterTable,
            string.IsNullOrWhiteSpace(module.DetailTable) ? null : module.DetailTable,
            EffectiveTabs(layout),
            BuildTableDesign(module.MasterTable, layout.Master, masterFields, masterFacts, rights,
                isDetail: false, customized: layout.MasterCustomized),
            BuildTableDesign(
                module.DetailTable ?? string.Empty,
                layout.Detail.Select(row => new FormLayoutRow(
                    row.Key, 1, row.OrderNo, 1, 1, false, null, null, 0, row.Hidden)).ToList(),
                detailFields, detailFacts, rights, isDetail: true, customized: layout.DetailCustomized),
            stamp,
            openMode,
            isDialog ? module.DialogWidth : null,
            isDialog ? module.DialogHeight : null);
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
                reader.GetInt32(0), reader.GetString(1), module.MasterTable, reader.GetInt32(2) == 1));
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

        // 呈现配置（打开方式 / 弹窗宽高）与版式同一笔保存、同一次重发布 ⇒ 保存即生效；
        // 全空表示"只存版式"（历史调用方），不动 MODULES 的呈现列
        var presentation = FormLayoutSubmission.ParsePresentation(request);
        // 列跨度按**该行所属页签的列数**夹取（页签级事实，未给则落兜底 4 列）：
        // 设计器与运行态必须同源，否则设计态排 3 段、运行态按 2 列渲染会把格子挤到下一行
        var layout = FormLayoutSubmission.Normalize(request);
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
            module, layout, presentation, request.IdempotencyKey, request.BaseUpdatedAt, userId, executor,
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
            module, null, FormPresentation.None, idempotencyKey, baseUpdatedAt, userId, executor,
            ResetAction, isReset: true, token);
    }

    private async Task<FormLayoutSaveOutcome> ApplyAsync(
        ModuleInfo module,
        FormLayoutDefinition? layout,
        FormPresentation presentation,
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
        var backup = await ReadBackupAsync(connection, module, token);

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
                if (presentation.IsGiven)
                {
                    await UpdatePresentationAsync(connection, transaction, moduleId, presentation, token);
                }
                await dirtyMarker.MarkDirtyAsync(connection, transaction, moduleId, executor, token);
                await auditWriter.WriteEventAsync(
                    connection, transaction, moduleId, $"module-{moduleId}", action,
                    isReset
                        ? $"重置模块 {moduleId} 的表单版式为默认版式。"
                        : $"保存模块 {moduleId} 的表单版式（页签 {layout!.Tabs.Count} 个〔{DescribeTabColumns(layout.Tabs)}〕、"
                          + $"主表 {layout.Master.Count} 行、明细 {layout.Detail.Count} 行）。"
                          + DescribePresentation(module, presentation),
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
            INSERT INTO dbo.MODULE_FORM_TAB (M_IDX, TAB_NO, TAB_TITLE, LAYOUT_COLUMNS)
            VALUES (@ModuleId, @TabNo, @Title, @Columns);
            """;
        foreach (var tab in layout.Tabs)
        {
            await using var command = new SqlCommand(tabSql, connection, transaction);
            command.Parameters.Add("@ModuleId", SqlDbType.Int).Value = module.ModuleId;
            command.Parameters.Add("@TabNo", SqlDbType.Int).Value = tab.No;
            command.Parameters.Add("@Title", SqlDbType.NVarChar, 50).Value = tab.Title.Length == 0 ? string.Empty : tab.Title;
            // 页签级布局列数：null = 沿用模块默认列数（读取侧 ResolveTabColumns 兜底）
            command.Parameters.Add("@Columns", SqlDbType.TinyInt).Value = (object?)tab.Columns ?? DBNull.Value;
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

    /// <summary>
    /// 保存前快照：行 + 页签 + 脏标记 + 模块呈现列，供发布失败时整笔恢复。
    /// 呈现列直接取已读入的模块行（同事务外读的同一行，值即保存前值）。
    /// </summary>
    private async Task<LayoutBackup> ReadBackupAsync(SqlConnection connection, ModuleInfo module, CancellationToken token)
    {
        var moduleId = module.ModuleId;
        var tabs = new List<FormTabDefinition>();
        var rows = new List<(string Table, FormLayoutRow Row)>();
        const string tabSql = """
            SELECT TAB_NO, LTRIM(RTRIM(ISNULL(TAB_TITLE,''))), LAYOUT_COLUMNS
            FROM dbo.MODULE_FORM_TAB WHERE M_IDX=@ModuleId;
            """;
        await using (var command = new SqlCommand(tabSql, connection))
        {
            command.Parameters.Add("@ModuleId", SqlDbType.Int).Value = moduleId;
            await using var reader = await command.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token))
            {
                tabs.Add(new FormTabDefinition(
                    reader.GetInt32(0), reader.GetString(1),
                    reader.IsDBNull(2) ? null : reader.GetByte(2)));
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
        const string dirtySql = "SELECT DIRTY_TAG FROM dbo.WORKBENCH_MODULE_DIRTY WHERE M_IDX=@ModuleId;";
        await using (var command = new SqlCommand(dirtySql, connection))
        {
            command.Parameters.Add("@ModuleId", SqlDbType.Int).Value = moduleId;
            var value = await command.ExecuteScalarAsync(token);
            if (value is not null and not DBNull)
            {
                dirty = Convert.ToBoolean(value);
            }
        }
        return new LayoutBackup(tabs, rows, dirty, module);
    }

    /// <summary>
    /// 写模块行的呈现列（打开方式 / 弹窗宽高）。同一事务内、与版式行一起提交，
    /// 所以重发布读到的是新值——这就是"设计器里保存即生效"的落点；值 null 即清空该列。
    ///
    /// **不写 `FORM_LAYOUT_COLUMNS`**：栅格列数自 2026-10-06 起是页签级事实
    /// （`MODULE_FORM_TAB.LAYOUT_COLUMNS`，随页签一起写），模块那一列退化为"未声明页签的兜底列数"。
    /// </summary>
    private static async Task UpdatePresentationAsync(
        SqlConnection connection, SqlTransaction transaction, int moduleId,
        FormPresentation presentation, CancellationToken token)
    {
        const string sql = """
            UPDATE dbo.MODULES SET
                FORM_OPEN_MODE=@OpenMode, FORM_DIALOG_WIDTH=@DialogWidth,
                FORM_DIALOG_HEIGHT=@DialogHeight
            WHERE M_IDX=@ModuleId;
            """;
        await using var command = new SqlCommand(sql, connection, transaction);
        command.Parameters.Add("@ModuleId", SqlDbType.Int).Value = moduleId;
        command.Parameters.Add("@OpenMode", SqlDbType.NVarChar, 20).Value = (object?)presentation.OpenMode ?? DBNull.Value;
        command.Parameters.Add("@DialogWidth", SqlDbType.Int).Value = (object?)presentation.DialogWidth ?? DBNull.Value;
        command.Parameters.Add("@DialogHeight", SqlDbType.Int).Value = (object?)presentation.DialogHeight ?? DBNull.Value;
        await command.ExecuteNonQueryAsync(token);
    }

    /// <summary>审计文案里的呈现变更（只列真正变了的项，未变的不刷屏）。</summary>
    private static string DescribePresentation(ModuleInfo module, FormPresentation presentation)
    {
        if (!presentation.IsGiven)
        {
            return string.Empty;
        }
        var changes = new List<string>();
        if (!string.Equals(FormOpenModes.Normalize(module.OpenMode), FormOpenModes.Normalize(presentation.OpenMode), StringComparison.Ordinal))
        {
            changes.Add($"打开方式 {FormOpenModes.Normalize(module.OpenMode)} → {FormOpenModes.Normalize(presentation.OpenMode)}");
        }
        if (module.DialogWidth != presentation.DialogWidth || module.DialogHeight != presentation.DialogHeight)
        {
            changes.Add($"弹窗尺寸 {DescribeSize(module.DialogWidth, module.DialogHeight)} → {DescribeSize(presentation.DialogWidth, presentation.DialogHeight)}");
        }
        return changes.Count == 0 ? string.Empty : "呈现配置：" + string.Join("；", changes) + "。";
    }

    /// <summary>审计文案里的页签列数摘要：列数是页签级事实，审计里要看得出"哪个页签几列"。</summary>
    private static string DescribeTabColumns(IReadOnlyList<FormTabDefinition> tabs)
        => string.Join("、", tabs.OrderBy(tab => tab.No).Select(tab =>
            $"{tab.No}{(tab.Title.Length == 0 ? "默认" : tab.Title)}={tab.Columns?.ToString() ?? "默认"}"));

    private static string DescribeSize(int? width, int? height)
        => width is null && height is null ? "默认" : $"{width?.ToString() ?? "默认"}×{height?.ToString() ?? "默认"}";

    private async Task RestoreBackupAsync(int moduleId, LayoutBackup backup, string userId, CancellationToken token)
    {
        await using var connection = connections.Create();
        await connection.OpenAsync(token);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(token);
        try
        {
            await DeleteRowsAsync(connection, transaction, moduleId, token);
            // 回滚必须把页签级列数一起写回：漏了它，保存中途失败（复原备份）会把"哪个页签几列"抹成
            // NULL（= 回落模块默认列数）——一次失败的保存改变了页面观感，且没有任何提示
            const string tabSql = """
                INSERT INTO dbo.MODULE_FORM_TAB (M_IDX, TAB_NO, TAB_TITLE, LAYOUT_COLUMNS)
                VALUES (@ModuleId, @TabNo, @Title, @Columns);
                """;
            foreach (var tab in backup.Tabs)
            {
                await using var command = new SqlCommand(tabSql, connection, transaction);
                command.Parameters.Add("@ModuleId", SqlDbType.Int).Value = moduleId;
                command.Parameters.Add("@TabNo", SqlDbType.Int).Value = tab.No;
                command.Parameters.Add("@Title", SqlDbType.NVarChar, 50).Value = tab.Title;
                command.Parameters.Add("@Columns", SqlDbType.TinyInt).Value = (object?)tab.Columns ?? DBNull.Value;
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
                const string clearSql = "DELETE FROM dbo.WORKBENCH_MODULE_DIRTY WHERE M_IDX=@ModuleId;";
                await using var clear = new SqlCommand(clearSql, connection, transaction);
                clear.Parameters.Add("@ModuleId", SqlDbType.Int).Value = moduleId;
                await clear.ExecuteNonQueryAsync(token);
            }
            else if (backup.Dirty == true)
            {
                await dirtyMarker.MarkDirtyAsync(connection, transaction, moduleId, userId, token);
            }

            // 呈现列也回到保存前：这次改动整体没生效（重发布被拦），模块行不该留下"半截已改"的呈现配置。
            // 页签级列数随页签行一起回到旧值（见上方 page-tab INSERT：LAYOUT_COLUMNS 一并写回）
            if (backup.Presentation is { } original)
            {
                await UpdatePresentationAsync(connection, transaction, moduleId,
                    new FormPresentation(original.OpenMode, original.DialogWidth, original.DialogHeight, IsGiven: true),
                    token);
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
        // 呈现配置取模块声明（FORM_OPEN_MODE / FORM_DIALOG_WIDTH / FORM_DIALOG_HEIGHT）；
        // 页签与一行几列都只来自 MODULE_FORM_TAB（迁移 322 起模块级列数已删）
        const string sql = """
            SELECT LTRIM(RTRIM(ISNULL(M_DESC,''))), LTRIM(RTRIM(ISNULL(MASTER_TABLE,''))),
                   LTRIM(RTRIM(ISNULL(DETAIL_TABLE,''))),
                   FORM_OPEN_MODE, FORM_DIALOG_WIDTH, FORM_DIALOG_HEIGHT
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
        var openMode = reader.IsDBNull(3) ? null : reader.GetString(3);
        var dialogWidth = reader.IsDBNull(4) ? (int?)null : reader.GetInt32(4);
        var dialogHeight = reader.IsDBNull(5) ? (int?)null : reader.GetInt32(5);
        return new ModuleInfo(moduleId, reader.GetString(0), masterTable,
            detailTable.Length == 0 ? null : detailTable, openMode, dialogWidth, dialogHeight);
    }

    /// <summary>
    /// 设计态看到的页签必须与运行态一致：页签只来自版式表（<c>MODULE_FORM_TAB</c>），
    /// 没有页签行时兜底为常驻的 1 号页签（列数按兜底 4 列，与库内 DEFAULT 同值）。
    /// </summary>
    private static IReadOnlyList<FormTabDefinition> EffectiveTabs(FormLayoutDefinition layout)
    {
        var tabs = layout.Tabs.ToList();
        if (tabs.All(tab => tab.No != 1))
        {
            tabs.Insert(0, new FormTabDefinition(1, string.Empty, FormLayoutDerivation.DefaultColumns));
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
            // 设计态的明细表头只画显示中的列（隐藏列不在画布上），已隐藏的明细列因此在语义上
            // 与"没排进表单"等价：必须留在字段池里，否则隐藏之后没有任何入口能把它选回来。
            if (!(isDetail && row.Hidden))
            {
                placed.Add(row.Key);
            }
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
        // 池 = 已登记但未排进表单的字段；管理员标记为不显示的字段（IS_VISIBLE=0）不进池——
        // 运行态一律不渲染它们（FormFieldSelector 剔除），排进版式也不会有任何效果
        foreach (var field in fields.Where(field => !placed.Contains(field.Key) && field.IsVisible))
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
        int ModuleId, string Title, string MasterTable, string? DetailTable,
        string? OpenMode = null, int? DialogWidth = null, int? DialogHeight = null);

    private sealed record LayoutBackup(
        IReadOnlyList<FormTabDefinition> Tabs,
        IReadOnlyList<(string Table, FormLayoutRow Row)> Rows,
        bool? Dirty,
        /// <summary>模块行的呈现列快照（打开方式 / 弹窗宽高 / 栅格列数）：发布失败时一并回滚。</summary>
        ModuleInfo? Presentation = null);
}
