using EOS.API.Data;
using Microsoft.Data.SqlClient;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// 参数化主键 WHERE 原语：列名来自服务端元数据，值一律走 @kN 参数，不进入 SQL 文本。
/// </summary>
public sealed class WorkbenchSqlKeyWhereTests
{
    [Fact]
    public void BuildKeyWhere_UsesNumberedParametersInColumnOrder()
    {
        var where = WorkbenchSql.BuildKeyWhere(["QUOTE_TYPE", "QUOTE_NO"]);

        Assert.Equal("[QUOTE_TYPE]=@k0 AND [QUOTE_NO]=@k1", where);
    }

    [Fact]
    public void BuildKeyWhere_EmptyColumns_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, WorkbenchSql.BuildKeyWhere([]));
    }

    [Fact]
    public void BuildKeyWhere_WithValues_LeavesNoValueInSqlText()
    {
        var where = WorkbenchSql.BuildKeyWhere(["QUOTE_NO"], ["BJK'O8"]);

        Assert.Equal("[QUOTE_NO]=@k0", where);
        Assert.DoesNotContain("BJK", where);
    }

    [Fact]
    public void BuildKeyWhere_ColumnValueCountMismatch_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            WorkbenchSql.BuildKeyWhere(["A", "B"], ["1"]));
    }

    [Fact]
    public void AddKeyParameters_RegistersValuesInColumnOrder()
    {
        using var command = new SqlCommand();

        WorkbenchSql.AddKeyParameters(command, ["QUOTE_TYPE", "QUOTE_NO"], ["BJK", "BJK26080001"]);

        Assert.Equal("BJK", command.Parameters["@k0"].Value);
        Assert.Equal("BJK26080001", command.Parameters["@k1"].Value);
    }

    [Fact]
    public void AddKeyParameters_NullValue_FallsBackToEmptyString()
    {
        using var command = new SqlCommand();

        WorkbenchSql.AddKeyParameters(command, ["QUOTE_NO"], [null!]);

        Assert.Equal(string.Empty, command.Parameters["@k0"].Value);
    }
}
