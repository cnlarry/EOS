using EOS.API.Data;
using EOS.API.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// 数据表/字段维护仓储的输入校验单测（不触库：非法输入在打开连接前即被拒绝）。
/// 数据库行为见 FieldAdminRepositoryIntegrationTests。
/// </summary>
public sealed class FieldAdminRepositoryTests
{
    private static FieldAdminRepository CreateRepository()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:ErpDatabase"] = "Server=localhost;Database=EOS.ERP;Integrated Security=True;Encrypt=True;TrustServerCertificate=True",
            })
            .Build();
        return new FieldAdminRepository(new DbConnectionFactory(config), new WorkbenchDirtyMarker(new DbConnectionFactory(config)), NullLogger<FieldAdminRepository>.Instance);
    }

    private static FieldAdminTableInput TableInput(string description = "测试表") => new(description, "P", "TABLE", null);

    [Theory]
    [InlineData("")]
    [InlineData("bad table")]
    [InlineData("1BAD")]
    [InlineData("A;B")]
    public async Task CreateTable_RejectsInvalidIdentifier(string tableId)
    {
        var repository = CreateRepository();
        await Assert.ThrowsAsync<ArgumentException>(
            () => repository.CreateTableAsync(new(tableId, TableInput()), "IT", CancellationToken.None));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task CreateTable_RejectsEmptyDescription(string description)
    {
        var repository = CreateRepository();
        await Assert.ThrowsAsync<ArgumentException>(
            () => repository.CreateTableAsync(new("COMPANY", TableInput(description)), "IT", CancellationToken.None));
    }

    [Fact]
    public async Task CreateTable_RejectsInvalidKindAndType()
    {
        var repository = CreateRepository();
        await Assert.ThrowsAsync<ArgumentException>(
            () => repository.CreateTableAsync(new("COMPANY", new("测试", "X", "TABLE", null)), "IT", CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(
            () => repository.CreateTableAsync(new("COMPANY", new("测试", "P", "BOGUS", null)), "IT", CancellationToken.None));
    }

    [Fact]
    public async Task GetTable_RejectsInvalidIdentifier()
    {
        var repository = CreateRepository();
        await Assert.ThrowsAsync<ArgumentException>(
            () => repository.GetTableAsync("bad table", CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(
            () => repository.GetUnmanagedFieldsAsync("bad table", CancellationToken.None));
    }

    [Fact]
    public async Task CreateField_RejectsInvalidTypeBeforeOpeningConnection()
    {
        var repository = CreateRepository();
        var request = new CreateFieldAdminRequest("COMPANY", "COMPANY_ID", new FieldAdminInput(
            Label: "测试", DataType: "NOT_A_TYPE", Width: 100, Align: null, HeaderAlign: "center", Format: null,
            IsVisible: true, IsDefault: true, IsQueryable: true, IsReadonly: false, IsRequired: false,
            IsCost: false, IsSecrecy: false, DefaultValue: null, VerifyIndex: null, Regex: null, Remark: null,
            BrowseUrl: null, BrowseModuleId: null, OnlyChoose: false, ChooseMultiple: false, ChoosePage: null,
            Choosers: [
                new(false, null, null, null, null, null),
                new(false, null, null, null, null, null),
                new(false, null, null, null, null, null),
                new(false, null, null, null, null, null),
            ],
            CanCopy: true));
        await Assert.ThrowsAsync<ArgumentException>(
            () => repository.CreateAsync(request, "IT", CancellationToken.None));
    }

    [Fact]
    public async Task CreateUnmanagedFields_RejectsEmptySelection()
    {
        var repository = CreateRepository();
        await Assert.ThrowsAsync<ArgumentException>(
            () => repository.CreateUnmanagedFieldsAsync(new("COMPANY", []), "IT", CancellationToken.None));
    }

    [Fact]
    public async Task CreateUnmanagedFields_RejectsInvalidFieldIdentifier()
    {
        var repository = CreateRepository();
        await Assert.ThrowsAsync<ArgumentException>(
            () => repository.CreateUnmanagedFieldsAsync(new("COMPANY", ["bad field"]), "IT", CancellationToken.None));
    }
}
