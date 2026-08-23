using System.Data;
using EOS.API.Models;
using Microsoft.Data.SqlClient;
using System.Text.RegularExpressions;

namespace EOS.API.Data;

/// <summary>
/// 菜单管理（模块 2301）：MODULES 增删改查。
/// 语义对齐旧 xp_menu_manage / P_SYS_DelTreeNode / P_Change_M_IDX：
/// - 保存：事务内更新/插入，编号变更时级联子级 M_P_IDX、权限与引用表；
/// - M_ROOT_IDX 按父链根重算并同步到子树；
/// - 删除：有下级菜单或主/副表已产生数据时拒绝（新保护），叶子且无数据时删除并清理权限引用。
/// - 排序：同级（M_P_IDX 相同）节点按 SORT_IDX,M_IDX 排序后重写 SORT_IDX（10 步进），
///   对齐旧系统「排序号」字段驱动的菜单顺序。
/// 全部参数化，动态标识符仅来自服务端校验。
/// </summary>
public sealed class MenuAdminRepository(
    DbConnectionFactory connections,
    WorkbenchDirtyMarker dirtyMarker,
    WorkbenchAuditWriter auditWriter,
    ILogger<MenuAdminRepository> logger)
{
    private static readonly Regex Identifier = new("^[A-Za-z_][A-Za-z0-9_]{0,127}$", RegexOptions.Compiled);

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
        var sql = """
            SELECT M_IDX,M_ALIAS,M_DESC,M_URL,NEW_URL,MODI_URL,HELP_URL,DETAIL_NO_FIELDS,DETAIL_NO_SAVE,
                   SEARCH_1,SEARCH_2,M_P_IDX,SORT_IDX,M_TAG,AUTO_APPROVE,IF_COPY,ERROR_NO_SAVE,SORT_FIELDS,
                   MASTER_TABLE,FILTER,DETAIL_TABLE,UPDATE_SP,AFTERSAVE_SP,NOT_BACK_FIELDS_M,NOT_BACK_FIELDS,
                   GROUP1,GROUP_EXP1,GROUP_DESC1,GROUP2,GROUP_EXP2,GROUP_DESC2,GROUP3,GROUP_EXP3,GROUP_DESC3,
                   GROUP4,GROUP_EXP4,GROUP_DESC4,GROUP5,GROUP_EXP5,GROUP_DESC5,LAST_UPDATE_BY,LAST_UPDATE_DATE,
                   FORM_TABS,FORM_COLUMNS,FORM_BUTTONS,M_ICON
            FROM dbo.MODULES WITH (NOLOCK)
            WHERE (@Keyword = '' OR M_DESC LIKE @Keyword OR M_ALIAS LIKE @Keyword OR CONVERT(nvarchar(20),M_IDX) LIKE @Keyword)
            ORDER BY ISNULL(M_P_IDX,0),SORT_IDX,M_IDX;
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
            if (!Identifier.IsMatch(table)) continue;
            result.Add(new MenuAdminTableInfo(table, reader.GetString(1), NullIfEmpty(reader.GetString(2)), NullIfEmpty(reader.GetString(3))));
        }
        return result;
    }

    /// <summary>菜单管理字段选择器候选：返回表在 FIELDS 中且物理存在的字段。</summary>
    public async Task<IReadOnlyList<MenuAdminFieldInfo>> GetTableFieldsAsync(string table, CancellationToken token)
    {
        if (!Identifier.IsMatch(table))
            throw new ArgumentException($"表名无效：{table}");
        const string sql = """
            SELECT LTRIM(RTRIM(f.F_ID)),
                   COALESCE(NULLIF(LTRIM(RTRIM(f.F_DESC)),''),LTRIM(RTRIM(f.F_ID))),
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
            if (!Identifier.IsMatch(field)) continue;
            result.Add(new MenuAdminFieldInfo(field, reader.GetString(1), reader.GetString(2), reader.GetBoolean(3), reader.GetBoolean(4), reader.GetBoolean(5)));
        }
        return result;
    }

    /// <summary>
    /// 菜单默认查询列（对齐旧 MenuBuilder「默认查询」→ SetQueryDefault）：
    /// 返回主表/副表全部可见物理字段及当前 SYSQL_DEFAULT 勾选顺序。
    /// </summary>
    public async Task<MenuDefaultColumns> GetDefaultColumnsAsync(int moduleId, string tableKind, CancellationToken token)
    {
        var (masterTable, detailTable) = await ResolveModuleTablesAsync(moduleId, token);
        var targetTable = ResolveTargetTable(masterTable, detailTable, tableKind)
            ?? throw new ArgumentException("该模块未配置" + (tableKind == "master" ? "操作主表" : "操作副表") + "。");
        const string sql = """
            SELECT LTRIM(RTRIM(f.F_ID)),COALESCE(NULLIF(LTRIM(RTRIM(f.F_DESC)),''),LTRIM(RTRIM(f.F_ID))),
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
            if (!Identifier.IsMatch(key)) continue;
            fields.Add(new MenuDefaultColumn(key, reader.GetString(1).Trim(), reader.GetBoolean(2), ++order));
        }
        return new MenuDefaultColumns(targetTable, tableKind, fields);
    }

    /// <summary>
    /// 保存菜单默认查询列（SYSQL_DEFAULT，先删后插、事务、参数化）。
    /// 对齐旧 SetQueryDefault 语义：T_ID=模块主表，T_ID_R=目标表。
    /// </summary>
    public async Task SaveDefaultColumnsAsync(int moduleId, SaveMenuDefaultColumns request, CancellationToken token)
    {
        if (request.Table is not ("master" or "detail"))
            throw new ArgumentException("table 仅支持 master 或 detail。");
        if (request.FieldIds.Count > 200 || request.FieldIds.Distinct(StringComparer.OrdinalIgnoreCase).Count() != request.FieldIds.Count)
            throw new ArgumentException("默认查询列配置无效（字段重复或过多）。");
        var (masterTable, detailTable) = await ResolveModuleTablesAsync(moduleId, token);
        if (masterTable is null) throw new ArgumentException("该模块未配置操作主表。");
        var targetTable = ResolveTargetTable(masterTable, detailTable, request.Table)
            ?? throw new ArgumentException("该模块未配置操作" + (request.Table == "master" ? "主表" : "副表") + "。");
        var allowed = await ReadVisibleFieldKeysAsync(targetTable, token);
        if (request.FieldIds.Any(field => !allowed.Contains(field)))
            throw new ArgumentException("默认查询列包含无效字段。");

        await using var connection = connections.Create();
        await connection.OpenAsync(token);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(token);
        try
        {
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
            await dirtyMarker.MarkDirtyAsync(connection, transaction, moduleId, "SYSTEM", token);
            await transaction.CommitAsync(token);
        }
        catch
        {
            await transaction.RollbackAsync(token);
            throw;
        }
        await auditWriter.WriteBestEffortAsync(moduleId, targetTable, "SAVE", "保存默认查询列", "SYSTEM", "MENU", result: 1, null, token);
        logger.LogInformation("保存默认查询列 module={ModuleId} kind={Kind} table={Table} fields={FieldCount}",
            moduleId, request.Table, targetTable, request.FieldIds.Count);
    }

    public async Task<MenuAdminModule?> GetModuleAsync(int id, CancellationToken token)
    {
        var sql = """
            SELECT M_IDX,M_ALIAS,M_DESC,M_URL,NEW_URL,MODI_URL,HELP_URL,DETAIL_NO_FIELDS,DETAIL_NO_SAVE,
                   SEARCH_1,SEARCH_2,M_P_IDX,SORT_IDX,M_TAG,AUTO_APPROVE,IF_COPY,ERROR_NO_SAVE,SORT_FIELDS,
                   MASTER_TABLE,FILTER,DETAIL_TABLE,UPDATE_SP,AFTERSAVE_SP,NOT_BACK_FIELDS_M,NOT_BACK_FIELDS,
                   GROUP1,GROUP_EXP1,GROUP_DESC1,GROUP2,GROUP_EXP2,GROUP_DESC2,GROUP3,GROUP_EXP3,GROUP_DESC3,
                   GROUP4,GROUP_EXP4,GROUP_DESC4,GROUP5,GROUP_EXP5,GROUP_DESC5,LAST_UPDATE_BY,LAST_UPDATE_DATE,
                   FORM_TABS,FORM_COLUMNS,FORM_BUTTONS,M_ICON
            FROM dbo.MODULES WITH (NOLOCK) WHERE M_IDX=@Id;
            """;
        await using var connection = connections.Create();
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@Id", SqlDbType.Int).Value = id;
        await connection.OpenAsync(token);
        await using var reader = await command.ExecuteReaderAsync(token);
        return await reader.ReadAsync(token) ? ReadModule(reader) : null;
    }

    /// <summary>
    /// 菜单同级排序（top/up/down/bottom）。事务内读取同父兄弟节点，按 SORT_IDX,M_IDX
    /// （即树当前显示顺序）确定目标位，重写整组 SORT_IDX 为 10 步进，保证显示顺序与
    /// 旧系统「ORDER BY SORT_IDX」完全一致（含重复值场景）。边界移动为无操作。
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
                // beforeId 指向自身：同一父级下等价于原位，直接视为无操作
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
    /// （允许把 M_IDX 改成新编号，级联子级 M_P_IDX、权限与引用表，对齐旧 OLD_IDX 语义）。
    /// </summary>
    public async Task<int> SaveAsync(MenuAdminModule input, int? oldId, string updatedBy, CancellationToken token)
    {
        if (oldId is { } updateId && input.M_IDX <= 0)
            throw new ArgumentException("菜单编号必须为正整数。");
        Validate(input);
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
                    // 编号变更级联：子级 M_P_IDX、M_ROOT_IDX、权限与引用表（对齐 P_Change_M_IDX）
                    await ChangeModuleIdAsync(connection, transaction, currentId, input.M_IDX, token);
                    // 子树根编号归一化（对齐 xp_menu_manage 末段）
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
            await dirtyMarker.MarkDirtyAsync(connection, transaction, input.M_IDX, updatedBy, token);
            if (oldId is { } oldModuleId && oldModuleId != input.M_IDX)
            {
                await dirtyMarker.MarkDirtyAsync(connection, transaction, oldModuleId, updatedBy, token);
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

    /// <summary>
    /// 删除菜单节点：有下级菜单、或主/副表已产生业务数据时拒绝（新保护，旧系统为递归删除且无校验）；
    /// 通过保护后删除节点并清理 SYSDD/SYSDH 权限引用（旧系统不清理，属遗留问题）。
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
                "DELETE FROM dbo.SYSDD WHERE M_IDX=@Id; DELETE FROM dbo.SYSDH WHERE M_IDX=@Id;", connection, transaction))
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
        if (!string.IsNullOrWhiteSpace(input.MASTER_TABLE) && !Identifier.IsMatch(input.MASTER_TABLE))
            throw new ArgumentException($"操作主表名无效：{input.MASTER_TABLE}");
        if (!string.IsNullOrWhiteSpace(input.DETAIL_TABLE) && !Identifier.IsMatch(input.DETAIL_TABLE))
            throw new ArgumentException($"操作副表名无效：{input.DETAIL_TABLE}");
        if (!ModuleRouteValidator.IsValidHostUrl(input.M_URL))
            throw new ArgumentException("页面链接不符合现代路由契约：应为承载页（如 /document-workbench，不带编号）、精确路径、直达表单模板或 /legacy/modules/{编号}。");
        if (!ModuleRouteValidator.IsValidActionUrl(input.NEW_URL))
            throw new ArgumentException("新增URL不符合路由契约：应为 /document-workbench/{moduleId}/new 模板或精确现代路径，留空回退统一表单。");
        if (!ModuleRouteValidator.IsValidActionUrl(input.MODI_URL))
            throw new ArgumentException("修改URL不符合路由契约：应为 /document-workbench/{moduleId}/edit 模板或精确现代路径，留空回退统一表单。");
    }

    private static MenuAdminModule ReadModule(SqlDataReader reader) => new(
        reader.GetInt32(0),
        GetString(reader, 1), GetString(reader, 2) ?? string.Empty, GetString(reader, 3), GetString(reader, 4),
        GetString(reader, 5), GetString(reader, 6), GetString(reader, 7),
        reader.GetBoolean(8), reader.GetBoolean(9), reader.GetBoolean(10),
        reader.IsDBNull(11) ? null : reader.GetInt32(11),
        reader.GetInt32(12), reader.GetBoolean(13), reader.GetBoolean(14), reader.GetBoolean(15),
        reader.GetBoolean(16), GetString(reader, 17),
        GetString(reader, 18), GetString(reader, 19), GetString(reader, 20),
        GetString(reader, 21), GetString(reader, 22), GetString(reader, 23), GetString(reader, 24),
        reader.GetBoolean(25), GetString(reader, 26), GetString(reader, 27),
        reader.GetBoolean(28), GetString(reader, 29), GetString(reader, 30),
        reader.GetBoolean(31), GetString(reader, 32), GetString(reader, 33),
        reader.GetBoolean(34), GetString(reader, 35), GetString(reader, 36),
        reader.GetBoolean(37), GetString(reader, 38), GetString(reader, 39),
        GetString(reader, 40),
        reader.IsDBNull(41) ? null : reader.GetDateTime(41),
        GetString(reader, 42),
        reader.IsDBNull(43) ? (int?)null : (int)reader.GetByte(43),
        GetString(reader, 44),
        GetString(reader, 45));

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
            if (Identifier.IsMatch(key)) result.Add(key);
        }
        return result;
    }

    private static string? GetString(SqlDataReader reader, int index) =>
        reader.IsDBNull(index) ? null : reader.GetString(index).Trim();

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
        if (!Identifier.IsMatch(table))
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
             (M_IDX,M_ALIAS,M_DESC,M_URL,NEW_URL,MODI_URL,HELP_URL,DETAIL_NO_FIELDS,DETAIL_NO_SAVE,SEARCH_1,SEARCH_2,
              M_P_IDX,SORT_IDX,M_TAG,M_ROOT_IDX,AUTO_APPROVE,IF_COPY,ERROR_NO_SAVE,SORT_FIELDS,
              MASTER_TABLE,FILTER,DETAIL_TABLE,UPDATE_SP,AFTERSAVE_SP,NOT_BACK_FIELDS_M,NOT_BACK_FIELDS,
              GROUP1,GROUP_EXP1,GROUP_DESC1,GROUP2,GROUP_EXP2,GROUP_DESC2,GROUP3,GROUP_EXP3,GROUP_DESC3,
              GROUP4,GROUP_EXP4,GROUP_DESC4,GROUP5,GROUP_EXP5,GROUP_DESC5,LAST_UPDATE_BY,LAST_UPDATE_DATE,
              FORM_TABS,FORM_COLUMNS,FORM_BUTTONS,M_ICON)
             VALUES
              (@M_IDX,@M_ALIAS,@M_DESC,@M_URL,@NEW_URL,@MODI_URL,@HELP_URL,@DETAIL_NO_FIELDS,@DETAIL_NO_SAVE,@SEARCH_1,@SEARCH_2,
              @M_P_IDX,@SORT_IDX,@M_TAG,@M_ROOT_IDX,@AUTO_APPROVE,@IF_COPY,@ERROR_NO_SAVE,@SORT_FIELDS,
              @MASTER_TABLE,@FILTER,@DETAIL_TABLE,@UPDATE_SP,@AFTERSAVE_SP,@NOT_BACK_FIELDS_M,@NOT_BACK_FIELDS,
              @GROUP1,@GROUP_EXP1,@GROUP_DESC1,@GROUP2,@GROUP_EXP2,@GROUP_DESC2,@GROUP3,@GROUP_EXP3,@GROUP_DESC3,
              @GROUP4,@GROUP_EXP4,@GROUP_DESC4,@GROUP5,@GROUP_EXP5,@GROUP_DESC5,@LAST_UPDATE_BY,GETDATE(),
              @FORM_TABS,@FORM_COLUMNS,@FORM_BUTTONS,@M_ICON);
            """;
        await using var command = BuildCommand(connection, transaction, sql, m, rootIdx, updatedBy);
        await command.ExecuteNonQueryAsync(token);
    }

    private static async Task UpdateAsync(SqlConnection connection, SqlTransaction transaction, int oldId, MenuAdminModule m, int rootIdx, string updatedBy, CancellationToken token)
    {
        const string sql = """
            UPDATE dbo.MODULES SET
              M_IDX=@M_IDX,M_ALIAS=@M_ALIAS,M_DESC=@M_DESC,M_URL=@M_URL,NEW_URL=@NEW_URL,MODI_URL=@MODI_URL,HELP_URL=@HELP_URL,
              DETAIL_NO_FIELDS=@DETAIL_NO_FIELDS,DETAIL_NO_SAVE=@DETAIL_NO_SAVE,SEARCH_1=@SEARCH_1,SEARCH_2=@SEARCH_2,
              M_P_IDX=@M_P_IDX,SORT_IDX=@SORT_IDX,M_TAG=@M_TAG,M_ROOT_IDX=@M_ROOT_IDX,AUTO_APPROVE=@AUTO_APPROVE,
              IF_COPY=@IF_COPY,ERROR_NO_SAVE=@ERROR_NO_SAVE,SORT_FIELDS=@SORT_FIELDS,
              MASTER_TABLE=@MASTER_TABLE,FILTER=@FILTER,DETAIL_TABLE=@DETAIL_TABLE,UPDATE_SP=@UPDATE_SP,
              AFTERSAVE_SP=@AFTERSAVE_SP,NOT_BACK_FIELDS_M=@NOT_BACK_FIELDS_M,NOT_BACK_FIELDS=@NOT_BACK_FIELDS,
              GROUP1=@GROUP1,GROUP_EXP1=@GROUP_EXP1,GROUP_DESC1=@GROUP_DESC1,
              GROUP2=@GROUP2,GROUP_EXP2=@GROUP_EXP2,GROUP_DESC2=@GROUP_DESC2,
              GROUP3=@GROUP3,GROUP_EXP3=@GROUP_EXP3,GROUP_DESC3=@GROUP_DESC3,
              GROUP4=@GROUP4,GROUP_EXP4=@GROUP_EXP4,GROUP_DESC4=@GROUP_DESC4,
              GROUP5=@GROUP5,GROUP_EXP5=@GROUP_EXP5,GROUP_DESC5=@GROUP_DESC5,
              LAST_UPDATE_BY=@LAST_UPDATE_BY,LAST_UPDATE_DATE=GETDATE(),
              FORM_TABS=@FORM_TABS,FORM_COLUMNS=@FORM_COLUMNS,FORM_BUTTONS=@FORM_BUTTONS,M_ICON=@M_ICON
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
        command.Parameters.AddWithValue("@NEW_URL", (object?)m.NEW_URL ?? DBNull.Value);
        command.Parameters.AddWithValue("@MODI_URL", (object?)m.MODI_URL ?? DBNull.Value);
        command.Parameters.AddWithValue("@HELP_URL", (object?)m.HELP_URL ?? DBNull.Value);
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
        command.Parameters.AddWithValue("@UPDATE_SP", (object?)m.UPDATE_SP ?? DBNull.Value);
        command.Parameters.AddWithValue("@AFTERSAVE_SP", (object?)m.AFTERSAVE_SP ?? DBNull.Value);
        command.Parameters.AddWithValue("@NOT_BACK_FIELDS_M", (object?)m.NOT_BACK_FIELDS_M ?? DBNull.Value);
        command.Parameters.AddWithValue("@NOT_BACK_FIELDS", (object?)m.NOT_BACK_FIELDS ?? DBNull.Value);
        for (var i = 0; i < 5; i++)
        {
            var enabled = i switch { 0 => m.GROUP1, 1 => m.GROUP2, 2 => m.GROUP3, 3 => m.GROUP4, _ => m.GROUP5 };
            var expression = i switch { 0 => m.GROUP_EXP1, 1 => m.GROUP_EXP2, 2 => m.GROUP_EXP3, 3 => m.GROUP_EXP4, _ => m.GROUP_EXP5 };
            var description = i switch { 0 => m.GROUP_DESC1, 1 => m.GROUP_DESC2, 2 => m.GROUP_DESC3, 3 => m.GROUP_DESC4, _ => m.GROUP_DESC5 };
            command.Parameters.Add($"@GROUP{i + 1}", SqlDbType.Bit).Value = enabled;
            command.Parameters.AddWithValue($"@GROUP_EXP{i + 1}", (object?)expression ?? DBNull.Value);
            command.Parameters.AddWithValue($"@GROUP_DESC{i + 1}", (object?)description ?? DBNull.Value);
        }
        command.Parameters.AddWithValue("@LAST_UPDATE_BY", updatedBy);
        command.Parameters.AddWithValue("@FORM_TABS", (object?)m.FORM_TABS ?? DBNull.Value);
        command.Parameters.Add("@FORM_COLUMNS", SqlDbType.TinyInt).Value = m.FORM_COLUMNS is { } columns ? (byte)Math.Clamp(columns, 1, 6) : (object)DBNull.Value;
        command.Parameters.AddWithValue("@FORM_BUTTONS", (object?)m.FORM_BUTTONS ?? DBNull.Value);
        command.Parameters.AddWithValue("@M_ICON", (object?)m.M_ICON ?? DBNull.Value);
        return command;
    }

    private static async Task ChangeModuleIdAsync(SqlConnection connection, SqlTransaction transaction, int oldId, int newId, CancellationToken token)
    {
        // 受控存储过程：固定表/列名级联（对齐旧 P_Change_M_IDX），无动态 SQL。
        await using var command = new SqlCommand("EXEC dbo.P_Change_M_IDX @OLD_IDX, @NEW_IDX;", connection, transaction);
        command.Parameters.Add("@OLD_IDX", SqlDbType.Int).Value = oldId;
        command.Parameters.Add("@NEW_IDX", SqlDbType.Int).Value = newId;
        await command.ExecuteNonQueryAsync(token);
    }

}
