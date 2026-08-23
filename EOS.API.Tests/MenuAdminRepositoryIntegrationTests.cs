using System.Data;
using System.Text.RegularExpressions;
using EOS.API.Data;
using EOS.API.Models;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// 菜单同级排序集成测试：直连 EOS.ERP 开发库验证 ReorderAsync 的 SQL 事务
/// （top/up/down/bottom、边界无操作、非法动作、SORT_IDX 重写与审计字段）。
/// 连接串来自 env EOS_ERP_TEST_CONNECTION 或本机 Codex 配置；拿不到连接串时跳过。
/// 测试使用独立临时 M_IDX 区段（99xxxxxxx），不触碰真实菜单，Dispose 清理。
/// </summary>
[Trait("Category", "Integration")]
public sealed class MenuAdminRepositoryIntegrationTests : IDisposable
{
    private static readonly Lazy<string?> ConnectionString = new(ResolveConnectionString);

    private readonly MenuAdminRepository _repository;
    private readonly int _parentId;
    private readonly int[] _childIds;
    private readonly List<int> _createdIds = [];
    private int _extraCount;

    public MenuAdminRepositoryIntegrationTests()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:ErpDatabase"] = ConnectionString.Value,
            })
            .Build();
        _repository = new MenuAdminRepository(new DbConnectionFactory(config), new WorkbenchDirtyMarker(new DbConnectionFactory(config)),
            new WorkbenchAuditWriter(new DbConnectionFactory(config), new Microsoft.AspNetCore.Http.HttpContextAccessor()),
            NullLogger<MenuAdminRepository>.Instance);
        var baseId = 990000000 + Random.Shared.Next(0, 9999999);
        _parentId = baseId;
        _childIds = [baseId + 1, baseId + 2, baseId + 3, baseId + 4];
    }

    [Fact]
    public async Task Reorder_MovesWithinSiblingsAndRewritesSortIdx()
    {
        if (ConnectionString.Value is null)
        {
            return;
        }

        await SeedAsync();

        // 初始 SORT_IDX：c2=10, c4=20, c3=30, c1=40 → 显示顺序 c2,c4,c3,c1
        var (ids, sortIds) = await QueryOrderAsync();
        Assert.Equal([_childIds[1], _childIds[3], _childIds[2], _childIds[0]], ids);
        Assert.Equal([10, 20, 30, 40], sortIds);

        // 上一：c4（第 2）→ 第 1
        await _repository.ReorderAsync(_childIds[3], "up", "IT", CancellationToken.None);
        (ids, sortIds) = await QueryOrderAsync();
        Assert.Equal([_childIds[3], _childIds[1], _childIds[2], _childIds[0]], ids);
        Assert.Equal([10, 20, 30, 40], sortIds);

        // 下一：c4（第 1）→ 第 2
        await _repository.ReorderAsync(_childIds[3], "down", "IT", CancellationToken.None);
        (ids, sortIds) = await QueryOrderAsync();
        Assert.Equal([_childIds[1], _childIds[3], _childIds[2], _childIds[0]], ids);

        // 最高：c1（最后）→ 第 1
        await _repository.ReorderAsync(_childIds[0], "top", "IT", CancellationToken.None);
        (ids, sortIds) = await QueryOrderAsync();
        Assert.Equal([_childIds[0], _childIds[1], _childIds[3], _childIds[2]], ids);
        Assert.Equal([10, 20, 30, 40], sortIds);

        // 最低：c1（第 1）→ 最后
        await _repository.ReorderAsync(_childIds[0], "bottom", "IT", CancellationToken.None);
        (ids, sortIds) = await QueryOrderAsync();
        Assert.Equal([_childIds[1], _childIds[3], _childIds[2], _childIds[0]], ids);
        Assert.Equal([10, 20, 30, 40], sortIds);

        // 边界无操作：首节点 up、末节点 down 均不改变顺序
        await _repository.ReorderAsync(_childIds[1], "up", "IT", CancellationToken.None);
        await _repository.ReorderAsync(_childIds[0], "down", "IT", CancellationToken.None);
        (ids, sortIds) = await QueryOrderAsync();
        Assert.Equal([_childIds[1], _childIds[3], _childIds[2], _childIds[0]], ids);

        // 审计字段：重排后 LAST_UPDATE_BY 落库
        await using (var connection = new SqlConnection(ConnectionString.Value))
        {
            await connection.OpenAsync();
            await using var command = new SqlCommand(
                "SELECT COUNT(*) FROM dbo.MODULES WHERE M_IDX=@Id AND LAST_UPDATE_BY='IT';", connection);
            command.Parameters.Add("@Id", SqlDbType.Int).Value = _childIds[3];
            Assert.Equal(1, (int)(await command.ExecuteScalarAsync() ?? 0));
        }
    }

    [Fact]
    public async Task Reorder_RejectsInvalidActionAndMissingNode()
    {
        if (ConnectionString.Value is null)
        {
            return;
        }

        await SeedAsync();
        var before = await QueryOrderAsync();

        await Assert.ThrowsAsync<ArgumentException>(
            () => _repository.ReorderAsync(_childIds[0], "left", "IT", CancellationToken.None));
        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => _repository.ReorderAsync(_parentId + 5000, "top", "IT", CancellationToken.None));

        var after = await QueryOrderAsync();
        Assert.Equal(before.Ids, after.Ids);
        Assert.Equal(before.SortIds, after.SortIds);
    }

    [Fact]
    public async Task Move_ReordersWithinSameLevelByBeforeId()
    {
        if (ConnectionString.Value is null)
        {
            return;
        }

        await SeedAsync();

        // 初始顺序 c2,c4,c3,c1（SORT 10,20,30,40）
        // c4 移到 c1 之前 → c2,c3,c4,c1
        await _repository.MoveAsync(_childIds[3], _parentId, _childIds[0], "IT", CancellationToken.None);
        var (ids, sortIds) = await QueryOrderAsync();
        Assert.Equal([_childIds[1], _childIds[2], _childIds[3], _childIds[0]], ids);
        Assert.Equal([10, 20, 30, 40], sortIds);

        // c4 移到同级末尾（c1 之后，beforeId=null）→ c2,c3,c1,c4
        await _repository.MoveAsync(_childIds[3], _parentId, null, "IT", CancellationToken.None);
        (ids, sortIds) = await QueryOrderAsync();
        Assert.Equal([_childIds[1], _childIds[2], _childIds[0], _childIds[3]], ids);
        Assert.Equal([10, 20, 30, 40], sortIds);

        // 原位置移动（c2 移到 c3 之前，本身已在 c3 之前）→ 无操作
        await _repository.MoveAsync(_childIds[1], _parentId, _childIds[2], "IT", CancellationToken.None);
        (ids, sortIds) = await QueryOrderAsync();
        Assert.Equal([_childIds[1], _childIds[2], _childIds[0], _childIds[3]], ids);
    }

    [Fact]
    public async Task Move_ChangesParentAndRenumbersBothGroups()
    {
        if (ConnectionString.Value is null)
        {
            return;
        }

        await SeedAsync();
        var otherRoot = await InsertModuleAsync(null, null, 10, "移动目标根");
        var otherChild = await InsertModuleAsync(otherRoot, otherRoot, 10, "移动目标根子节点");

        // c2 从 P 移入 Q 末尾
        await _repository.MoveAsync(_childIds[1], otherRoot, null, "IT", CancellationToken.None);

        var (pIds, pSorts) = await QueryOrderAsync();
        Assert.Equal([_childIds[3], _childIds[2], _childIds[0]], pIds);
        Assert.Equal([10, 20, 30], pSorts);

        var (qIds, qSorts) = await QueryGroupOrderAsync(otherRoot);
        Assert.Equal([otherChild, _childIds[1]], qIds);
        Assert.Equal([10, 20], qSorts);

        await using (var connection = new SqlConnection(ConnectionString.Value))
        {
            await connection.OpenAsync();
            await using var command = new SqlCommand(
                "SELECT M_P_IDX,M_ROOT_IDX,LAST_UPDATE_BY FROM dbo.MODULES WHERE M_IDX=@Id;", connection);
            command.Parameters.Add("@Id", SqlDbType.Int).Value = _childIds[1];
            await using var reader = await command.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal(otherRoot, reader.GetInt32(0));
            Assert.Equal(otherRoot, reader.GetInt32(1));
            Assert.Equal("IT", reader.GetString(2).Trim());
        }
    }

    [Fact]
    public async Task Move_NormalizesSubtreeRoot()
    {
        if (ConnectionString.Value is null)
        {
            return;
        }

        var group = await InsertModuleAsync(_parentId, _parentId, 10, "移动组");
        var leaf = await InsertModuleAsync(group, _parentId, 10, "移动叶子");
        var otherRoot = await InsertModuleAsync(null, null, 10, "新根");

        await _repository.MoveAsync(group, otherRoot, null, "IT", CancellationToken.None);

        await using var connection = new SqlConnection(ConnectionString.Value);
        await connection.OpenAsync();
        await using var command = new SqlCommand(
            "SELECT M_P_IDX,M_ROOT_IDX FROM dbo.MODULES WHERE M_IDX IN (@Group,@Leaf);", connection);
        command.Parameters.Add("@Group", SqlDbType.Int).Value = group;
        command.Parameters.Add("@Leaf", SqlDbType.Int).Value = leaf;
        await using var reader = await command.ExecuteReaderAsync();
        var rows = new List<(int Parent, int Root)>();
        while (await reader.ReadAsync())
            rows.Add((reader.GetInt32(0), reader.GetInt32(1)));
        Assert.Equal(2, rows.Count);
        // 组本身：父=新根，根=新根；叶子：父仍为组，但根已归一为新根
        Assert.Equal((otherRoot, otherRoot), rows.Single(row => row.Root == otherRoot && row.Parent == otherRoot));
        Assert.Equal((group, otherRoot), rows.Single(row => row.Parent == group));
    }

    [Fact]
    public async Task Move_RejectsSelfDescendantAndInvalidTargets()
    {
        if (ConnectionString.Value is null)
        {
            return;
        }

        await SeedAsync();
        var grandchild = await InsertModuleAsync(_childIds[0], _parentId, 10, "孙节点");
        var missing = _parentId + 900000;

        await Assert.ThrowsAsync<ArgumentException>(
            () => _repository.MoveAsync(_childIds[0], _childIds[0], null, "IT", CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(
            () => _repository.MoveAsync(_parentId, grandchild, null, "IT", CancellationToken.None));
        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => _repository.MoveAsync(_childIds[0], missing, null, "IT", CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(
            () => _repository.MoveAsync(_childIds[0], _parentId, missing, "IT", CancellationToken.None));

        // 失败后数据不变
        var (ids, sortIds) = await QueryOrderAsync();
        Assert.Equal([_childIds[1], _childIds[3], _childIds[2], _childIds[0]], ids);
        Assert.Equal([10, 20, 30, 40], sortIds);
    }

    [Fact]
    public async Task Move_ReordersRootLevelWithoutDoubleRenumber()
    {
        if (ConnectionString.Value is null)
        {
            return;
        }

        // 快照真实根节点排序，测试结束后恢复（根级移动会重排整个根组）
        var (rootIds, rootSorts) = await QueryGroupOrderAsync(0);
        var a = await InsertModuleAsync(null, null, 10, "根级A");
        var b = await InsertModuleAsync(null, null, 20, "根级B");
        var c = await InsertModuleAsync(null, null, 30, "根级C");

        try
        {
            // C 移到 A 之前（根级排序；此前 oldParent=null 与 newParent=0 比较导致双重重排）
            await _repository.MoveAsync(c, null, a, "IT", CancellationToken.None);

            var (ids, sorts) = await QueryGroupOrderAsync(0);
            var tempPositions = ids
                .Select((id, index) => (Id: id, Index: index))
                .Where(item => item.Id == a || item.Id == b || item.Id == c)
                .OrderBy(item => item.Index)
                .ToList();

            // 顺序：C 在 A 之前；三个临时根排序号互不相同（无并列 = 未发生双重重排）
            Assert.Equal([c, a, b], tempPositions.Select(item => item.Id));
            var tempSorts = tempPositions.Select(item => sorts[item.Index]).ToArray();
            Assert.Equal(tempSorts.Length, tempSorts.Distinct().Count());

            // 仍为根级（父与根不变）
            await using (var connection = new SqlConnection(ConnectionString.Value))
            {
                await connection.OpenAsync();
                await using var command = new SqlCommand(
                    "SELECT M_P_IDX, M_ROOT_IDX FROM dbo.MODULES WHERE M_IDX=@Id;", connection);
                command.Parameters.Add("@Id", SqlDbType.Int).Value = c;
                await using var reader = await command.ExecuteReaderAsync();
                Assert.True(await reader.ReadAsync());
                Assert.True(reader.IsDBNull(0));
                Assert.Equal(c, reader.GetInt32(1));
            }
        }
        finally
        {
            await using var connection = new SqlConnection(ConnectionString.Value);
            await connection.OpenAsync();
            await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync();
            // 恢复真实根节点排序
            for (var i = 0; i < rootIds.Length; i++)
            {
                await using var update = new SqlCommand(
                    "UPDATE dbo.MODULES SET SORT_IDX=@Sort WHERE M_IDX=@Id;", connection, transaction);
                update.Parameters.Add("@Sort", SqlDbType.Int).Value = rootSorts[i];
                update.Parameters.Add("@Id", SqlDbType.Int).Value = rootIds[i];
                await update.ExecuteNonQueryAsync();
            }
            // 清理临时根（Dispose 不再重复删除）
            _createdIds.RemoveAll(id => id == a || id == b || id == c);
            await using var delete = new SqlCommand(
                $"DELETE FROM dbo.MODULES WHERE M_IDX IN ({a},{b},{c});", connection, transaction);
            await delete.ExecuteNonQueryAsync();
            await transaction.CommitAsync();
        }
    }

    [Fact]
    public async Task Create_AutoGeneratesIdAndSort()
    {
        if (ConnectionString.Value is null)
        {
            return;
        }

        var input = new MenuAdminModule(
            M_IDX: 0, M_ALIAS: null, M_DESC: "自动编号测试", M_URL: null, NEW_URL: null, MODI_URL: null, HELP_URL: null,
            DETAIL_NO_FIELDS: null, DETAIL_NO_SAVE: false, SEARCH_1: false, SEARCH_2: false, M_P_IDX: null,
            SORT_IDX: 0, M_TAG: true, AUTO_APPROVE: false, IF_COPY: false, ERROR_NO_SAVE: false, SORT_FIELDS: null,
            MASTER_TABLE: null, FILTER: null, DETAIL_TABLE: null, UPDATE_SP: null, AFTERSAVE_SP: null,
            NOT_BACK_FIELDS_M: null, NOT_BACK_FIELDS: null,
            GROUP1: false, GROUP_EXP1: null, GROUP_DESC1: null,
            GROUP2: false, GROUP_EXP2: null, GROUP_DESC2: null,
            GROUP3: false, GROUP_EXP3: null, GROUP_DESC3: null,
            GROUP4: false, GROUP_EXP4: null, GROUP_DESC4: null,
            GROUP5: false, GROUP_EXP5: null, GROUP_DESC5: null,
            LAST_UPDATE_BY: null, LAST_UPDATE_DATE: null, FORM_TABS: null, FORM_COLUMNS: null, FORM_BUTTONS: null,
            M_ICON: "product");

        var id = await _repository.SaveAsync(input, null, "IT", CancellationToken.None);
        try
        {
            Assert.True(id > 0);
            var created = await _repository.GetModuleAsync(id, CancellationToken.None);
            Assert.NotNull(created);
            Assert.Equal("自动编号测试", created!.M_DESC);
            Assert.True(created.SORT_IDX > 0, "自动生成排序号应大于 0");
            Assert.Equal("product", created.M_ICON);
            Assert.Null(created.M_P_IDX);
        }
        finally
        {
            _createdIds.Remove(id);
            await _repository.DeleteAsync(id, CancellationToken.None);
        }
    }

    [Fact]
    public async Task Save_RejectsUnknownTable()
    {
        if (ConnectionString.Value is null)
        {
            return;
        }

        var input = new MenuAdminModule(
            M_IDX: 0, M_ALIAS: null, M_DESC: "表校验测试", M_URL: null, NEW_URL: null, MODI_URL: null, HELP_URL: null,
            DETAIL_NO_FIELDS: null, DETAIL_NO_SAVE: false, SEARCH_1: false, SEARCH_2: false, M_P_IDX: null,
            SORT_IDX: 0, M_TAG: true, AUTO_APPROVE: false, IF_COPY: false, ERROR_NO_SAVE: false, SORT_FIELDS: null,
            MASTER_TABLE: "NOT_A_TABLE", FILTER: null, DETAIL_TABLE: null, UPDATE_SP: null, AFTERSAVE_SP: null,
            NOT_BACK_FIELDS_M: null, NOT_BACK_FIELDS: null,
            GROUP1: false, GROUP_EXP1: null, GROUP_DESC1: null,
            GROUP2: false, GROUP_EXP2: null, GROUP_DESC2: null,
            GROUP3: false, GROUP_EXP3: null, GROUP_DESC3: null,
            GROUP4: false, GROUP_EXP4: null, GROUP_DESC4: null,
            GROUP5: false, GROUP_EXP5: null, GROUP_DESC5: null,
            LAST_UPDATE_BY: null, LAST_UPDATE_DATE: null);

        await Assert.ThrowsAsync<ArgumentException>(
            () => _repository.SaveAsync(input, null, "IT", CancellationToken.None));
    }

    [Fact]
    public async Task Delete_BlocksWhenChildrenExist()
    {
        if (ConnectionString.Value is null)
        {
            return;
        }

        await SeedAsync();
        await Assert.ThrowsAsync<ArgumentException>(
            () => _repository.DeleteAsync(_parentId, CancellationToken.None));
    }

    [Fact]
    public async Task Delete_BlocksWhenTableHasData()
    {
        if (ConnectionString.Value is null)
        {
            return;
        }

        var id = await InsertModuleAsync(_parentId, _parentId, 10, "有数据模块");
        await using (var connection = new SqlConnection(ConnectionString.Value))
        {
            await connection.OpenAsync();
            await using var command = new SqlCommand(
                "UPDATE dbo.MODULES SET MASTER_TABLE='COMPANY' WHERE M_IDX=@Id;", connection);
            command.Parameters.Add("@Id", SqlDbType.Int).Value = id;
            await command.ExecuteNonQueryAsync();
        }

        await Assert.ThrowsAsync<ArgumentException>(
            () => _repository.DeleteAsync(id, CancellationToken.None));
    }

    [Fact]
    public async Task Delete_LeafWithoutDataSucceedsAndCleansRights()
    {
        if (ConnectionString.Value is null)
        {
            return;
        }

        var id = await InsertModuleAsync(null, null, 10, "可删叶子");
        await using (var seed = new SqlConnection(ConnectionString.Value))
        {
            await seed.OpenAsync();
            await using var command = new SqlCommand(
                "INSERT INTO dbo.SYSDD (USER_ID, M_IDX) VALUES ('MENU_IT', @Id);", seed);
            command.Parameters.Add("@Id", SqlDbType.Int).Value = id;
            await command.ExecuteNonQueryAsync();
        }

        try
        {
            await _repository.DeleteAsync(id, CancellationToken.None);
            _createdIds.Remove(id);

            await using var check = new SqlConnection(ConnectionString.Value);
            await check.OpenAsync();
            await using var nodeCommand = new SqlCommand(
                "SELECT COUNT(*) FROM dbo.MODULES WHERE M_IDX=@Id;", check);
            nodeCommand.Parameters.Add("@Id", SqlDbType.Int).Value = id;
            Assert.Equal(0, (int)(await nodeCommand.ExecuteScalarAsync() ?? 0));
            await using var rightCommand = new SqlCommand(
                "SELECT COUNT(*) FROM dbo.SYSDD WHERE USER_ID='MENU_IT' AND M_IDX=@Id;", check);
            rightCommand.Parameters.Add("@Id", SqlDbType.Int).Value = id;
            Assert.Equal(0, (int)(await rightCommand.ExecuteScalarAsync() ?? 0));
        }
        finally
        {
            _createdIds.Remove(id);
            await using var cleanup = new SqlConnection(ConnectionString.Value);
            await cleanup.OpenAsync();
            await using var command = new SqlCommand(
                "DELETE FROM dbo.SYSDD WHERE USER_ID='MENU_IT'; DELETE FROM dbo.MODULES WHERE M_IDX=@Id;", cleanup);
            command.Parameters.Add("@Id", SqlDbType.Int).Value = id;
            await command.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    public async Task GetTableFields_ReturnsPhysicalFields()
    {
        if (ConnectionString.Value is null)
        {
            return;
        }

        var fields = await _repository.GetTableFieldsAsync("COMPANY", CancellationToken.None);
        Assert.NotEmpty(fields);
        Assert.Contains(fields, field => field.FieldId.Equals("COMPANY_ID", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(fields, field => !System.Text.RegularExpressions.Regex.IsMatch(field.FieldId, "^[A-Za-z_][A-Za-z0-9_]*$"));
    }

    public void Dispose()
    {
        if (ConnectionString.Value is null || _createdIds.Count == 0)
        {
            return;
        }

        try
        {
            using var connection = new SqlConnection(ConnectionString.Value);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = $"DELETE FROM dbo.MODULES WHERE M_IDX IN ({string.Join(",", _createdIds)});";
            command.ExecuteNonQuery();
        }
        catch
        {
            // 清理失败不影响测试结论；残留仅为开发库测试数据
        }
    }

    private async Task SeedAsync()
    {
        await using var connection = new SqlConnection(ConnectionString.Value);
        await connection.OpenAsync();
        await using var command = new SqlCommand(
            """
            INSERT INTO dbo.MODULES (M_IDX,M_DESC,M_P_IDX,SORT_IDX,M_TAG) VALUES
            (@Parent,N'排序测试根',NULL,0,1),
            (@C1,N'排序测试1',@Parent,40,1),
            (@C2,N'排序测试2',@Parent,10,1),
            (@C3,N'排序测试3',@Parent,30,1),
            (@C4,N'排序测试4',@Parent,20,1);
            """, connection);
        command.Parameters.Add("@Parent", SqlDbType.Int).Value = _parentId;
        command.Parameters.Add("@C1", SqlDbType.Int).Value = _childIds[0];
        command.Parameters.Add("@C2", SqlDbType.Int).Value = _childIds[1];
        command.Parameters.Add("@C3", SqlDbType.Int).Value = _childIds[2];
        command.Parameters.Add("@C4", SqlDbType.Int).Value = _childIds[3];
        await command.ExecuteNonQueryAsync();
        _createdIds.AddRange(_childIds);
        _createdIds.Add(_parentId);
    }

    private async Task<int> InsertModuleAsync(int? parentId, int? rootId, int sortIdx, string desc)
    {
        var id = _parentId + 1000 + _extraCount++;
        var root = rootId ?? id;
        await using var connection = new SqlConnection(ConnectionString.Value);
        await connection.OpenAsync();
        await using var command = new SqlCommand(
            "INSERT INTO dbo.MODULES (M_IDX,M_DESC,M_P_IDX,M_ROOT_IDX,SORT_IDX,M_TAG) VALUES (@Id,@Desc,@Parent,@Root,@Sort,1);",
            connection);
        command.Parameters.Add("@Id", SqlDbType.Int).Value = id;
        command.Parameters.AddWithValue("@Desc", desc);
        command.Parameters.Add("@Parent", SqlDbType.Int).Value = (object?)parentId ?? DBNull.Value;
        command.Parameters.Add("@Root", SqlDbType.Int).Value = root;
        command.Parameters.Add("@Sort", SqlDbType.Int).Value = sortIdx;
        await command.ExecuteNonQueryAsync();
        _createdIds.Add(id);
        return id;
    }

    private async Task<(int[] Ids, int[] SortIds)> QueryOrderAsync()
    {
        await using var connection = new SqlConnection(ConnectionString.Value);
        await connection.OpenAsync();
        await using var command = new SqlCommand(
            """
            SELECT M_IDX,SORT_IDX FROM dbo.MODULES WITH (NOLOCK)
            WHERE ISNULL(M_P_IDX,0)=@Parent
            ORDER BY SORT_IDX,M_IDX;
            """, connection);
        command.Parameters.Add("@Parent", SqlDbType.Int).Value = _parentId;
        await using var reader = await command.ExecuteReaderAsync();
        var ids = new List<int>();
        var sortIds = new List<int>();
        while (await reader.ReadAsync())
        {
            ids.Add(reader.GetInt32(0));
            sortIds.Add(reader.GetInt32(1));
        }
        return (ids.ToArray(), sortIds.ToArray());
    }

    private async Task<(int[] Ids, int[] SortIds)> QueryGroupOrderAsync(int parentId)
    {
        await using var connection = new SqlConnection(ConnectionString.Value);
        await connection.OpenAsync();
        await using var command = new SqlCommand(
            """
            SELECT M_IDX,SORT_IDX FROM dbo.MODULES WITH (NOLOCK)
            WHERE ISNULL(M_P_IDX,0)=@Parent
            ORDER BY SORT_IDX,M_IDX;
            """, connection);
        command.Parameters.Add("@Parent", SqlDbType.Int).Value = parentId;
        await using var reader = await command.ExecuteReaderAsync();
        var ids = new List<int>();
        var sortIds = new List<int>();
        while (await reader.ReadAsync())
        {
            ids.Add(reader.GetInt32(0));
            sortIds.Add(reader.GetInt32(1));
        }
        return (ids.ToArray(), sortIds.ToArray());
    }

    private static string? ResolveConnectionString()
    {
        var env = Environment.GetEnvironmentVariable("EOS_ERP_TEST_CONNECTION");
        if (!string.IsNullOrWhiteSpace(env))
        {
            return env;
        }

        try
        {
            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "config.toml");
            var text = File.ReadAllText(path);
            var match = Regex.Match(text, "\\[mcp_servers\\.mssql-erp\\.env\\][\\s\\S]*?MSSQL_CONNECTION_STRING\\s*=\\s*\"([^\"]+)\"");
            return match.Success && match.Groups[1].Value.Contains("Database=EOS.ERP")
                ? match.Groups[1].Value
                : null;
        }
        catch
        {
            return null;
        }
    }
}
