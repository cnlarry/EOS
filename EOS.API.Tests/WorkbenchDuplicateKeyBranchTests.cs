using EOS.API.Data;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// 唯一键冲突的分支判定（<see cref="WorkbenchCommandHandler.IndexCoversBillNo"/>）：
/// 新增撞唯一键时，"自动单号被占（重新保存可取新号）"与"编号已存在（要用户改）"是两种不同的
/// 处置，判错的后果是把可重试的场景说成用户错误、或反之。这里把判据钉住。
/// </summary>
public class WorkbenchDuplicateKeyBranchTests
{
    private static WorkbenchCommandHandler.ConflictIndex Index(params string[] columns) =>
        new(columns, IsPrimaryKey: false);

    private static WorkbenchCommandHandler.ConflictIndex PrimaryKey(params string[] columns) =>
        new(columns, IsPrimaryKey: true);

    [Fact]
    public void 非主键唯一索引命中单号列_判为单号冲突()
    {
        Assert.True(WorkbenchCommandHandler.IndexCoversBillNo(
            Index("BILL_NO"), ["TYPE", "BILL_NO"], "BILL_NO"));
    }

    [Fact]
    public void 主键索引覆盖到主键列_判为单号冲突()
    {
        // 自动单号模块的主键是「单别 + 单号」，撞主键即撞单号。
        Assert.True(WorkbenchCommandHandler.IndexCoversBillNo(
            PrimaryKey("TYPE", "BILL_NO"), ["TYPE", "BILL_NO"], "BILL_NO"));
    }

    [Fact]
    public void 自动单号模块的主键冲突_一律按单号冲突处置()
    {
        // 第二判据是**有意放宽**的：只有自动单号模块才会走到这里，而这类模块的单号由服务端
        // 预生成，撞主键就是撞单号——不必再去核对主键列里是否字面包含单号列。
        // 放宽的代价是"主键里还有别的用户输入列"时文案可能不够精准，但方向是"可重试"，
        // 比把可重试的场景说成用户错误要好。
        Assert.True(WorkbenchCommandHandler.IndexCoversBillNo(
            PrimaryKey("PRO_NO"), ["PRO_NO"], "BILL_NO"));
    }

    [Fact]
    public void 非主键唯一索引且不含单号列_不算单号冲突()
    {
        Assert.False(WorkbenchCommandHandler.IndexCoversBillNo(
            Index("CLIENT_PRO_NO"), ["MOULD_ID"], "BILL_NO"));
    }

    [Fact]
    public void 索引名解析不出来时的空集合_不算单号冲突()
    {
        // 解析失败（消息本地化）时按"非单号冲突"处置：仍走可读的重复键 400，而不是上抛成 500。
        Assert.False(WorkbenchCommandHandler.IndexCoversBillNo(
            new WorkbenchCommandHandler.ConflictIndex(Array.Empty<string>(), false), ["PRO_NO"], "BILL_NO"));
    }
}
