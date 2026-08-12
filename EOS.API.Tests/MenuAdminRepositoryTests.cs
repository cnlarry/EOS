using EOS.API.Data;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// 菜单同级排序目标位置纯逻辑测试（不依赖数据库）。
/// 覆盖 top/up/down/bottom、边界无操作与非法动作。
/// </summary>
public sealed class MenuAdminRepositoryTests
{
    [Theory]
    [InlineData("top", 2, 5, 0)]
    [InlineData("top", 0, 5, 0)]
    [InlineData("top", 4, 5, 0)]
    [InlineData("up", 2, 5, 1)]
    [InlineData("up", 0, 5, 0)]
    [InlineData("up", 1, 5, 0)]
    [InlineData("down", 2, 5, 3)]
    [InlineData("down", 4, 5, 4)]
    [InlineData("down", 3, 5, 4)]
    [InlineData("bottom", 2, 5, 4)]
    [InlineData("bottom", 4, 5, 4)]
    [InlineData("bottom", 0, 1, 0)]
    public void TargetIndex_ComputesPosition(string action, int currentIndex, int count, int expected)
    {
        Assert.Equal(expected, MenuAdminRepository.TargetIndex(currentIndex, count, action));
    }

    [Fact]
    public void TargetIndex_RejectsUnknownAction()
    {
        Assert.Throws<ArgumentException>(() => MenuAdminRepository.TargetIndex(0, 3, "left"));
        Assert.Throws<ArgumentException>(() => MenuAdminRepository.TargetIndex(0, 3, ""));
    }
}
