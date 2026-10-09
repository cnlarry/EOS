using System.Data;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;

namespace EOS.API.Data;

/// <summary>配置面读模型的一行（一个分组）。</summary>
/// <param name="Available">受控编译器能否执行该表达式。**不按调用者权限收敛**：配置面回答的是
/// "这个表达式合法吗"，不是"我能不能按这个字段分组"（后者在读侧由分组项的置灰表达）。</param>
public sealed record ModuleGroupAdminRow(
    int GroupId,
    int SortIdx,
    string Description,
    string Expression,
    bool Available,
    string? Error);

/// <summary>配置面读模型：目标模块 + 它的分组清单。</summary>
public sealed record ModuleGroupAdminModule(
    int ModuleId,
    string ModuleDesc,
    string NodeKind,
    string MasterTable,
    IReadOnlyList<ModuleGroupAdminRow> Groups);

/// <summary>
/// 2315「模块分组」的读写：为**其它**模块维护列表分组（名称 + 表达式 + 顺序）。
///
/// 这个配置面不装配工作台定义、也不走发布流程：分组表达式在组装工作台定义时**实时读取**
/// （<c>WorkbenchDefinition.GroupExpressions</c> 带 `[JsonIgnore]`，从不进快照），保存即生效。
///
/// 写入校验与读侧同源（<see cref="ModuleFieldWhitelist"/> + <see cref="GroupExpressionParser"/>）：
/// 配置面放行的表达式，必须是运行期点得开的那一个。此外它守三条**形态**纪律——
/// 只有统一工作台模块才有分组消费方（迁移 340 清的就是非工作台节点上的这类残留）、
/// 表达式必须来自目标模块主表的物理字段、长度不能超出列宽（超长由库报错会变成 500）。
/// </summary>
public sealed class ModuleGroupAdminRepository(
    DbConnectionFactory connections,
    ILogger<ModuleGroupAdminRepository> logger)
{
    /// <summary>分组名称列宽（`MODULE_GROUPS.GROUP_DESC nvarchar(50)`）。</summary>
    public const int MaxDescriptionLength = 50;

    /// <summary>表达式列宽（`MODULE_GROUPS.GROUP_EXP nvarchar(500)`）。</summary>
    public const int MaxExpressionLength = 500;

    /// <summary>
    /// 单模块分组数量上界。分组数量**不受表结构限制**（这正是独立成表的目的），
    /// 但下拉里塞进几十项就无法使用了，故留一个防滥用上界；调它只需要改这一个常量。
    /// </summary>
    public const int MaxGroupsPerModule = 50;

    private const int SortStep = 10;
    private const string WorkbenchKind = "WORKBENCH";

    /// <summary>字符串字面量（诊断时先剔除，免得把 'YES' 里的 YES 当成列名）。</summary>
    private static readonly Regex StringLiteral = new(@"'[^']*'", RegexOptions.Compiled);

    /// <summary>表达式里"像标识符"的片段，仅用于把编译失败翻译成人话（不参与判定）。</summary>
    private static readonly Regex IdentifierLike = new(@"[A-Za-z_][A-Za-z0-9_.]*", RegexOptions.Compiled);

    /// <summary>受控子集里的关键字与函数名：诊断时不算作"列名"，免得报"未知列 WHEN"。</summary>
    private static readonly HashSet<string> ExpressionKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "case", "when", "then", "else", "end", "as",
        "year", "month", "datepart", "replicate", "cast", "convert",
        "varchar", "nvarchar", "week", "day",
    };

    public async Task<ModuleGroupAdminModule?> GetAsync(int moduleId, CancellationToken token)
    {
        await using var connection = connections.Create();
        await connection.OpenAsync(token);
        return await ReadModuleAsync(connection, null, moduleId, token);
    }

    public async Task<ModuleGroupAdminRow> CreateAsync(
        int moduleId, string? description, string? expression, string updatedBy, CancellationToken token)
    {
        var desc = NormalizeDescription(description, required: true)!;
        var exp = NormalizeExpression(expression, required: true)!;
        await using var connection = connections.Create();
        await connection.OpenAsync(token);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(token);
        var target = await ResolveTargetAsync(connection, transaction, moduleId, token);
        await EnsureExpressionAsync(connection, transaction, target.MasterTable, exp, token);

        int sortIdx;
        await using (var countCommand = new SqlCommand(
            "SELECT COUNT(*),ISNULL(MAX(SORT_IDX),0) FROM dbo.MODULE_GROUPS WHERE M_IDX=@ModuleId;", connection, transaction))
        {
            countCommand.Parameters.Add("@ModuleId", SqlDbType.Int).Value = moduleId;
            await using var reader = await countCommand.ExecuteReaderAsync(token);
            await reader.ReadAsync(token);
            var count = reader.GetInt32(0);
            if (count >= MaxGroupsPerModule)
                throw new ArgumentException($"该模块的分组已达上界 {MaxGroupsPerModule} 组：分组数量不受表结构限制，但下拉里塞进更多就无法使用了。");
            sortIdx = reader.IsDBNull(1) ? SortStep : reader.GetInt32(1) + SortStep;
        }

        int groupId;
        await using (var insert = new SqlCommand(
            """
            INSERT INTO dbo.MODULE_GROUPS (M_IDX,SORT_IDX,GROUP_DESC,GROUP_EXP,CREATE_PERSON,CREATE_DATE,LAST_UPDATE_BY,LAST_UPDATE_DATE)
            OUTPUT INSERTED.GROUP_ID
            VALUES (@ModuleId,@SortIdx,@Desc,@Exp,@UpdatedBy,GETDATE(),@UpdatedBy,GETDATE());
            """, connection, transaction))
        {
            insert.Parameters.Add("@ModuleId", SqlDbType.Int).Value = moduleId;
            insert.Parameters.Add("@SortIdx", SqlDbType.Int).Value = sortIdx;
            insert.Parameters.Add("@Desc", SqlDbType.NVarChar, MaxDescriptionLength).Value = desc;
            insert.Parameters.Add("@Exp", SqlDbType.NVarChar, MaxExpressionLength).Value = exp;
            insert.Parameters.Add("@UpdatedBy", SqlDbType.NVarChar, 100).Value = updatedBy;
            groupId = Convert.ToInt32(await insert.ExecuteScalarAsync(token));
        }

        await transaction.CommitAsync(token);
        logger.LogInformation("模块分组新增 module={ModuleId} group={GroupId} by={UpdatedBy}", moduleId, groupId, updatedBy);
        return new ModuleGroupAdminRow(groupId, sortIdx, desc, exp, true, null);
    }

    public async Task<ModuleGroupAdminRow> UpdateAsync(
        int moduleId, int groupId, string? description, string? expression, string updatedBy, CancellationToken token)
    {
        var desc = NormalizeDescription(description, required: true)!;
        var exp = NormalizeExpression(expression, required: true)!;
        await using var connection = connections.Create();
        await connection.OpenAsync(token);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(token);
        var (owner, sortIdx) = await ReadOwnerAsync(connection, transaction, groupId, token);
        EnsureOwned(moduleId, groupId, owner);
        var target = await ResolveTargetAsync(connection, transaction, moduleId, token);
        await EnsureExpressionAsync(connection, transaction, target.MasterTable, exp, token);

        await using (var update = new SqlCommand(
            """
            UPDATE dbo.MODULE_GROUPS
               SET GROUP_DESC=@Desc,GROUP_EXP=@Exp,LAST_UPDATE_BY=@UpdatedBy,LAST_UPDATE_DATE=GETDATE()
             WHERE GROUP_ID=@GroupId;
            """, connection, transaction))
        {
            update.Parameters.Add("@Desc", SqlDbType.NVarChar, MaxDescriptionLength).Value = desc;
            update.Parameters.Add("@Exp", SqlDbType.NVarChar, MaxExpressionLength).Value = exp;
            update.Parameters.Add("@UpdatedBy", SqlDbType.NVarChar, 100).Value = updatedBy;
            update.Parameters.Add("@GroupId", SqlDbType.Int).Value = groupId;
            await update.ExecuteNonQueryAsync(token);
        }

        await transaction.CommitAsync(token);
        logger.LogInformation("模块分组修改 module={ModuleId} group={GroupId} by={UpdatedBy}", moduleId, groupId, updatedBy);
        return new ModuleGroupAdminRow(groupId, sortIdx, desc, exp, true, null);
    }

    /// <summary>删除一个分组。<paramref name="moduleId"/> 由调用方提供（审计与归属核对用），与行的归属不符即拒绝。</summary>
    public async Task DeleteAsync(int moduleId, int groupId, string updatedBy, CancellationToken token)
    {
        await using var connection = connections.Create();
        await connection.OpenAsync(token);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(token);
        var (owner, _) = await ReadOwnerAsync(connection, transaction, groupId, token);
        EnsureOwned(moduleId, groupId, owner);
        await using (var delete = new SqlCommand("DELETE FROM dbo.MODULE_GROUPS WHERE GROUP_ID=@GroupId;", connection, transaction))
        {
            delete.Parameters.Add("@GroupId", SqlDbType.Int).Value = groupId;
            await delete.ExecuteNonQueryAsync(token);
        }
        await transaction.CommitAsync(token);
        logger.LogInformation("模块分组删除 module={ModuleId} group={GroupId} by={UpdatedBy}", moduleId, groupId, updatedBy);
    }

    /// <summary>
    /// 同一模块内的排序动作（top / up / down / bottom）：整段重写 SORT_IDX（10 的倍数，与菜单排序同款）。
    /// SORT_IDX 只决定下拉顺序，**不是身份**——边界移动是无操作，不报错。
    /// </summary>
    public async Task MoveAsync(int moduleId, int groupId, string action, string updatedBy, CancellationToken token)
    {
        await using var connection = connections.Create();
        await connection.OpenAsync(token);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(token);
        var (owner, _) = await ReadOwnerAsync(connection, transaction, groupId, token);
        EnsureOwned(moduleId, groupId, owner);

        var siblings = new List<int>();
        await using (var siblingCommand = new SqlCommand(
            "SELECT GROUP_ID FROM dbo.MODULE_GROUPS WHERE M_IDX=@ModuleId ORDER BY SORT_IDX,GROUP_ID;", connection, transaction))
        {
            siblingCommand.Parameters.Add("@ModuleId", SqlDbType.Int).Value = moduleId;
            await using var reader = await siblingCommand.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token))
                siblings.Add(reader.GetInt32(0));
        }

        var currentIndex = siblings.IndexOf(groupId);
        if (currentIndex < 0)
            throw new KeyNotFoundException($"分组 {groupId} 不存在。");
        var targetIndex = MenuAdminRepository.TargetIndex(currentIndex, siblings.Count, action);
        if (targetIndex == currentIndex)
        {
            await transaction.CommitAsync(token);
            return;
        }

        siblings.RemoveAt(currentIndex);
        siblings.Insert(targetIndex, groupId);
        for (var position = 0; position < siblings.Count; position++)
        {
            await using var update = new SqlCommand(
                "UPDATE dbo.MODULE_GROUPS SET SORT_IDX=@SortIdx,LAST_UPDATE_BY=@UpdatedBy,LAST_UPDATE_DATE=GETDATE() WHERE GROUP_ID=@GroupId;",
                connection, transaction);
            update.Parameters.Add("@SortIdx", SqlDbType.Int).Value = (position + 1) * SortStep;
            update.Parameters.Add("@UpdatedBy", SqlDbType.NVarChar, 100).Value = updatedBy;
            update.Parameters.Add("@GroupId", SqlDbType.Int).Value = siblings[position];
            await update.ExecuteNonQueryAsync(token);
        }

        await transaction.CommitAsync(token);
        logger.LogInformation("模块分组排序 module={ModuleId} group={GroupId} action={Action} by={UpdatedBy}",
            moduleId, groupId, action, updatedBy);
    }

    /// <summary>
    /// 目标模块的分组可用字段清单（界面上的"可引用列"提示）。与写入校验**同一份白名单**：
    /// 列出来的都是存得下的，免得界面提示与实际拒存规则各说各话。
    /// </summary>
    public async Task<IReadOnlyList<string>> ReadAllowedFieldsAsync(int moduleId, CancellationToken token)
    {
        await using var connection = connections.Create();
        await connection.OpenAsync(token);
        var target = await ResolveTargetAsync(connection, null, moduleId, token);
        var allowed = await ModuleFieldWhitelist.ReadAsync(connection, null, target.MasterTable, null, token);
        return [.. allowed.OrderBy(key => key, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>模块行 + 它的全部分组行（模块不存在返回 null）。</summary>
    private async Task<ModuleGroupAdminModule?> ReadModuleAsync(
        SqlConnection connection, SqlTransaction? transaction, int moduleId, CancellationToken token)
    {
        string moduleDesc, nodeKind, masterTable;
        await using (var moduleCommand = new SqlCommand(
            """
            SELECT LTRIM(RTRIM(ISNULL(m.M_DESC,''))),LTRIM(RTRIM(ISNULL(m.MASTER_TABLE,''))),
                   LTRIM(RTRIM(ISNULL(n.NODE_KIND,'')))
            FROM dbo.MODULES m
            LEFT JOIN dbo.V_MODULE_NODE n ON n.M_IDX=m.M_IDX
            WHERE m.M_IDX=@ModuleId;
            """, connection, transaction))
        {
            moduleCommand.Parameters.Add("@ModuleId", SqlDbType.Int).Value = moduleId;
            await using var reader = await moduleCommand.ExecuteReaderAsync(token);
            if (!await reader.ReadAsync(token)) return null;
            moduleDesc = reader.GetString(0);
            masterTable = reader.GetString(1);
            nodeKind = reader.GetString(2);
        }

        var groups = new List<(int GroupId, int SortIdx, string Description, string Expression)>();
        await using (var groupCommand = new SqlCommand(
            """
            SELECT GROUP_ID,SORT_IDX,GROUP_DESC,GROUP_EXP FROM dbo.MODULE_GROUPS
            WHERE M_IDX=@ModuleId ORDER BY SORT_IDX,GROUP_ID;
            """, connection, transaction))
        {
            groupCommand.Parameters.Add("@ModuleId", SqlDbType.Int).Value = moduleId;
            await using var reader = await groupCommand.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token))
            {
                groups.Add((
                    reader.GetInt32(0),
                    reader.IsDBNull(1) ? 0 : reader.GetInt32(1),
                    reader.IsDBNull(2) ? string.Empty : reader.GetString(2).Trim(),
                    reader.IsDBNull(3) ? string.Empty : reader.GetString(3).Trim()));
            }
        }

        var allowed = string.IsNullOrWhiteSpace(masterTable)
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            : await ModuleFieldWhitelist.ReadAsync(connection, transaction, masterTable, null, token);
        var rows = new List<ModuleGroupAdminRow>(groups.Count);
        foreach (var group in groups)
        {
            var valid = GroupExpressionParser.TryCompile(group.Expression, masterTable, allowed, out _);
            rows.Add(new ModuleGroupAdminRow(
                group.GroupId, group.SortIdx, group.Description, group.Expression,
                valid, valid ? null : DescribeCompileFailure(group.Expression, masterTable, allowed)));
        }
        return new ModuleGroupAdminModule(moduleId, moduleDesc, nodeKind, masterTable, rows);
    }

    /// <summary>分组的归属模块与顺序（不存在即 404）。</summary>
    private static async Task<(int ModuleId, int SortIdx)> ReadOwnerAsync(
        SqlConnection connection, SqlTransaction? transaction, int groupId, CancellationToken token)
    {
        await using var command = new SqlCommand(
            "SELECT M_IDX,ISNULL(SORT_IDX,0) FROM dbo.MODULE_GROUPS WHERE GROUP_ID=@GroupId;", connection, transaction);
        command.Parameters.Add("@GroupId", SqlDbType.Int).Value = groupId;
        await using var reader = await command.ExecuteReaderAsync(token);
        if (!await reader.ReadAsync(token))
            throw new KeyNotFoundException($"分组 {groupId} 不存在。");
        return (reader.GetInt32(0), reader.GetInt32(1));
    }

    /// <summary>路径段里的模块号必须就是这一行的归属模块：不一致即拒绝（否则审计会挂到别的模块上）。</summary>
    private static void EnsureOwned(int moduleId, int groupId, int owner)
    {
        if (owner != moduleId)
            throw new ArgumentException($"分组 {groupId} 不属于模块 {moduleId}（它属于模块 {owner}）。");
    }

    /// <summary>
    /// 目标模块能不能配分组：必须是**统一工作台模块且有主表**。形态判据取视图 `dbo.V_MODULE_NODE`
    /// （与 `ModuleRouteValidator.ResolveKind` 同源，见迁移 340）——只有工作台模块装配工作台定义，
    /// 才有人消费分组；给别的形态配了是无处可用的空转配置。
    /// </summary>
    private static async Task<(string MasterTable, string ModuleDesc)> ResolveTargetAsync(
        SqlConnection connection, SqlTransaction? transaction, int moduleId, CancellationToken token)
    {
        await using var command = new SqlCommand(
            """
            SELECT LTRIM(RTRIM(ISNULL(m.M_DESC,''))),LTRIM(RTRIM(ISNULL(m.MASTER_TABLE,''))),
                   LTRIM(RTRIM(ISNULL(n.NODE_KIND,'')))
            FROM dbo.MODULES m
            LEFT JOIN dbo.V_MODULE_NODE n ON n.M_IDX=m.M_IDX
            WHERE m.M_IDX=@ModuleId;
            """, connection, transaction);
        command.Parameters.Add("@ModuleId", SqlDbType.Int).Value = moduleId;
        await using var reader = await command.ExecuteReaderAsync(token);
        if (!await reader.ReadAsync(token))
            throw new KeyNotFoundException($"模块 {moduleId} 不存在。");
        var moduleDesc = reader.GetString(0);
        var masterTable = reader.GetString(1);
        var nodeKind = reader.GetString(2);
        if (!string.Equals(nodeKind, WorkbenchKind, StringComparison.Ordinal))
            throw new ArgumentException($"「{moduleDesc}」（{moduleId}）不是统一工作台模块（形态：{DescribeKind(nodeKind)}），没有分组消费方，不能配分组。");
        if (masterTable.Length == 0)
            throw new ArgumentException($"「{moduleDesc}」（{moduleId}）没有操作主表，分组表达式没有可分组的数据。");
        return (masterTable, moduleDesc);
    }

    /// <summary>表达式校验：受控编译（同一份白名单与解析器）+ 给出指名到列的失败原因。</summary>
    private static async Task EnsureExpressionAsync(
        SqlConnection connection, SqlTransaction transaction, string masterTable, string expression, CancellationToken token)
    {
        var allowed = await ModuleFieldWhitelist.ReadAsync(connection, transaction, masterTable, null, token);
        if (!GroupExpressionParser.TryCompile(expression, masterTable, allowed, out _))
            throw new ArgumentException(DescribeCompileFailure(expression, masterTable, allowed));
    }

    /// <summary>
    /// 把"超出受控子集"翻译成能照着改的人话：指名第一个不在白名单里的标识符；
    /// 看不出具体原因时回落成子集说明（判定本身始终是 <see cref="GroupExpressionParser"/>）。
    /// </summary>
    private static string DescribeCompileFailure(string expression, string masterTable, IReadOnlySet<string> allowed)
    {
        if (string.IsNullOrWhiteSpace(expression)) return "分组表达式不能为空。";
        if (string.IsNullOrWhiteSpace(masterTable)) return "该模块没有操作主表，分组表达式无法校验。";
        var code = StringLiteral.Replace(expression, "''");
        foreach (Match match in IdentifierLike.Matches(code))
        {
            var token = match.Value;
            var parts = token.Split('.', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length > 2) continue;
            var field = parts[^1];
            if (ExpressionKeywords.Contains(field)) continue;
            if (field.Equals(masterTable, StringComparison.OrdinalIgnoreCase)) continue;
            if (parts.Length == 2 && !parts[0].Equals(masterTable, StringComparison.OrdinalIgnoreCase))
                return $"表达式里的表名 {parts[0]} 与模块主表 {masterTable} 不一致：跨表的列不能用于分组。";
            if (!allowed.Contains(field))
                return $"列 {field} 不在主表 {masterTable} 的分组可用字段里（虚拟字段与未登记字段都不在其中）。";
        }
        return "分组表达式超出受控子集：只支持主表字段、字符串/数字字面量、+ - * /、括号、CASE，"
             + "以及 YEAR / MONTH / DATEPART / REPLICATE / CAST / CONVERT 这几个函数。";
    }

    private static string? NormalizeDescription(string? value, bool required)
    {
        var text = (value ?? string.Empty).Trim();
        if (text.Length == 0)
            return required ? throw new ArgumentException("分组名称不能为空。") : null;
        if (text.Length > MaxDescriptionLength)
            throw new ArgumentException($"分组名称最长 {MaxDescriptionLength} 个字符（当前 {text.Length}）。");
        return text;
    }

    private static string? NormalizeExpression(string? value, bool required)
    {
        var text = (value ?? string.Empty).Trim();
        if (text.Length == 0)
            return required ? throw new ArgumentException("分组表达式不能为空。") : null;
        if (text.Length > MaxExpressionLength)
            throw new ArgumentException($"分组表达式最长 {MaxExpressionLength} 个字符（当前 {text.Length}）。");
        return text;
    }

    private static string DescribeKind(string nodeKind) => nodeKind switch
    {
        "CUSTOMPAGE" => "自定义承载页",
        "DIRECTORY" => "目录节点",
        _ => "未知形态",
    };
}
