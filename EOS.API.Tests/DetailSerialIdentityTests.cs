using EOS.API.Data;
using EOS.API.Models;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// 明细项次（SERIAL_NO）是**行身份**而不是序号：下游单据按"单号 + 项次"引用明细行，
/// 若每次保存都按提交顺序重赋 1..n，删掉中间一行就会让其后各行整体前移，下游引用随之
/// 静默指向另一行（真库实测存在"引用的项次在当前明细中不存在"与"引用项次超出当前行数"的历史数据）。
///
/// 口径：调用方回传各行**原有**项次（新行给 null），服务端接纳既有号、只给新行分配未占用的号。
/// </summary>
public sealed class DetailSerialIdentityTests
{
    private static FormFieldDefinition SerialField() =>
        new("SERIAL_NO", "项次", "smallint", 100, null, false, null, null, null, true, true, false, false, null, [],
            false, false, false, false, false, true, null);

    private static List<Dictionary<string, object?>> Rows(params string?[] markers)
    {
        var fields = SerialField();
        return markers
            .Select(marker => new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["EMP_ID"] = marker,
            })
            .ToList();
    }

    private static string[] Serials(IReadOnlyList<Dictionary<string, object?>> rows) =>
        rows.Select(row => row.TryGetValue("SERIAL_NO", out var value) ? Convert.ToString(value) ?? "<null>" : "<null>").ToArray();

    [Fact]
    public void 回传既有项次时保留原号_不再按顺序重排()
    {
        // 场景：原来 3 行（项次 1/2/3），用户删掉中间一行后保存——剩下两行必须保持 1 与 3
        var rows = Rows("员工A", "员工C");

        RecordPayloadValidator.AssignSerialNumbers(rows, [SerialField()], ["1", "3"]);

        Assert.Equal(["1", "3"], Serials(rows));
    }

    [Fact]
    public void 新行取下一个未占用的号()
    {
        var rows = Rows("员工A", "员工C", "员工D");

        RecordPayloadValidator.AssignSerialNumbers(rows, [SerialField()], ["1", "3", null]);

        Assert.Equal(["1", "3", "4"], Serials(rows));
    }

    [Fact]
    public void 未回传项次时保持既有行为_按顺序从1起编()
    {
        var rows = Rows("A", "B", "C");

        RecordPayloadValidator.AssignSerialNumbers(rows, [SerialField()], null);

        Assert.Equal(["1", "2", "3"], Serials(rows));
    }

    [Fact]
    public void 行内已有的项次不被覆盖且计入占用()
    {
        var rows = Rows("A", "B");
        rows[0]["SERIAL_NO"] = (short)7;   // 例如服务端带入

        RecordPayloadValidator.AssignSerialNumbers(rows, [SerialField()], ["9", null]);

        // 第一行保留 7；新行取「已用最大号 +1」= 8（不复用 ≤7 的任何号，避免顶替历史身份）
        Assert.Equal(["7", "8"], Serials(rows));
    }

    [Fact]
    public void 重复回传同一项次时后者让位_保证号不冲突()
    {
        var rows = Rows("A", "B");

        RecordPayloadValidator.AssignSerialNumbers(rows, [SerialField()], ["2", "2"]);

        // 第一行用 2；第二行的 2 已被占用，退让到最大号之后（保存路径另会以 400 拦下重复回传）
        Assert.Equal(["2", "3"], Serials(rows));
        Assert.Equal(2, Serials(rows).Distinct().Count());
    }

    [Fact]
    public void 无SERIAL_NO字段的表不受影响()
    {
        var rows = Rows("A");
        var noSerial = new FormFieldDefinition("EMP_ID", "工号", "nvarchar", 100, null, false, null, null, null,
            false, true, false, false, null, [], false, false, false, false, false, false, null);

        RecordPayloadValidator.AssignSerialNumbers(rows, [noSerial], ["5"]);

        Assert.False(rows[0].ContainsKey("SERIAL_NO"));
    }
}
