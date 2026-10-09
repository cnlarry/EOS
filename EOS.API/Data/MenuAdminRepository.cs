using System.Data;
using EOS.API.Data.Forms;
using EOS.API.Models;
using Microsoft.Data.SqlClient;

using EOS.API.Telemetry;
namespace EOS.API.Data;

/// <summary>
/// 菜单管理（模块 2301）：MODULES 增删改查。
/// 语义 / P_SYS_DelTreeNode / P_Change_M_IDX：
/// - 保存：事务内更新/插入，编号变更时级联子级 M_P_IDX、权限与引用表；
/// - M_ROOT_IDX 按父链根重算并同步到子树；
/// - 删除：有下级菜单或主/副表已产生数据时拒绝（新保护），叶子且无数据时删除并清理权限引用。
/// - 排序：同级（M_P_IDX 相同）节点按 SORT_IDX,M_IDX 排序后重写 SORT_IDX（10 步进），
/// 系统「排序号」字段驱动的菜单顺序。
/// 全部参数化，动态标识符仅来自服务端校验。
/// </summary>
public sealed class MenuAdminRepository(
    DbConnectionFactory connections,
    ModuleBusinessConfigRepository businessConfigRepository,
    WorkbenchDirtyMarker dirtyMarker,
    WorkbenchAuditWriter auditWriter,
    ILogger<MenuAdminRepository> logger)
{

    /// <summary>排序后目标下标（0 起）。非法动作抛 ArgumentException；边界移动为无操作。</summary>
    public static int TargetIndex(int currentIndex, int count, string action) => action switch
    {
        "top" => 0,
        "up" => currentIndex > 0 ? currentIndex - 1 : currentIndex,
        "down" => currentIndex < count - 1 ? currentIndex + 1 : currentIndex,
        "bottom" => count - 1,
        _ => throw new ArgumentException("排序动作仅支持 top / up / down / bottom。"),
    };

    public async Task<MenuAdminList> GetModulesAsync(string? keyword, CancellationToken token)
    {
        using var timing = DbTimingCollector.Instance.Measure();
        var sql = """
            SELECT MODULES.M_IDX,M_ALIAS,M_DESC,M_URL,DETAIL_NO_FIELDS,DETAIL_NO_SAVE,
                   SEARCH_1,SEARCH_2,M_P_IDX,SORT_IDX,M_TAG,AUTO_APPROVE,IF_COPY,ERROR_NO_SAVE,SORT_FIELDS,
                   MASTER_TABLE,FILTER,DETAIL_TABLE,NOT_BACK_FIELDS_M,NOT_BACK_FIELDS,LAST_UPDATE_BY,LAST_UPDATE_DATE,
                   NULL AS FORM_TABS,NULL AS FORM_COLUMNS,M_ICON,EFFECT_ENGINE_TAG,
                   (SELECT TOP 1 LTRIM(RTRIM(t.T_DESC)) FROM dbo.TABLES t WITH (NOLOCK)
                     WHERE LTRIM(RTRIM(t.T_ID))=LTRIM(RTRIM(MODULES.MASTER_TABLE))) AS MASTER_TABLE_DESC,
                   (SELECT TOP 1 LTRIM(RTRIM(t.T_DESC)) FROM dbo.TABLES t WITH (NOLOCK)
                     WHERE LTRIM(RTRIM(t.T_ID))=LTRIM(RTRIM(MODULES.DETAIL_TABLE))) AS DETAIL_TABLE_DESC,
                   ISNULL(d.DIRTY_TAG,0) AS DIRTY_TAG,s.VERSION AS PUBLISH_VERSION,s.PUBLISHED_AT,
                   REMARK
                   FROM dbo.MODULES WITH (NOLOCK)
                   LEFT JOIN dbo.WORKBENCH_MODULE_DIRTY d WITH (NOLOCK) ON d.M_IDX=MODULES.M_IDX
                   LEFT JOIN dbo.WORKBENCH_DEFINITION_SNAPSHOT s WITH (NOLOCK) ON s.M_IDX=MODULES.M_IDX AND s.IS_CURRENT=1
                   WHERE (@Keyword = '' OR M_DESC LIKE @Keyword OR M_ALIAS LIKE @Keyword OR CONVERT(nvarchar(20),MODULES.M_IDX) LIKE @Keyword)
            ORDER BY ISNULL(M_P_IDX,0),SORT_IDX,MODULES.M_IDX;
            """;
        await using var connection = connections.Create();
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@Keyword", SqlDbType.NVarChar, 200).Value = string.IsNullOrWhiteSpace(keyword) ? string.Empty : "%" + keyword.Trim() + "%";
        await connection.OpenAsync(token);
        await using var reader = await command.ExecuteReaderAsync(token);
        var modules = new List<MenuAdminModule>();
        while (await reader.ReadAsync(token))
            modules.Add(ReadModule(reader));
        return new MenuAdminList(modules.Count, modules);
    }

    /// <summary>菜单管理表选择器候选（TABLES 元数据，供操作主表/副表选择）。</summary>
    public async Task<IReadOnlyList<MenuAdminTableInfo>> GetTablesAsync(CancellationToken token)
    {
        const string sql = """
            SELECT LTRIM(RTRIM(T_ID)),LTRIM(RTRIM(T_DESC)),LTRIM(RTRIM(ISNULL(T_KIND,''))),LTRIM(RTRIM(ISNULL(T_TYPE,'')))
            FROM dbo.TABLES WITH (NOLOCK)
            ORDER BY T_DESC,T_ID;
            """;
        await using var connection = connections.Create();
        await using var command = new SqlCommand(sql, connection);
        await connection.OpenAsync(token);
        await using var reader = await command.ExecuteReaderAsync(token);
        var result = new List<MenuAdminTableInfo>();
        while (await reader.ReadAsync(token))
        {
            var table = reader.GetString(0);
            if (!WorkbenchSql.Identifier.IsMatch(table)) continue;
            result.Add(new MenuAdminTableInfo(table, reader.GetString(1), NullIfEmpty(reader.GetString(2)), NullIfEmpty(reader.GetString(3))));
        }
        return result;
    }

    /// <summary>菜单管理字段选择器候选：返回表在 FIELDS 中且物理存在的字段。</summary>
    public async Task<IReadOnlyList<MenuAdminFieldInfo>> GetTableFieldsAsync(string table, CancellationToken token)
    {
        if (!WorkbenchSql.Identifier.IsMatch(table))
            throw new ArgumentException($"表名无效：{table}");
        const string sql = """
            SELECT LTRIM(RTRIM(f.F_ID)),
                   COALESCE(NULLIF(NULLIF(NULLIF(LTRIM(RTRIM(f.F_DESC)),''),'NULL'),'&nbsp;'),LTRIM(RTRIM(f.F_ID))),
                   COALESCE(LTRIM(RTRIM(f.F_TYPE)),'nvarchar'),
                   CAST(COALESCE(f.IS_VISIBLE,1) AS bit),
                   CAST(COALESCE(f.IS_VIRTUAL,0) AS bit),
                   CAST(COALESCE(f.IS_QUERY,1) AS bit)
            FROM dbo.FIELDS f WITH (NOLOCK)
            WHERE LTRIM(RTRIM(f.T_ID))=@Table
              AND EXISTS (SELECT 1 FROM sys.columns c
                          JOIN sys.objects o ON c.object_id=o.object_id AND o.type IN ('U','V')
                          JOIN sys.schemas s ON o.schema_id=s.schema_id
                          WHERE s.name=N'dbo' AND o.name=@Table AND c.name=f.F_ID)
            ORDER BY f.F_ID;
            """;
        await using var connection = connections.Create();
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@Table", SqlDbType.NVarChar, 100).Value = table.Trim();
        await connection.OpenAsync(token);
        await using var reader = await command.ExecuteReaderAsync(token);
        var result = new List<MenuAdminFieldInfo>();
        while (await reader.ReadAsync(token))
        {
            var field = reader.GetString(0);
            if (!WorkbenchSql.Identifier.IsMatch(field)) continue;
            result.Add(new MenuAdminFieldInfo(field, reader.GetString(1), reader.GetString(2), reader.GetBoolean(3), reader.GetBoolean(4), reader.GetBoolean(5)));
        }
        return result;
    }

    /// <summary>
    /// 菜单默认查询列：
    /// 返回主表/副表全部可见物理字段及当前 SYSQL_DEFAULT 勾选顺序。
    /// </summary>
    public async Task<MenuDefaultColumns> GetDefaultColumnsAsync(int moduleId, string tableKind, CancellationToken token)
    {
        var (masterTable, detailTable) = await ResolveModuleTablesAsync(moduleId, token);
        var targetTable = ResolveTargetTable(masterTable, detailTable, tableKind)
            ?? throw new ArgumentException("该模块未配置" + (tableKind == "master" ? "操作主表" : "操作副表") + "。");
        const string sql = """
            SELECT LTRIM(RTRIM(f.F_ID)),COALESCE(NULLIF(NULLIF(NULLIF(LTRIM(RTRIM(f.F_DESC)),''),'NULL'),'&nbsp;'),LTRIM(RTRIM(f.F_ID))),
                   CAST(CASE WHEN d.F_ID IS NULL THEN 0 ELSE 1 END AS bit),
                   COALESCE(d.F_IDX,COALESCE(f.VERIFY_INDEX,999))
            FROM dbo.FIELDS f WITH (NOLOCK)
            LEFT JOIN dbo.SYSQL_DEFAULT d WITH (NOLOCK)
              ON d.T_ID=@MasterTable AND d.T_ID_R=@TargetTable AND LTRIM(RTRIM(d.F_ID))=LTRIM(RTRIM(f.F_ID))
            WHERE f.T_ID=@TargetTable AND COALESCE(f.IS_VISIBLE,1)=1 AND COALESCE(f.IS_VIRTUAL,0)=0
              AND EXISTS (SELECT 1 FROM sys.columns c
                          JOIN sys.objects o ON c.object_id=o.object_id AND o.type IN ('U','V')
                          JOIN sys.schemas s ON o.schema_id=s.schema_id
                          WHERE s.name=N'dbo' AND o.name=@TargetTable AND c.name=f.F_ID)
            ORDER BY CASE WHEN d.F_ID IS NULL THEN 1 ELSE 0 END,COALESCE(d.F_IDX,COALESCE(f.VERIFY_INDEX,999)),f.F_ID;
            """;
        await using var connection = connections.Create();
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@MasterTable", SqlDbType.NVarChar, 100).Value = masterTable
            ?? throw new ArgumentException("该模块未配置操作主表。");
        command.Parameters.Add("@TargetTable", SqlDbType.NVarChar, 100).Value = targetTable;
        await connection.OpenAsync(token);
        await using var reader = await command.ExecuteReaderAsync(token);
        var fields = new List<MenuDefaultColumn>();
        var order = 0;
        while (await reader.ReadAsync(token))
        {
            var key = reader.GetString(0).Trim();
            if (!WorkbenchSql.Identifier.IsMatch(key)) continue;
            fields.Add(new MenuDefaultColumn(key, reader.GetString(1).Trim(), reader.GetBoolean(2), ++order));
        }
        return new MenuDefaultColumns(targetTable, tableKind, fields);
    }

    /// <summary>
    /// 保存菜单默认查询列（SYSQL_DEFAULT，先删后插、事务、参数化）。
    /// 语义：T_ID=模块主表，T_ID_R=目标表。
    /// </summary>
    public async Task SaveDefaultColumnsAsync(int moduleId, SaveMenuDefaultColumns request, CancellationToken token)
    {
        var (masterTable, detailTable) = await ResolveModuleTablesAsync(moduleId, token);
        await using var connection = connections.Create();
        await connection.OpenAsync(token);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(token);
        try
        {
            await SaveDefaultColumnsScopedAsync(connection, transaction, masterTable, detailTable, request, token);
            await dirtyMarker.MarkDirtyAsync(connection, transaction, moduleId, "SYSTEM", token);
            // 默认列存在 SYSQL_DEFAULT、读的时候按 (T_ID=主表, T_ID_R=目标表) 取：主表相同的**其它模块**
            // 读的是同一组默认列，定义也会跟着变，因此按表再标一次（只标本模块会漏掉它们）。
            if (!string.IsNullOrWhiteSpace(masterTable))
                await dirtyMarker.MarkDirtyForTableAsync(connection, transaction, masterTable, "SYSTEM", token);
            await transaction.CommitAsync(token);
        }
        catch
        {
            await transaction.RollbackAsync(token);
            throw;
        }
        var targetTable = ResolveTargetTable(masterTable, detailTable, request.Table) ?? string.Empty;
        await auditWriter.WriteBestEffortAsync(moduleId, targetTable, "SAVE", "保存默认查询列", "SYSTEM", "MENU", result: 1, null, token);
        logger.LogInformation("保存默认查询列 module={ModuleId} kind={Kind} table={Table} fields={FieldCount}",
            moduleId, request.Table, targetTable, request.FieldIds.Count);
    }

    /// <summary>
    /// 在调用方事务内重写某一张表的默认查询列（先删后插、不提交、不标脏）。
    /// 表名由调用方给出（模块形态可能刚在同一事务内写入），字段仍按可见字段白名单校验。
    /// </summary>
    public async Task SaveDefaultColumnsScopedAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        string? masterTable,
        string? detailTable,
        SaveMenuDefaultColumns request,
        CancellationToken token)
    {
        if (request.Table is not ("master" or "detail"))
            throw new ArgumentException("table 仅支持 master 或 detail。");
        if (request.FieldIds.Count > 200 || request.FieldIds.Distinct(StringComparer.OrdinalIgnoreCase).Count() != request.FieldIds.Count)
            throw new ArgumentException("默认查询列配置无效（字段重复或过多）。");
        if (masterTable is null) throw new ArgumentException("该模块未配置操作主表。");
        var targetTable = ResolveTargetTable(masterTable, detailTable, request.Table)
            ?? throw new ArgumentException("该模块未配置操作" + (request.Table == "master" ? "主表" : "副表") + "。");
        var allowed = await ReadVisibleFieldKeysAsync(targetTable, token);
        if (request.FieldIds.Any(field => !allowed.Contains(field)))
            throw new ArgumentException("默认查询列包含无效字段。");

        await using (var delete = new SqlCommand("DELETE FROM dbo.SYSQL_DEFAULT WHERE T_ID=@MasterTable AND T_ID_R=@TargetTable;", connection, transaction))
        {
            delete.Parameters.Add("@MasterTable", SqlDbType.NVarChar, 100).Value = masterTable;
            delete.Parameters.Add("@TargetTable", SqlDbType.NVarChar, 100).Value = targetTable;
            await delete.ExecuteNonQueryAsync(token);
        }
        for (var i = 0; i < request.FieldIds.Count; i++)
        {
            await using var insert = new SqlCommand(
                "INSERT INTO dbo.SYSQL_DEFAULT (T_ID,T_ID_R,F_ID,F_IDX) VALUES (@MasterTable,@TargetTable,@FieldId,@Position);",
                connection, transaction);
            insert.Parameters.Add("@MasterTable", SqlDbType.NVarChar, 100).Value = masterTable;
            insert.Parameters.Add("@TargetTable", SqlDbType.NVarChar, 100).Value = targetTable;
            insert.Parameters.Add("@FieldId", SqlDbType.NVarChar, 100).Value = request.FieldIds[i];
            insert.Parameters.Add("@Position", SqlDbType.Int).Value = i + 1;
            await insert.ExecuteNonQueryAsync(token);
        }
    }

    public async Task<MenuAdminModule?> GetModuleAsync(int id, CancellationToken token)
    {
        var sql = """
            SELECT MODULES.M_IDX,M_ALIAS,M_DESC,M_URL,DETAIL_NO_FIELDS,DETAIL_NO_SAVE,
                   SEARCH_1,SEARCH_2,M_P_IDX,SORT_IDX,M_TAG,AUTO_APPROVE,IF_COPY,ERROR_NO_SAVE,SORT_FIELDS,
                   MASTER_TABLE,FILTER,DETAIL_TABLE,NOT_BACK_FIELDS_M,NOT_BACK_FIELDS,LAST_UPDATE_BY,LAST_UPDATE_DATE,
                   NULL AS FORM_TABS,NULL AS FORM_COLUMNS,M_ICON,EFFECT_ENGINE_TAG,
                   (SELECT TOP 1 LTRIM(RTRIM(t.T_DESC)) FROM dbo.TABLES t WITH (NOLOCK)
                     WHERE LTRIM(RTRIM(t.T_ID))=LTRIM(RTRIM(MODULES.MASTER_TABLE))) AS MASTER_TABLE_DESC,
                   (SELECT TOP 1 LTRIM(RTRIM(t.T_DESC)) FROM dbo.TABLES t WITH (NOLOCK)
                     WHERE LTRIM(RTRIM(t.T_ID))=LTRIM(RTRIM(MODULES.DETAIL_TABLE))) AS DETAIL_TABLE_DESC,
                   ISNULL(d.DIRTY_TAG,0) AS DIRTY_TAG,s.VERSION AS PUBLISH_VERSION,s.PUBLISHED_AT,
                   REMARK
                   FROM dbo.MODULES WITH (NOLOCK)
                   LEFT JOIN dbo.WORKBENCH_MODULE_DIRTY d WITH (NOLOCK) ON d.M_IDX=MODULES.M_IDX
                   LEFT JOIN dbo.WORKBENCH_DEFINITION_SNAPSHOT s WITH (NOLOCK) ON s.M_IDX=MODULES.M_IDX AND s.IS_CURRENT=1
                   WHERE MODULES.M_IDX=@Id;
            """;
        await using var connection = connections.Create();
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@Id", SqlDbType.Int).Value = id;
        await connection.OpenAsync(token);
        await using var reader = await command.ExecuteReaderAsync(token);
        return await reader.ReadAsync(token) ? ReadModule(reader) : null;
    }

    /// <summary>
    /// Reorders sibling menu items (top/up/down/bottom). Reads siblings within the same parent,
    /// determines target position by current display order (SORT_IDX, M_IDX), and rewrites
    /// the entire group of SORT_IDX values in 10-step increments. Boundary moves are no-ops.
    /// </summary>
    public async Task ReorderAsync(int id, string action, string updatedBy, CancellationToken token)
    {
        if (action is not ("top" or "up" or "down" or "bottom"))
            throw new ArgumentException("排序动作仅支持 top / up / down / bottom。");

        await using var connection = connections.Create();
        await connection.OpenAsync(token);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(token);
        try
        {
            if (!await ExistsAsync(connection, transaction, id, token))
                throw new KeyNotFoundException($"菜单节点 {id} 不存在。");

            // 父键归一化：M_P_IDX 为 NULL 或 0 均视为根级。
            int parentKey;
            await using (var nodeCommand = new SqlCommand(
                "SELECT ISNULL(M_P_IDX,0) FROM dbo.MODULES WITH (NOLOCK) WHERE M_IDX=@Id;", connection, transaction))
            {
                nodeCommand.Parameters.Add("@Id", SqlDbType.Int).Value = id;
                parentKey = (int)(await nodeCommand.ExecuteScalarAsync(token) ?? 0);
            }

            var siblingIds = new List<int>();
            await using (var siblingCommand = new SqlCommand(
                """
                SELECT M_IDX FROM dbo.MODULES WITH (NOLOCK)
                WHERE ISNULL(M_P_IDX,0)=@ParentKey
                ORDER BY SORT_IDX,M_IDX;
                """, connection, transaction))
            {
                siblingCommand.Parameters.Add("@ParentKey", SqlDbType.Int).Value = parentKey;
                await using var reader = await siblingCommand.ExecuteReaderAsync(token);
                while (await reader.ReadAsync(token))
                    siblingIds.Add(reader.GetInt32(0));
            }

            var currentIndex = siblingIds.IndexOf(id);
            if (currentIndex < 0)
                throw new KeyNotFoundException($"菜单节点 {id} 不存在。");

            var targetIndex = TargetIndex(currentIndex, siblingIds.Count, action);
            if (targetIndex == currentIndex)
            {
                await transaction.CommitAsync(token);
                return;
            }

            siblingIds.RemoveAt(currentIndex);
            siblingIds.Insert(targetIndex, id);

            const string updateSql = """
                UPDATE dbo.MODULES SET SORT_IDX=@SortIdx,LAST_UPDATE_BY=@UpdatedBy,LAST_UPDATE_DATE=GETDATE()
                WHERE M_IDX=@Id;
                """;
            for (var position = 0; position < siblingIds.Count; position++)
            {
                await using var update = new SqlCommand(updateSql, connection, transaction);
                update.Parameters.Add("@SortIdx", SqlDbType.Int).Value = (position + 1) * 10;
                update.Parameters.AddWithValue("@UpdatedBy", updatedBy);
                update.Parameters.Add("@Id", SqlDbType.Int).Value = siblingIds[position];
                await update.ExecuteNonQueryAsync(token);
            }

            await transaction.CommitAsync(token);
            logger.LogInformation("菜单排序 module={ModuleId} action={Action} parent={ParentKey} siblings={Count}",
                id, action, parentKey, siblingIds.Count);
        }
        catch
        {
            await transaction.RollbackAsync(token);
            throw;
        }
    }

    /// <summary>
    /// 菜单拖拽移动（跨父级或同级重排）：
    /// parentId=目标父节点（null/0=根级），beforeId=插入到该同级节点之前（null=追加末尾）。
    /// 事务内重写相关同级 SORT_IDX（10 步进）、更新节点 M_P_IDX/M_ROOT_IDX，
    /// 根变化时递归归一化整棵子树的 M_ROOT_IDX；目标为自身/子孙/循环引用时拒绝。
    /// </summary>
    public async Task MoveAsync(int id, int? parentId, int? beforeId, string updatedBy, CancellationToken token)
    {
        await using var connection = connections.Create();
        await connection.OpenAsync(token);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(token);
        try
        {
            int? oldParent;
            var oldRoot = 0;
            await using (var nodeCommand = new SqlCommand(
                "SELECT ISNULL(M_P_IDX,0),ISNULL(M_ROOT_IDX,0) FROM dbo.MODULES WITH (NOLOCK) WHERE M_IDX=@Id;",
                connection, transaction))
            {
                nodeCommand.Parameters.Add("@Id", SqlDbType.Int).Value = id;
                await using var reader = await nodeCommand.ExecuteReaderAsync(token);
                if (!await reader.ReadAsync(token))
                    throw new KeyNotFoundException($"菜单节点 {id} 不存在。");
                var parent = reader.GetInt32(0);
                oldParent = parent == 0 ? null : parent;
                oldRoot = reader.GetInt32(1);
            }
            // 父级键归一化：M_P_IDX 为 NULL 或 0 均为根级，避免 null!=0 把根级排序误判为跨父移动
            var oldParentKey = oldParent ?? 0;

            var newParent = parentId ?? 0;
            if (newParent == id)
                throw new ArgumentException("不能将菜单移动到自身下面。");
            var rootIdx = 0;
            if (newParent != 0)
            {
                if (!await ExistsAsync(connection, transaction, newParent, token))
                    throw new KeyNotFoundException($"目标父节点 {newParent} 不存在。");
                // 拒绝移动到自身子孙/循环引用
                rootIdx = await ResolveRootAsync(connection, transaction, newParent, id, token);
            }
            var effectiveRoot = rootIdx == 0 ? id : rootIdx;

            // 目标同级当前显示顺序（SORT_IDX,M_IDX）
            var target = await LoadSiblingIdsAsync(connection, transaction, newParent, token);
            var previousOrder = target.ToList();
            var currentIndex = target.IndexOf(id);
            if (currentIndex >= 0)
                target.RemoveAt(currentIndex);
            if (beforeId == id)
            {
                // beforeId 指向自身：同一父级下与原位一致，直接视为无操作
                if (oldParentKey == newParent)
                {
                    await transaction.CommitAsync(token);
                    return;
                }
                throw new ArgumentException("目标插入位置无效：beforeId 不能是移动节点自身。");
            }
            var insertIndex = beforeId is { } before ? target.IndexOf(before) : target.Count;
            if (insertIndex < 0)
                throw new ArgumentException($"目标插入位置无效：beforeId={beforeId} 不在同级菜单中。");
            target.Insert(insertIndex, id);

            // 同父且顺序未变化 → 无操作
            if (oldParentKey == newParent && previousOrder.SequenceEqual(target))
            {
                await transaction.CommitAsync(token);
                return;
            }

            // 重写目标同级 SORT_IDX（含被移动节点）
            await RenumberSiblingsAsync(connection, transaction, newParent, target, updatedBy, token);

            if (oldParentKey != newParent)
            {
                // 旧同级去除被移动节点后重排
                var oldGroup = await LoadSiblingIdsAsync(connection, transaction, oldParentKey, token);
                oldGroup.Remove(id);
                await RenumberSiblingsAsync(connection, transaction, oldParentKey, oldGroup, updatedBy, token);
                // 更新被移动节点的父/根与审计
                await using var update = new SqlCommand(
                    """
                    UPDATE dbo.MODULES SET M_P_IDX=@NewParent,M_ROOT_IDX=@NewRoot,
                                           LAST_UPDATE_BY=@UpdatedBy,LAST_UPDATE_DATE=GETDATE()
                    WHERE M_IDX=@Id;
                    """, connection, transaction);
                update.Parameters.Add("@NewParent", SqlDbType.Int).Value = newParent == 0 ? (object)DBNull.Value : newParent;
                update.Parameters.Add("@NewRoot", SqlDbType.Int).Value = effectiveRoot;
                update.Parameters.AddWithValue("@UpdatedBy", updatedBy);
                update.Parameters.Add("@Id", SqlDbType.Int).Value = id;
                await update.ExecuteNonQueryAsync(token);
            }

            // 根变化时递归归一化子树 M_ROOT_IDX（避免导航祖先补全走 M_ROOT_IDX 时失效）
            if (oldRoot != effectiveRoot)
                await NormalizeSubtreeRootAsync(connection, transaction, id, effectiveRoot, token);

            await transaction.CommitAsync(token);
            logger.LogInformation("菜单移动 module={ModuleId} parent={NewParent} before={BeforeId} root={Root}",
                id, newParent == 0 ? "null" : newParent.ToString(), beforeId?.ToString() ?? "end", effectiveRoot);
        }
        catch
        {
            await transaction.RollbackAsync(token);
            throw;
        }
    }

    /// <summary>
    /// 新增或更新菜单节点（事务）。oldId 为空表示新增；oldId 非空表示更新该编号的节点
    /// （允许把 M_IDX 改成新编号，级联子级 M_P_IDX、权限与引用表， 语义）。
    /// </summary>
    /// <summary>兼容入口：只保存模块行本身（关联配置由各自入口单独保存）。</summary>
    public Task<int> SaveAsync(MenuAdminModule input, int? oldId, string updatedBy, CancellationToken token)
        => SaveAllAsync(new SaveMenuModuleRequest(input, null, null), oldId, updatedBy, token);

    /// <summary>
    /// 保存模块：模块行 + 行为动作/校验规则 + 默认查询列在同一事务内落库。
    /// 顺序固定为「先模块、后关联配置」——动作配置的物理校验依赖刚写入的主/副表形态。
    /// BusinessConfig / DefaultColumns 为 null 或空表示该部分保持不动。
    /// </summary>
    public async Task<int> SaveAllAsync(SaveMenuModuleRequest request, int? oldId, string updatedBy, CancellationToken token)
    {
        // 呈现配置（打开方式 / 弹窗宽高 / 栅格列数）不在本端点的写面上：它归表单设计器，
        // 随版式保存 + 同请求重发布（保存即生效）。此处只读回显示，写路径见 FormLayoutRepository。
        var input = NormalizeForKind(request.Module);
        if (oldId is { } updateId && input.M_IDX <= 0)
            throw new ArgumentException("菜单编号必须为正整数。");
        Validate(input);
        var kind = ModuleRouteValidator.ResolveKind(input.M_URL, input.MASTER_TABLE);
        await using var connection = connections.Create();
        await connection.OpenAsync(token);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(token);
        try
        {
            // 新增时自动分配编号与排序号（前端不再手工维护；排序号默认追加到同级末尾）
            if (oldId is null)
            {
                if (input.M_IDX <= 0)
                    input = input with { M_IDX = await NextModuleIdAsync(connection, transaction, token) };
                if (input.SORT_IDX <= 0)
                    input = input with { SORT_IDX = await NextSiblingSortAsync(connection, transaction, input.M_P_IDX, token) };
            }
            await EnsureTablesExistAsync(connection, transaction, input, token);
            await EnsureShapeInvariantsAsync(connection, transaction, oldId, input, kind, token);
            var rootIdx = await ResolveRootAsync(connection, transaction, input.M_P_IDX, input.M_IDX, token);
            if (oldId is { } currentId)
            {
                if (!await ExistsAsync(connection, transaction, currentId, token))
                    throw new KeyNotFoundException($"菜单节点 {currentId} 不存在。");
            if (currentId != input.M_IDX && await ExistsAsync(connection, transaction, input.M_IDX, token))
                throw new ArgumentException($"菜单编号 {input.M_IDX} 已被其它节点占用。");
            var oldRoot = await ReadRootAsync(connection, transaction, currentId, token);
            await UpdateAsync(connection, transaction, currentId, input, rootIdx, updatedBy, token);
            // 仅父级变化（编号不变）时也归一化子树 M_ROOT_IDX，避免根引用陈旧
            var effectiveRoot = rootIdx == 0 ? input.M_IDX : rootIdx;
            if (oldRoot != effectiveRoot)
                await NormalizeSubtreeRootAsync(connection, transaction, currentId, effectiveRoot, token);
            if (currentId != input.M_IDX)
            {
                    // 编号变更级联：子级 M_P_IDX、M_ROOT_IDX、权限与引用表
                    await ChangeModuleIdAsync(connection, transaction, currentId, input.M_IDX, token);
                    // 子树根编号归一化
                    await using var normalize = new SqlCommand(
                        "UPDATE dbo.MODULES SET M_ROOT_IDX=@Root WHERE M_ROOT_IDX=@Id;", connection, transaction);
                    normalize.Parameters.Add("@Root", SqlDbType.Int).Value = rootIdx;
                    normalize.Parameters.Add("@Id", SqlDbType.Int).Value = input.M_IDX;
                    await normalize.ExecuteNonQueryAsync(token);
                }
            }
            else
            {
                if (input.M_IDX <= 0)
                    throw new ArgumentException("菜单编号必须为正整数。");
                if (await ExistsAsync(connection, transaction, input.M_IDX, token))
                    throw new ArgumentException($"菜单编号 {input.M_IDX} 已存在。");
                await InsertAsync(connection, transaction, input, rootIdx, updatedBy, token);
            }
            if (request.BusinessConfig is { } businessConfig)
            {
                await businessConfigRepository.SaveScopedAsync(
                    connection, transaction, input.M_IDX, businessConfig, updatedBy, token);
            }
            if (request.DefaultColumns is { Count: > 0 } defaultColumns)
            {
                var masterTable = string.IsNullOrWhiteSpace(input.MASTER_TABLE) ? null : input.MASTER_TABLE.Trim();
                var detailTable = string.IsNullOrWhiteSpace(input.DETAIL_TABLE) ? null : input.DETAIL_TABLE.Trim();
                foreach (var columns in defaultColumns)
                    await SaveDefaultColumnsScopedAsync(connection, transaction, masterTable, detailTable, columns, token);
                // 同 SaveDefaultColumnsAsync：默认列按表存，主表相同的其它模块也会跟着变，按表补标。
                if (!string.IsNullOrWhiteSpace(masterTable))
                    await dirtyMarker.MarkDirtyForTableAsync(connection, transaction, masterTable, updatedBy, token);
            }
            // 只有统一工作台模块需要重发布：目录节点与自定义承载页不装配工作台定义，
            // 给它们标脏只会在"待发布"清单里挂上永远发布不出来的条目（发布门会直接拒绝它们）。
            // 形态从工作台切走的节点要把旧脏标记撤掉，否则它会一直留在清单里。
            if (kind == ModuleNodeKind.Workbench)
            {
                await dirtyMarker.MarkDirtyAsync(connection, transaction, input.M_IDX, updatedBy, token);
            }
            else
            {
                await dirtyMarker.ClearDirtyAsync(connection, transaction, input.M_IDX, token);
            }
            if (oldId is { } oldModuleId && oldModuleId != input.M_IDX)
            {
                // 编号变更后旧编号已不存在：脏标记表没有外键、编号级联语句也不管它，
                // 在这里再标一笔只会留下一个指向空编号的孤儿行。
                await dirtyMarker.ClearDirtyAsync(connection, transaction, oldModuleId, token);
            }
            await transaction.CommitAsync(token);
            await auditWriter.WriteBestEffortAsync(input.M_IDX, $"{oldId}->{input.M_IDX}", "SAVE", "保存菜单节点", updatedBy, "MENU", result: 1, null, token);
            logger.LogInformation("菜单保存 module={ModuleId} updatedBy={UpdatedBy}", input.M_IDX, updatedBy);
            return input.M_IDX;
        }
        catch
        {
            await transaction.RollbackAsync(token);
            throw;
        }
    }

    /// <summary>模块定义快照的历史版本列表（新→旧），供管理端查看发布记录。</summary>
    public async Task<IReadOnlyList<MenuModuleVersion>> GetVersionsAsync(int moduleId, CancellationToken token)
    {
        const string sql = """
            SELECT VERSION,PUBLISHED_BY,PUBLISHED_AT,VALIDATION_STATUS,IS_CURRENT
            FROM dbo.WORKBENCH_DEFINITION_SNAPSHOT WITH (NOLOCK)
            WHERE M_IDX=@ModuleId
            ORDER BY VERSION DESC;
            """;
        await using var connection = connections.Create();
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@ModuleId", SqlDbType.Int).Value = moduleId;
        await connection.OpenAsync(token);
        await using var reader = await command.ExecuteReaderAsync(token);
        var result = new List<MenuModuleVersion>();
        while (await reader.ReadAsync(token))
        {
            var version = reader.GetInt32(0);
            result.Add(new MenuModuleVersion(
                version,
                $"module-{moduleId}-v{version}",
                GetString(reader, 1),
                reader.IsDBNull(2) ? null : reader.GetDateTime(2),
                GetString(reader, 3) ?? string.Empty,
                reader.GetBoolean(4)));
        }
        return result;
    }

    /// <summary>
    /// 仅改名（菜单树的轻量操作）：只写 M_DESC，不标脏、不进模块保存事务。
    /// 名称属菜单呈现属性，保存后立即在导航生效；模块定义快照在下一次发布时带出新名称。
    /// </summary>
    public async Task RenameAsync(int id, string description, string updatedBy, CancellationToken token)
    {
        var name = (description ?? string.Empty).Trim();
        if (name.Length == 0) throw new ArgumentException("菜单名称不允许为空。");
        if (name.Length > 500) throw new ArgumentException("菜单名称过长（最多 500 字）。");
        const string sql = "UPDATE dbo.MODULES SET M_DESC=@Name,LAST_UPDATE_BY=@UpdatedBy,LAST_UPDATE_DATE=GETDATE() WHERE M_IDX=@Id;";
        await using var connection = connections.Create();
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@Name", SqlDbType.NVarChar, 500).Value = name;
        command.Parameters.Add("@UpdatedBy", SqlDbType.NVarChar, 50).Value = updatedBy;
        command.Parameters.Add("@Id", SqlDbType.Int).Value = id;
        await connection.OpenAsync(token);
        if (await command.ExecuteNonQueryAsync(token) == 0)
            throw new KeyNotFoundException($"菜单节点 {id} 不存在。");
        await auditWriter.WriteBestEffortAsync(id, id.ToString(), "RENAME", "菜单重命名", updatedBy, "MENU", result: 1, null, token);
        logger.LogInformation("菜单重命名 module={ModuleId} name={Name}", id, name);
    }

    /// <summary>
    /// 启用/停用（M_TAG）：菜单呈现属性，只决定侧栏是否显示该模块，不标脏、不需发布。
    /// </summary>
    public async Task SetEnabledAsync(int id, bool enabled, string updatedBy, CancellationToken token)
    {
        const string sql = "UPDATE dbo.MODULES SET M_TAG=@Enabled,LAST_UPDATE_BY=@UpdatedBy,LAST_UPDATE_DATE=GETDATE() WHERE M_IDX=@Id;";
        await using var connection = connections.Create();
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@Enabled", SqlDbType.Bit).Value = enabled;
        command.Parameters.Add("@UpdatedBy", SqlDbType.NVarChar, 50).Value = updatedBy;
        command.Parameters.Add("@Id", SqlDbType.Int).Value = id;
        await connection.OpenAsync(token);
        if (await command.ExecuteNonQueryAsync(token) == 0)
            throw new KeyNotFoundException($"菜单节点 {id} 不存在。");
        await auditWriter.WriteBestEffortAsync(id, id.ToString(), enabled ? "ENABLE" : "DISABLE",
            enabled ? "启用菜单" : "停用菜单", updatedBy, "MENU", result: 1, null, token);
        logger.LogInformation("菜单启停 module={ModuleId} enabled={Enabled}", id, enabled);
    }

    /// <summary>
    /// 删除菜单节点：有下级菜单、或主/副表已产生业务数据时拒绝（新保护，为递归删除且无校验）；
    /// 通过保护后删除节点并清理 SYSDD/SYSDH 权限引用。
    /// </summary>
    public async Task DeleteAsync(int id, CancellationToken token)
    {
        await using var connection = connections.Create();
        await connection.OpenAsync(token);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(token);
        try
        {
            if (!await ExistsAsync(connection, transaction, id, token))
                throw new KeyNotFoundException($"菜单节点 {id} 不存在。");
            await using (var childCheck = new SqlCommand(
                "SELECT CASE WHEN EXISTS(SELECT 1 FROM dbo.MODULES WHERE M_P_IDX=@Id) THEN 1 ELSE 0 END;", connection, transaction))
            {
                childCheck.Parameters.Add("@Id", SqlDbType.Int).Value = id;
                if ((int)(await childCheck.ExecuteScalarAsync(token) ?? 0) == 1)
                    throw new ArgumentException("该菜单存在下级菜单，不可删除。请先删除或移动其下级菜单。");
            }
            var (master, detail) = await ResolveModuleTablesAsync(id, token);
            foreach (var (table, label) in new[] { (master, "主表"), (detail, "副表") })
            {
                if (table is null) continue;
                await EnsureTableExistsAsync(connection, transaction, table, label, token);
                if (await TableHasRowsAsync(connection, transaction, table, token))
                    throw new ArgumentException($"该模块已产生业务数据（{label} {table} 存在记录），不可删除。");
            }
            await using (var delete = new SqlCommand("DELETE FROM dbo.MODULES WHERE M_IDX=@Id;", connection, transaction))
            {
                delete.Parameters.Add("@Id", SqlDbType.Int).Value = id;
                await delete.ExecuteNonQueryAsync(token);
            }
            await using (var rights = new SqlCommand(
                // 按钮授权行按模块清理：模块没了，它的按钮名单也不能留在库里（与模块/报表权限同处清理）。
                "DELETE FROM dbo.SYSDD WHERE M_IDX=@Id; DELETE FROM dbo.SYSDH WHERE M_IDX=@Id;"
                + " DELETE FROM dbo.SYSDD_BUTTON WHERE M_IDX=@Id; DELETE FROM dbo.SYSDH_BUTTON WHERE M_IDX=@Id;",
                connection, transaction))
            {
                rights.Parameters.Add("@Id", SqlDbType.Int).Value = id;
                await rights.ExecuteNonQueryAsync(token);
            }
            await transaction.CommitAsync(token);
            await auditWriter.WriteBestEffortAsync(id, $"{id}", "DELETE", "菜单节点删除", "SYSTEM", "MENU", result: 1, null, token);
            logger.LogInformation("菜单删除 module={ModuleId}", id);
        }
        catch
        {
            await transaction.RollbackAsync(token);
            throw;
        }
    }

    private static void Validate(MenuAdminModule input)
    {
        if (string.IsNullOrWhiteSpace(input.M_DESC)) throw new ArgumentException("菜单名称不允许为空。");
        if (input.M_P_IDX is { } parent && parent == input.M_IDX)
            throw new ArgumentException("上级菜单不能是自身。");
        if (!string.IsNullOrWhiteSpace(input.MASTER_TABLE) && !WorkbenchSql.Identifier.IsMatch(input.MASTER_TABLE))
            throw new ArgumentException($"操作主表名无效：{input.MASTER_TABLE}");
        if (!string.IsNullOrWhiteSpace(input.DETAIL_TABLE) && !WorkbenchSql.Identifier.IsMatch(input.DETAIL_TABLE))
            throw new ArgumentException($"操作副表名无效：{input.DETAIL_TABLE}");
        if (!ModuleRouteValidator.IsValidHostUrl(input.M_URL))
            throw new ArgumentException("页面链接不符合路由契约：应为承载页（如 /workbench，不带编号）、精确路径，或留空（目录节点/未声明承载页）。");
    }

    /// <summary>
    /// 形态外的配置项一律落回默认值。理由不是"省事"，而是这些列在那些形态下**没有消费方**：
    /// 排序字段 / 不可解批 / 自动批核 / 可复制 / 异常不可保存 / 明细必需字段 / 无明细不可保存
    /// 全部经工作台定义或效果引擎生效；分组表达式只在工作台列表上取分组值。
    ///
    /// 两处**不**清空：
    /// - <c>FILTER</c>：它同时是报表的数据范围与选择器的数据范围，任何形态都会被**实时**读
    ///   （不经快照），只在"没有主表、无从过滤"时才清掉；
    /// - <c>SEARCH_1/2</c>：搜索中心是独立于承载页的可达面（`/search-center`），只要有主表就能用；
    ///   没有主表则必须清掉——搜索中心的模块清单直接读 `MASTER_TABLE`，为 NULL 会整页 500。
    /// </summary>
    private static MenuAdminModule NormalizeForKind(MenuAdminModule input)
    {
        if (ModuleRouteValidator.ResolveKind(input.M_URL, input.MASTER_TABLE) == ModuleNodeKind.Workbench)
            return input;
        var hasMaster = !string.IsNullOrWhiteSpace(input.MASTER_TABLE);
        return input with
        {
            SORT_FIELDS = null,
            NOT_BACK_FIELDS = null,
            NOT_BACK_FIELDS_M = null,
            AUTO_APPROVE = false,
            IF_COPY = false,
            ERROR_NO_SAVE = false,
            DETAIL_NO_FIELDS = null,
            DETAIL_NO_SAVE = false,
            EffectEngineTag = false,
            FILTER = hasMaster ? input.FILTER : null,
            SEARCH_1 = hasMaster ? input.SEARCH_1 : false,
            SEARCH_2 = hasMaster ? input.SEARCH_2 : false,
        };
    }

    /// <summary>库内已落地的形态事实（主表 / 副表 / 数据范围），用于比对"这次改了没有"。</summary>
    private sealed record StoredShape(string? Master, string? Detail, string? Filter);

    /// <summary>
    /// 形态不变量的服务端兜底（前端隐藏只改善体验，真源在服务端）：
    /// ① 非统一工作台模块的主表/副表由开发团队定义——它们是报表数据集、搜索中心与选择器的锚点，
    ///    菜单管理只展示不允许改；
    /// ② <c>FILTER</c> 被报表与选择器实时消费，文本一变就必须过受控解析器；文本没变则放行，
    ///    免得历史遗留的坏值把同一行上其它字段的保存一并卡死。
    /// </summary>
    private async Task EnsureShapeInvariantsAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        int? oldId,
        MenuAdminModule input,
        ModuleNodeKind kind,
        CancellationToken token)
    {
        if (oldId is { } currentId)
        {
            var stored = await ReadStoredShapeAsync(connection, transaction, currentId, token);
            if (kind != ModuleNodeKind.Workbench
                && (!SameTable(stored.Master, input.MASTER_TABLE) || !SameTable(stored.Detail, input.DETAIL_TABLE)))
            {
                throw new ArgumentException(
                    "该节点不是统一工作台模块，它的主表/副表由开发团队定义（报表数据集、搜索中心与选择器的锚点），不能在菜单管理里修改。");
            }
            if (SameFilter(stored.Filter, input.FILTER)) return;
        }
        await EnsureFilterSupportedAsync(connection, transaction, input, token);
    }

    private static async Task<StoredShape> ReadStoredShapeAsync(
        SqlConnection connection, SqlTransaction transaction, int moduleId, CancellationToken token)
    {
        const string sql = """
            SELECT LTRIM(RTRIM(ISNULL(MASTER_TABLE,''))),LTRIM(RTRIM(ISNULL(DETAIL_TABLE,''))),LTRIM(RTRIM(ISNULL(FILTER,'')))
            FROM dbo.MODULES WITH (NOLOCK) WHERE M_IDX=@Id;
            """;
        await using var command = new SqlCommand(sql, connection, transaction);
        command.Parameters.Add("@Id", SqlDbType.Int).Value = moduleId;
        await using var reader = await command.ExecuteReaderAsync(token);
        if (!await reader.ReadAsync(token))
            throw new KeyNotFoundException($"菜单节点 {moduleId} 不存在。");
        return new StoredShape(
            NullIfBlank(reader.GetString(0)),
            NullIfBlank(reader.GetString(1)),
            NullIfBlank(reader.GetString(2)));
    }

    private static async Task EnsureFilterSupportedAsync(
        SqlConnection connection, SqlTransaction transaction, MenuAdminModule input, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(input.FILTER)) return;
        var masterTable = input.MASTER_TABLE?.Trim() ?? string.Empty;
        if (masterTable.Length == 0)
            throw new ArgumentException("未配置操作主表时不允许设置模块过滤条件（没有可过滤的对象）。");
        // 本调用发生在**已开事务**的连接上：命令必须显式带上事务，否则驱动直接拒绝执行
        // （同一口径见 WorkbenchApprovalService.CheckApprovalPreconditionsAsync 的 transaction 参数）。
        if (!await WorkbenchDefinitionValidator.TryValidateModuleFilterAsync(connection, transaction, masterTable, input.FILTER, token))
            throw new ArgumentException(
                "模块过滤条件超出受控子集，已拒绝保存（与报表数据范围、选择器数据范围共用同一解析器）。");
    }

    private static string? NullIfBlank(string value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static bool SameTable(string? left, string? right) =>
        string.Equals(left ?? string.Empty, right?.Trim() ?? string.Empty, StringComparison.OrdinalIgnoreCase);

    private static bool SameFilter(string? left, string? right) =>
        string.Equals(left ?? string.Empty, right?.Trim() ?? string.Empty, StringComparison.Ordinal);

    private static MenuAdminModule ReadModule(SqlDataReader reader) => new(
        reader.GetInt32(0),
        GetString(reader, 1), GetString(reader, 2) ?? string.Empty, GetString(reader, 3), GetString(reader, 4),
        GetBool(reader, 5), GetBool(reader, 6), GetBool(reader, 7),
        reader.IsDBNull(8) ? null : reader.GetInt32(8),
        // SORT_IDX 在库里是**可空**的（节点未排序时就是 NULL）：直接 GetInt32 会在
        // 「可空标志位为 NULL 时列表照常返回」这条口径下整列崩掉（SqlNullValueException）。
        // NULL 按"未排序"处理，取 0。
        reader.IsDBNull(9) ? 0 : reader.GetInt32(9), GetBool(reader, 10), GetBool(reader, 11), GetBool(reader, 12),
        GetBool(reader, 13), GetString(reader, 14),
        GetString(reader, 15), GetString(reader, 16), GetString(reader, 17),
        GetString(reader, 18), GetString(reader, 19),
        GetString(reader, 20),
        reader.IsDBNull(21) ? null : reader.GetDateTime(21),
        GetString(reader, 22),
        reader.IsDBNull(23) ? (int?)null : (int)reader.GetByte(23),
        GetString(reader, 24),
        EffectEngineTag: GetBool(reader, 25),
        MasterTableDesc: GetString(reader, 26),
        DetailTableDesc: GetString(reader, 27),
        DirtyTag: GetBool(reader, 28),
        PublishVersion: reader.IsDBNull(29) ? null : reader.GetInt32(29),
        PublishedAt: reader.IsDBNull(30) ? null : reader.GetDateTime(30),
        // REMARK 追加在两个 SELECT 的**最末尾**（下标 31）：既有 0..30 的下标一个都不用挪
        REMARK: GetString(reader, 31),
        // 形态由承载页（下标 3）与主表（下标 15）算出，不落库：库里只有一个事实来源，
        // 投影只是它的读法（见 ModuleNodeKind）。
        NODE_KIND: ModuleRouteValidator.WireName(
            ModuleRouteValidator.ResolveKind(GetString(reader, 3), GetString(reader, 15))));

    private async Task<(string? Master, string? Detail)> ResolveModuleTablesAsync(int moduleId, CancellationToken token)
    {
        const string sql = "SELECT MASTER_TABLE,DETAIL_TABLE FROM dbo.MODULES WITH (NOLOCK) WHERE M_IDX=@Id;";
        await using var connection = connections.Create();
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@Id", SqlDbType.Int).Value = moduleId;
        await connection.OpenAsync(token);
        await using var reader = await command.ExecuteReaderAsync(token);
        if (!await reader.ReadAsync(token)) return (null, null);
        var master = reader.IsDBNull(0) ? null : reader.GetString(0).Trim();
        var detail = reader.IsDBNull(1) ? null : reader.GetString(1).Trim();
        return (string.IsNullOrWhiteSpace(master) ? null : master, string.IsNullOrWhiteSpace(detail) ? null : detail);
    }

    private static string? ResolveTargetTable(string? masterTable, string? detailTable, string tableKind) =>
        tableKind == "master" ? masterTable : detailTable;

    private async Task<IReadOnlySet<string>> ReadVisibleFieldKeysAsync(string targetTable, CancellationToken token)
    {
        const string sql = """
            SELECT LTRIM(RTRIM(F_ID)) FROM dbo.FIELDS WITH (NOLOCK)
            WHERE T_ID=@TargetTable AND COALESCE(IS_VISIBLE,1)=1 AND COALESCE(IS_VIRTUAL,0)=0
              AND EXISTS (SELECT 1 FROM sys.columns c
                          JOIN sys.objects o ON c.object_id=o.object_id AND o.type IN ('U','V')
                          JOIN sys.schemas s ON o.schema_id=s.schema_id
                          WHERE s.name=N'dbo' AND o.name=@TargetTable AND c.name=F_ID);
            """;
        await using var connection = connections.Create();
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@TargetTable", SqlDbType.NVarChar, 100).Value = targetTable;
        await connection.OpenAsync(token);
        await using var reader = await command.ExecuteReaderAsync(token);
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (await reader.ReadAsync(token))
        {
            var key = reader.GetString(0).Trim();
            if (WorkbenchSql.Identifier.IsMatch(key)) result.Add(key);
        }
        return result;
    }

    private static string? GetString(SqlDataReader reader, int index) =>
        reader.IsDBNull(index) ? null : reader.GetString(index).Trim();

    /// <summary>
    /// 读可空 bit 列：NULL 一律当 0。
    ///
    /// MODULES 里这批标志位（M_TAG / AUTO_APPROVE / IF_COPY / ERROR_NO_SAVE /
    /// EFFECT_ENGINE_TAG 等）**是可空的**——历史上就出现过整批 NULL（分组标志列 GROUP1..5
    /// 就是那一批，已由迁移 341 下线，同迁移给剩余标志列补了 DEFAULT）。直接 GetBoolean
    /// 会在遇到 NULL 时抛 SqlNullValueException，把整个菜单管理列表打成 500；而本查询用的是
    /// NOLOCK，还会读到别的事务里尚未提交、这些列为 NULL 的半成品行，进一步放大触发面。
    /// "NULL 当 0"与库内其它读取口径（`ISNULL(列,0)`）一致。
    /// </summary>
    private static bool GetBool(SqlDataReader reader, int index) =>
        reader.IsDBNull(index) ? false : reader.GetBoolean(index);

    private static string? NullIfEmpty(string value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static async Task<int> NextModuleIdAsync(SqlConnection connection, SqlTransaction transaction, CancellationToken token)
    {
        await using var command = new SqlCommand("SELECT ISNULL(MAX(M_IDX),0)+1 FROM dbo.MODULES;", connection, transaction);
        return (int)(await command.ExecuteScalarAsync(token) ?? 1);
    }

    private static async Task<int> NextSiblingSortAsync(SqlConnection connection, SqlTransaction transaction, int? parentId, CancellationToken token)
    {
        await using var command = new SqlCommand(
            "SELECT ISNULL(MAX(SORT_IDX),0)+10 FROM dbo.MODULES WHERE ISNULL(M_P_IDX,0)=@ParentKey;", connection, transaction);
        command.Parameters.Add("@ParentKey", SqlDbType.Int).Value = parentId ?? 0;
        return (int)(await command.ExecuteScalarAsync(token) ?? 10);
    }

    private static async Task EnsureTablesExistAsync(SqlConnection connection, SqlTransaction transaction, MenuAdminModule input, CancellationToken token)
    {
        if (!string.IsNullOrWhiteSpace(input.MASTER_TABLE))
            await EnsureTableExistsAsync(connection, transaction, input.MASTER_TABLE, "操作主表", token);
        if (!string.IsNullOrWhiteSpace(input.DETAIL_TABLE))
            await EnsureTableExistsAsync(connection, transaction, input.DETAIL_TABLE, "操作副表", token);
    }

    private static async Task EnsureTableExistsAsync(SqlConnection connection, SqlTransaction transaction, string table, string label, CancellationToken token)
    {
        if (!WorkbenchSql.Identifier.IsMatch(table))
            throw new ArgumentException($"{label}名无效：{table}");
        await using var command = new SqlCommand(
            "SELECT CASE WHEN EXISTS(SELECT 1 FROM dbo.TABLES WITH (NOLOCK) WHERE LTRIM(RTRIM(T_ID))=@Table) THEN 1 ELSE 0 END;",
            connection, transaction);
        command.Parameters.Add("@Table", SqlDbType.NVarChar, 100).Value = table.Trim();
        if ((int)(await command.ExecuteScalarAsync(token) ?? 0) != 1)
            throw new ArgumentException($"{label}不存在：{table}");
    }

    private static async Task<bool> TableHasRowsAsync(SqlConnection connection, SqlTransaction transaction, string table, CancellationToken token)
    {
        // 表名已由 Identifier 与 TABLES 元数据双重校验，动态标识符仅来自服务端白名单
        var sql = $"SELECT CASE WHEN EXISTS(SELECT 1 FROM dbo.[{table}]) THEN 1 ELSE 0 END;";
        await using var command = new SqlCommand(sql, connection, transaction);
        return (int)(await command.ExecuteScalarAsync(token) ?? 0) == 1;
    }

    private static async Task<bool> ExistsAsync(SqlConnection connection, SqlTransaction transaction, int id, CancellationToken token)
    {
        await using var command = new SqlCommand("SELECT CASE WHEN EXISTS(SELECT 1 FROM dbo.MODULES WHERE M_IDX=@Id) THEN 1 ELSE 0 END;", connection, transaction);
        command.Parameters.Add("@Id", SqlDbType.Int).Value = id;
        return (int)(await command.ExecuteScalarAsync(token) ?? 0) == 1;
    }

    private static async Task<int> ReadRootAsync(SqlConnection connection, SqlTransaction transaction, int id, CancellationToken token)
    {
        await using var command = new SqlCommand(
            "SELECT ISNULL(M_ROOT_IDX,0) FROM dbo.MODULES WITH (NOLOCK) WHERE M_IDX=@Id;", connection, transaction);
        command.Parameters.Add("@Id", SqlDbType.Int).Value = id;
        return (int)(await command.ExecuteScalarAsync(token) ?? 0);
    }

    private static async Task<List<int>> LoadSiblingIdsAsync(SqlConnection connection, SqlTransaction transaction, int parentKey, CancellationToken token)
    {
        const string sql = """
            SELECT M_IDX FROM dbo.MODULES WITH (NOLOCK)
            WHERE ISNULL(M_P_IDX,0)=@ParentKey
            ORDER BY SORT_IDX,M_IDX;
            """;
        await using var command = new SqlCommand(sql, connection, transaction);
        command.Parameters.Add("@ParentKey", SqlDbType.Int).Value = parentKey;
        await using var reader = await command.ExecuteReaderAsync(token);
        var ids = new List<int>();
        while (await reader.ReadAsync(token))
            ids.Add(reader.GetInt32(0));
        return ids;
    }

    private static async Task RenumberSiblingsAsync(
        SqlConnection connection, SqlTransaction transaction, int parentKey, IReadOnlyList<int> ids,
        string updatedBy, CancellationToken token)
    {
        const string sql = """
            UPDATE dbo.MODULES SET SORT_IDX=@SortIdx,LAST_UPDATE_BY=@UpdatedBy,LAST_UPDATE_DATE=GETDATE()
            WHERE M_IDX=@Id;
            """;
        for (var position = 0; position < ids.Count; position++)
        {
            await using var command = new SqlCommand(sql, connection, transaction);
            command.Parameters.Add("@SortIdx", SqlDbType.Int).Value = (position + 1) * 10;
            command.Parameters.AddWithValue("@UpdatedBy", updatedBy);
            command.Parameters.Add("@Id", SqlDbType.Int).Value = ids[position];
            await command.ExecuteNonQueryAsync(token);
        }
    }

    private static async Task NormalizeSubtreeRootAsync(
        SqlConnection connection, SqlTransaction transaction, int nodeId, int rootId, CancellationToken token)
    {
        const string sql = """
            WITH T AS (
                SELECT M_IDX FROM dbo.MODULES WHERE M_IDX=@Id
                UNION ALL
                SELECT m.M_IDX FROM dbo.MODULES m INNER JOIN T ON m.M_P_IDX=T.M_IDX
            )
            UPDATE m SET M_ROOT_IDX=@Root FROM dbo.MODULES m INNER JOIN T ON T.M_IDX=m.M_IDX;
            """;
        await using var command = new SqlCommand(sql, connection, transaction);
        command.Parameters.Add("@Id", SqlDbType.Int).Value = nodeId;
        command.Parameters.Add("@Root", SqlDbType.Int).Value = rootId;
        await command.ExecuteNonQueryAsync(token);
    }

    private static async Task<int> ResolveRootAsync(SqlConnection connection, SqlTransaction transaction, int? parentId, int nodeId, CancellationToken token)
    {
        if (parentId is null or 0) return 0;
        var current = parentId.Value;
        var visited = new HashSet<int> { current };
        if (current == nodeId) throw new ArgumentException("上级菜单不能是自身。");
        for (var depth = 0; depth < 32; depth++)
        {
            await using var command = new SqlCommand("SELECT M_P_IDX FROM dbo.MODULES WITH (NOLOCK) WHERE M_IDX=@Id;", connection, transaction);
            command.Parameters.Add("@Id", SqlDbType.Int).Value = current;
            var value = await command.ExecuteScalarAsync(token);
            if (value is null || value == DBNull.Value) return current;
            var parent = (int)value;
            if (parent == 0) return current;
            if (parent == nodeId) throw new ArgumentException("上级菜单不能是自身或其子孙。");
            if (!visited.Add(parent)) throw new ArgumentException("上级菜单存在循环引用。");
            current = parent;
        }
        throw new ArgumentException("上级菜单链过深（超过 32 级）。");
    }

    private static async Task InsertAsync(SqlConnection connection, SqlTransaction transaction, MenuAdminModule m, int rootIdx, string updatedBy, CancellationToken token)
    {
        const string sql = """
            INSERT INTO dbo.MODULES
             (M_IDX,M_ALIAS,M_DESC,M_URL,DETAIL_NO_FIELDS,DETAIL_NO_SAVE,SEARCH_1,SEARCH_2,
              M_P_IDX,SORT_IDX,M_TAG,M_ROOT_IDX,AUTO_APPROVE,IF_COPY,ERROR_NO_SAVE,SORT_FIELDS,
              MASTER_TABLE,FILTER,DETAIL_TABLE,NOT_BACK_FIELDS_M,NOT_BACK_FIELDS,LAST_UPDATE_BY,LAST_UPDATE_DATE,
              M_ICON,EFFECT_ENGINE_TAG,REMARK)
              VALUES
              (@M_IDX,@M_ALIAS,@M_DESC,@M_URL,@DETAIL_NO_FIELDS,@DETAIL_NO_SAVE,@SEARCH_1,@SEARCH_2,
              @M_P_IDX,@SORT_IDX,@M_TAG,@M_ROOT_IDX,@AUTO_APPROVE,@IF_COPY,@ERROR_NO_SAVE,@SORT_FIELDS,
              @MASTER_TABLE,@FILTER,@DETAIL_TABLE,@NOT_BACK_FIELDS_M,@NOT_BACK_FIELDS,@LAST_UPDATE_BY,GETDATE(),
              @M_ICON,@EFFECT_ENGINE_TAG,@REMARK);
            """;
        await using var command = BuildCommand(connection, transaction, sql, m, rootIdx, updatedBy);
        await command.ExecuteNonQueryAsync(token);
    }

    private static async Task UpdateAsync(SqlConnection connection, SqlTransaction transaction, int oldId, MenuAdminModule m, int rootIdx, string updatedBy, CancellationToken token)
    {
        const string sql = """
            UPDATE dbo.MODULES SET
              M_IDX=@M_IDX,M_ALIAS=@M_ALIAS,M_DESC=@M_DESC,M_URL=@M_URL,
              DETAIL_NO_FIELDS=@DETAIL_NO_FIELDS,DETAIL_NO_SAVE=@DETAIL_NO_SAVE,SEARCH_1=@SEARCH_1,SEARCH_2=@SEARCH_2,
              M_P_IDX=@M_P_IDX,SORT_IDX=@SORT_IDX,M_TAG=@M_TAG,M_ROOT_IDX=@M_ROOT_IDX,AUTO_APPROVE=@AUTO_APPROVE,
              IF_COPY=@IF_COPY,ERROR_NO_SAVE=@ERROR_NO_SAVE,SORT_FIELDS=@SORT_FIELDS,
              MASTER_TABLE=@MASTER_TABLE,FILTER=@FILTER,DETAIL_TABLE=@DETAIL_TABLE,
              NOT_BACK_FIELDS_M=@NOT_BACK_FIELDS_M,NOT_BACK_FIELDS=@NOT_BACK_FIELDS,
              LAST_UPDATE_BY=@LAST_UPDATE_BY,LAST_UPDATE_DATE=GETDATE(),
              M_ICON=@M_ICON,
              EFFECT_ENGINE_TAG=@EFFECT_ENGINE_TAG,
              REMARK=@REMARK
              WHERE M_IDX=@OLD_IDX;
            """;
        await using var command = BuildCommand(connection, transaction, sql, m, rootIdx, updatedBy);
        command.Parameters.Add("@OLD_IDX", SqlDbType.Int).Value = oldId;
        await command.ExecuteNonQueryAsync(token);
    }

    private static SqlCommand BuildCommand(SqlConnection connection, SqlTransaction transaction, string sql, MenuAdminModule m, int rootIdx, string updatedBy)
    {
        var command = new SqlCommand(sql, connection, transaction);
        command.Parameters.Add("@M_IDX", SqlDbType.Int).Value = m.M_IDX;
        command.Parameters.AddWithValue("@M_ALIAS", (object?)m.M_ALIAS ?? DBNull.Value);
        command.Parameters.AddWithValue("@M_DESC", m.M_DESC);
        command.Parameters.AddWithValue("@M_URL", (object?)m.M_URL ?? DBNull.Value);
        command.Parameters.AddWithValue("@DETAIL_NO_FIELDS", (object?)m.DETAIL_NO_FIELDS ?? DBNull.Value);
        command.Parameters.Add("@DETAIL_NO_SAVE", SqlDbType.Bit).Value = m.DETAIL_NO_SAVE;
        command.Parameters.Add("@SEARCH_1", SqlDbType.Bit).Value = m.SEARCH_1;
        command.Parameters.Add("@SEARCH_2", SqlDbType.Bit).Value = m.SEARCH_2;
        command.Parameters.Add("@M_P_IDX", SqlDbType.Int).Value = (object?)(m.M_P_IDX is { } parent && parent != 0 ? parent : null) ?? DBNull.Value;
        command.Parameters.Add("@SORT_IDX", SqlDbType.Int).Value = m.SORT_IDX;
        command.Parameters.Add("@M_TAG", SqlDbType.Bit).Value = m.M_TAG;
        command.Parameters.Add("@M_ROOT_IDX", SqlDbType.Int).Value = rootIdx == 0 ? m.M_IDX : rootIdx;
        command.Parameters.Add("@AUTO_APPROVE", SqlDbType.Bit).Value = m.AUTO_APPROVE;
        command.Parameters.Add("@IF_COPY", SqlDbType.Bit).Value = m.IF_COPY;
        command.Parameters.Add("@ERROR_NO_SAVE", SqlDbType.Bit).Value = m.ERROR_NO_SAVE;
        command.Parameters.AddWithValue("@SORT_FIELDS", (object?)m.SORT_FIELDS ?? DBNull.Value);
        command.Parameters.AddWithValue("@MASTER_TABLE", (object?)m.MASTER_TABLE ?? DBNull.Value);
        command.Parameters.AddWithValue("@FILTER", (object?)m.FILTER ?? DBNull.Value);
        command.Parameters.AddWithValue("@DETAIL_TABLE", (object?)m.DETAIL_TABLE ?? DBNull.Value);
        command.Parameters.AddWithValue("@NOT_BACK_FIELDS_M", (object?)m.NOT_BACK_FIELDS_M ?? DBNull.Value);
        command.Parameters.AddWithValue("@NOT_BACK_FIELDS", (object?)m.NOT_BACK_FIELDS ?? DBNull.Value);
        command.Parameters.AddWithValue("@LAST_UPDATE_BY", updatedBy);
        command.Parameters.AddWithValue("@M_ICON", (object?)m.M_ICON ?? DBNull.Value);
        command.Parameters.Add("@EFFECT_ENGINE_TAG", SqlDbType.Bit).Value = m.EffectEngineTag;
        // 模块备注：写"这个模块是干什么的"（2301「基础」页签可编辑）。空串归一成 NULL，
        // 免得列表/详情在两处显示口径上出现"空串 vs NULL"的假差异。
        command.Parameters.AddWithValue("@REMARK", string.IsNullOrWhiteSpace(m.REMARK) ? DBNull.Value : m.REMARK.Trim());
        return command;
    }

    /// <summary>
    /// 模块编号变更的引用级联：把指向旧编号的列改指新编号（本节点 / 子节点 / 根节点、
    /// 个人与组权限、模块分组、报表与查询、字段与选择器、流程定义与流转、单据性质、待办）。
    /// 语句与顺序对照原 `P_Change_M_IDX` 过程本体；**唯一差异**是选择器数据源那张表：
    /// 原过程写的是已被 取代的 `FIELDS_CHOOSER`（该表在库内已不存在，原过程本体因此
    /// 整条跑不通），这里改用现表 `FIELD_DATASOURCE` 的同名列 `SOURCE_M_IDX`。
    /// 全部参数化、无动态标识符。
    ///
    /// 报表归属列 `REPORT.M_IDX` **不在这里列**：它由外键 `FK_REPORT_MODULE` 的
    /// `ON UPDATE CASCADE` 随本语句首行（改 `MODULES.M_IDX`）自动改指——外键若不带来级联，
    /// 首行本身就会被引用完整性拦下。
    /// </summary>
    // 与旧过程 P_Change_M_IDX 逐条对应（差异逐条具名在 MenuAdminModuleIdCascadeLiveTests）：
    // TASK 已随零引用死表退役（迁移 322），级联里不再有它。
    internal const string ChangeModuleIndexSql = """
        UPDATE dbo.MODULES SET M_IDX=@NEW_IDX WHERE M_IDX=@OLD_IDX;
        UPDATE dbo.MODULES SET M_P_IDX=@NEW_IDX WHERE M_P_IDX=@OLD_IDX;
        UPDATE dbo.MODULES SET M_ROOT_IDX=@NEW_IDX WHERE M_ROOT_IDX=@OLD_IDX;
        UPDATE dbo.SYSDD SET M_IDX=@NEW_IDX WHERE M_IDX=@OLD_IDX;
        UPDATE dbo.REPORT_USER_STATE SET M_IDX=@NEW_IDX WHERE M_IDX=@OLD_IDX;
        UPDATE dbo.SYSDH SET M_IDX=@NEW_IDX WHERE M_IDX=@OLD_IDX;
        UPDATE dbo.SYSQR SET R_M_IDX=@NEW_IDX WHERE R_M_IDX=@OLD_IDX;
        UPDATE dbo.FIELDS SET BROWSE_M_IDX=@NEW_IDX WHERE BROWSE_M_IDX=@OLD_IDX;
        UPDATE dbo.FIELD_DATASOURCE SET SOURCE_M_IDX=@NEW_IDX WHERE SOURCE_M_IDX=@OLD_IDX;
        UPDATE dbo.WFFORM SET WF_M_IDX=@NEW_IDX WHERE WF_M_IDX=@OLD_IDX;
        UPDATE dbo.WFFORM_FLOW SET WF_M_IDX=@NEW_IDX WHERE WF_M_IDX=@OLD_IDX;
        UPDATE dbo.WF_MONITOR SET WF_M_IDX=@NEW_IDX WHERE WF_M_IDX=@OLD_IDX;
        UPDATE dbo.BILLKIND SET B_M_IDX=@NEW_IDX WHERE B_M_IDX=@OLD_IDX;
        UPDATE dbo.MODULE_GROUPS SET M_IDX=@NEW_IDX WHERE M_IDX=@OLD_IDX;
        """;

    internal static async Task ChangeModuleIdAsync(SqlConnection connection, SqlTransaction transaction, int oldId, int newId, CancellationToken token)
    {
        // 级联语句固定表/列名，参数化传值；新旧编号同批执行，顺序与旧过程一致。
        await using var command = new SqlCommand(ChangeModuleIndexSql, connection, transaction);
        command.Parameters.Add("@OLD_IDX", SqlDbType.Int).Value = oldId;
        command.Parameters.Add("@NEW_IDX", SqlDbType.Int).Value = newId;
        await command.ExecuteNonQueryAsync(token);
    }

}
