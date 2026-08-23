using EOS.API.Models;
using EOS.API.Security;
using Xunit;

namespace EOS.API.Tests;

public sealed class ModulePermissionTests
{
    private static ModulePermission Permission(
        bool browse = true,
        bool addNew = false,
        bool edit = false,
        bool delete = false,
        bool approve = false,
        bool endCase = false,
        bool fileView = false,
        bool setup = false)
    {
        var rights = new LegacyModuleRights(
            browse, false, false, setup,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            addNew, edit, delete, approve, false, endCase, false,
            fileView, false, false, false,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            string.Empty, "Z");
        return new ModulePermission(rights);
    }

    [Theory]
    [InlineData(true, PermissionAction.Browse, true)]
    [InlineData(true, PermissionAction.Delete, false)]
    [InlineData(false, PermissionAction.Browse, false)]
    public void Can_MapsNamedActions(bool browse, PermissionAction action, bool expected)
    {
        Assert.Equal(expected, Permission(browse: browse).Can(action));
    }

    [Fact]
    public void Can_MapsEachPermissionAction()
    {
        var permission = Permission(addNew: true, edit: true, delete: true, approve: true, endCase: true, fileView: true, setup: true);
        Assert.True(permission.Can(PermissionAction.AddNew));
        Assert.True(permission.Can(PermissionAction.Edit));
        Assert.True(permission.Can(PermissionAction.Delete));
        Assert.True(permission.Can(PermissionAction.Approve));
        Assert.True(permission.Can(PermissionAction.EndCase));
        Assert.True(permission.Can(PermissionAction.FileView));
        Assert.True(permission.Can(PermissionAction.Setup));
        Assert.False(permission.Can(PermissionAction.Deapprove));
        Assert.False(permission.Can(PermissionAction.UnEndCase));
    }
}
