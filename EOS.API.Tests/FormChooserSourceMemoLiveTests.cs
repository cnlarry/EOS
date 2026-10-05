using System.Data;
using EOS.API.Data;
using EOS.API.Models;
using Microsoft.Data.SqlClient;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// 选择器来源记忆的真库用例（整个用例在一个事务里跑，结尾整体回滚，不留数据）。
///
/// 回显解析同组伴生字段时，来源由「这张单记录下来的来源」决定。用例用**判别式**来源对做断言：
/// 来源 1 指向一张真表（伴生列存在，能解析出名字），来源 2 指向一张不含该列的表
/// （`ColumnsExistAsync` 不通过，解析直接放弃）——于是"记忆是否被采用"是可以从结果看出来的，
/// 而不是只断言"没报错"。
///
/// 需要 MSSQL_ERP_CONN。
/// </summary>
[Trait("Category", "Integration")]
[Collection("live-database")]
public sealed class FormChooserSourceMemoLiveTests
{
    private static readonly string ConnectionString =
        Environment.GetEnvironmentVariable("MSSQL_ERP_CONN")
        ?? throw new InvalidOperationException(
            "真库集成测试需要 MSSQL_ERP_CONN；未配置即失败（无连接跳过≠已验证）。");

    private const string MasterKey = "[\"MEMO-LIVE-TEST\"]";

    private static FieldChooserSource Source(int serial, string table, string returnMapping) =>
        new(true, table, table, 1000 + serial, Filter: null, ReturnMapping: returnMapping, SerialNo: serial);

    private static FormFieldDefinition Field(
        string key,
        IReadOnlyList<FieldChooserSource> choosers,
        int cellRole,
        bool displayOnly) =>
        new(
            Key: key,
            Label: key,
            DataType: "nvarchar",
            DisplayLength: 100,
            DisplayFormat: null,
            IsRequired: false,
            VerifyIndex: null,
            Regex: null,
            DefaultValue: null,
            IsReadonly: false,
            IsVisible: true,
            OnlyChoose: false,
            ChooseMultiple: false,
            ChoosePage: null,
            Choosers: choosers,
            IsPrimaryKey: false,
            IsAutoIncrement: false,
            IsVirtual: displayOnly,
            IsCost: false,
            IsSecrecy: false,
            ServerFilled: false,
            MaxLength: 100,
            CellGroup: "MEMO_GROUP",
            CellRole: cellRole,
            DisplayOnly: displayOnly);

    /// <summary>可解析来源（真表）与不可解析来源（缺伴生列的表）配成一对。</summary>
    private static IReadOnlyList<FormFieldDefinition> BuildFields(string mainKey, string companionKey) =>
    [
        Field(mainKey,
        [
            Source(1, "HR_EMPLOYEE", $"[{{\"target\":\"{mainKey}\",\"column\":\"EMP_ID\"}},{{\"target\":\"{companionKey}\",\"column\":\"EMP_NAME\"}}]"),
            // BILLKIND 没有 EMP_NAME：命中这一来源时解析必然放弃
            Source(2, "BILLKIND", $"[{{\"target\":\"{mainKey}\",\"column\":\"B_M_IDX\"}},{{\"target\":\"{companionKey}\",\"column\":\"EMP_NAME\"}}]"),
        ], cellRole: 1, displayOnly: false),
        Field(companionKey, [], cellRole: 2, displayOnly: true),
    ];

