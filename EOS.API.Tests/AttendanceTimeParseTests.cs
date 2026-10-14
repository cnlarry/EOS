using EOS.API.Data;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// 考勤里的时间文本解析。过去读不懂一律返回 0，而 0 分钟就是午夜：
/// 一份脏的打卡或排班会被当成"零点整到岗"，算出提前八小时到岗或荒谬的工时，
/// 且不报错、不告警——只在报表上留一条没人看得懂的记录。
/// 现在读不懂返回 <see langword="null"/>，由调用方跳过该项计算并把笔数计入结果。
/// </summary>
public sealed class AttendanceTimeParseTests
{
    [Theory]
    [InlineData("08:30", 510)]
    [InlineData("00:00", 0)]
    [InlineData("23:59", 1439)]
    [InlineData("08:30:00", 510)]
    [InlineData(" 08:30 ", 510)]
    [InlineData("17:05", 1025)]
    public void ParseTime_合法时间_解析为自零点的分钟数(string text, int expected)
    {
        Assert.Equal(expected, AttendanceCalcService.ParseTime(text));
    }

    [Theory]
    [InlineData("24:30", 1470)]   // 跨天排班的时段可落到次日
    [InlineData("25:15", 1515)]
    public void ParseTime_跨天排班_允许小时超过24(string text, int expected)
    {
        Assert.Equal(expected, AttendanceCalcService.ParseTime(text));
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("8")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(":")]
    [InlineData("08:xx")]
    [InlineData("xx:30")]
    [InlineData("25:70")]     // 分钟越界：过去算成次日凌晨 2:10
    [InlineData("08:60")]
    [InlineData("-1:30")]     // 负时长：过去算成 -90 分钟
    [InlineData("48:00")]     // 超出跨天上限
    public void ParseTime_读不懂或越界_返回空而不是零(string text)
    {
        // 判别点：退回"一律返回 0"时，这里每一条都会变成 0 而不是 null
        Assert.Null(AttendanceCalcService.ParseTime(text));
    }
}