    private static async Task<(string EmpId, string EmpName)> ReadEmployeeAsync(SqlConnection connection, SqlTransaction transaction)
    {
        await using var command = new SqlCommand(
            "SELECT TOP 1 EMP_ID, EMP_NAME FROM dbo.HR_EMPLOYEE WHERE ISNULL(EMP_NAME,'')<>'' ORDER BY EMP_ID;",
            connection, transaction);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync(), "HR_EMPLOYEE 没有可用样本（EMP_NAME 非空）");
        return (reader.GetString(0), reader.GetString(1));
    }

    [Fact]
    public async Task 记忆写入读取替换删除_真库往返()
    {
        var token = CancellationToken.None;
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync(token);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(token);
        try
        {
            const int moduleId = 990001;
            const string table = "MEMO_ROUNDTRIP_TABLE";
            const string field = "MAIN_FIELD";
            var rowKey = FormChooserSourceMemo.SerializeKey(["K1"]);

            await FormChooserSourceMemo.InsertAsync(connection, transaction, moduleId, table, field, rowKey, MasterKey, 2, "tester", token);
            var read = await FormChooserSourceMemo.ReadAsync(connection, transaction, moduleId, table, MasterKey, token);
            Assert.Equal(2, read[FormChooserSourceMemo.EntryKey(field, rowKey)]);

            // 替换：同一 (表, 字段, 行键) 只保留最近一次选择
            await FormChooserSourceMemo.DeleteEntriesAsync(connection, transaction, moduleId, table, [(field, rowKey)], token);
            await FormChooserSourceMemo.InsertAsync(connection, transaction, moduleId, table, field, rowKey, MasterKey, 1, "tester", token);
            read = await FormChooserSourceMemo.ReadAsync(connection, transaction, moduleId, table, MasterKey, token);
            Assert.Single(read);
            Assert.Equal(1, read[FormChooserSourceMemo.EntryKey(field, rowKey)]);

            // 单据维度删除：另一张表上的记忆同时清掉（明细行记忆不会被落下）
            await FormChooserSourceMemo.InsertAsync(connection, transaction, moduleId, "MEMO_DETAIL_TABLE", field, rowKey, MasterKey, 3, "tester", token);
            await FormChooserSourceMemo.DeleteDocumentAsync(connection, transaction, moduleId, MasterKey, token);
            Assert.Empty(await FormChooserSourceMemo.ReadAsync(connection, transaction, moduleId, table, MasterKey, token));
            Assert.Empty(await FormChooserSourceMemo.ReadAsync(connection, transaction, moduleId, "MEMO_DETAIL_TABLE", MasterKey, token));
        }
        finally
        {
            await transaction.RollbackAsync(token);
        }
    }

    [Fact]
    public async Task 主表回显_无记忆用首个来源_有记忆改用记忆来源()
    {
        var token = CancellationToken.None;
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync(token);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(token);
        try
        {
            const int moduleId = 990002;
            const string table = "MEMO_MASTER_TEST";
            const string field = "EMP_REF";
            const string companion = "EMP_NAME";
            var fields = BuildFields(field, companion);
            var (empId, empName) = await ReadEmployeeAsync(connection, transaction);
            var keyValues = new List<string> { "MASTER-KEY-1" };

            // 无记忆：回退首个启用来源（HR_EMPLOYEE），伴生字段按名字解析出来
            var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase) { [field] = empId };
            await WorkbenchCommandHandler.ResolveChooserDisplaysAsync(connection, transaction, moduleId, table, fields, row, keyValues, token);
            Assert.Equal(empName, row[companion]);

            // 记忆指向来源 2（缺伴生列的表）：来源被切换到该表，解析放弃，伴生字段不再被填
            await FormChooserSourceMemo.InsertAsync(connection, transaction, moduleId, table, field,
                FormChooserSourceMemo.SerializeKey(keyValues), FormChooserSourceMemo.SerializeKey(keyValues), 2, "tester", token);
            var rememberedRow = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase) { [field] = empId };
            await WorkbenchCommandHandler.ResolveChooserDisplaysAsync(connection, transaction, moduleId, table, fields, rememberedRow, keyValues, token);
            Assert.False(rememberedRow.ContainsKey(companion));

            // 记忆改回来源 1：重新按第一个来源解析
            await FormChooserSourceMemo.DeleteEntriesAsync(connection, transaction, moduleId, table, [(field, FormChooserSourceMemo.SerializeKey(keyValues))], token);
            await FormChooserSourceMemo.InsertAsync(connection, transaction, moduleId, table, field,
                FormChooserSourceMemo.SerializeKey(keyValues), FormChooserSourceMemo.SerializeKey(keyValues), 1, "tester", token);
            var restoredRow = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase) { [field] = empId };
            await WorkbenchCommandHandler.ResolveChooserDisplaysAsync(connection, transaction, moduleId, table, fields, restoredRow, keyValues, token);
            Assert.Equal(empName, restoredRow[companion]);
        }
        finally
        {
            await transaction.RollbackAsync(token);
        }
    }

    [Fact]
    public async Task 明细回显_按明细行键定位记忆_行键不同不串用()
    {
        var token = CancellationToken.None;
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync(token);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(token);
        try
        {
            const int moduleId = 990003;
            const string detailTable = "COP_ORDER_D";   // 主键 ORDER_TYPE + ORDER_NO + SERIAL_NO
            const string field = "ORDER_EMP_REF";
            const string companion = "ORDER_EMP_NAME";
            var fields = BuildFields(field, companion);
            var (empId, empName) = await ReadEmployeeAsync(connection, transaction);
            var keyValues = new List<string> { "MASTER-KEY-2" };
            var rows = new List<Dictionary<string, object?>>
            {
                new(StringComparer.OrdinalIgnoreCase) { ["ORDER_TYPE"] = "A", ["ORDER_NO"] = "N1", ["SERIAL_NO"] = "1", [field] = empId },
                new(StringComparer.OrdinalIgnoreCase) { ["ORDER_TYPE"] = "A", ["ORDER_NO"] = "N1", ["SERIAL_NO"] = "2", [field] = empId },
            };
            var firstRowKey = FormChooserSourceMemo.SerializeKey(["A", "N1", "1"]);

            // 只给第一行写记忆（指向不可解析的来源 2）：只有第一行受影响，第二行仍走首个来源
            await FormChooserSourceMemo.InsertAsync(connection, transaction, moduleId, detailTable, field,
                firstRowKey, FormChooserSourceMemo.SerializeKey(keyValues), 2, "tester", token);
            await WorkbenchCommandHandler.ResolveDetailChooserDisplaysAsync(connection, transaction, moduleId, detailTable, fields, rows, keyValues, token);
            Assert.False(rows[0].ContainsKey(companion));
            Assert.Equal(empName, rows[1][companion]);
        }
        finally
        {
            await transaction.RollbackAsync(token);
        }
    }
}
