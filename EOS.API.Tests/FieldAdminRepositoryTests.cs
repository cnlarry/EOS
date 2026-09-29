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
        return new FieldAdminRepository(new DbConnectionFactory(config), new WorkbenchDirtyMarker(new DbConnectionFactory(config)),
            new WorkbenchAuditWriter(new DbConnectionFactory(config), new Microsoft.AspNetCore.Http.HttpContextAccessor(), Provider(config),
                Microsoft.Extensions.Options.Options.Create(new EOS.API.Models.AuditSettings())),
            new RestrictedExpressionService(new DbConnectionFactory(config)),
            new WorkbenchIdempotency(),
            NullLogger<FieldAdminRepository>.Instance);
    }

    private static FieldAdminTableInput TableInput(string description = "测试表") => new(description, "P", "TABLE", null);

    private static WorkbenchDefinitionProvider Provider(Microsoft.Extensions.Configuration.IConfiguration config) =>
        new(new DbConnectionFactory(config), NullLogger<WorkbenchDefinitionProvider>.Instance);

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
    public async Task CreateField_RejectsUncompilableRegexBeforeOpeningConnection()
    {
        var repository = CreateRepository();
        var request = new CreateFieldAdminRequest("COMPANY", "COMPANY_ID", FieldInput() with { Regex = "[" });
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

    private static FieldAdminInput FieldInput(
        string label = "测试字段",
        string? format = null,
        bool isVisible = true,
        bool isCost = false,
        int width = 100) => new(
        Label: label, DataType: "nvarchar", Width: width, Align: null, HeaderAlign: "center", Format: format,
        IsVisible: isVisible, IsDefault: true, IsQueryable: true, IsReadonly: false, IsRequired: false,
        IsCost: isCost, IsSecrecy: false, DefaultValue: null, VerifyIndex: null, Regex: null, Remark: null,
        BrowseUrl: null, BrowseModuleId: null, OnlyChoose: false, ChooseMultiple: false, ChoosePage: null,
        Choosers:
        [
            new(true, "CLIENT", "客户", 1401, null, "CLIENT_ID=CLIENT_ID"),
            new(false, null, null, null, null, null),
            new(false, null, null, null, null, null),
            new(false, null, null, null, null, null),
        ],
        CanCopy: true);

    [Fact]
    public void DiffInputs_ReportsOnlyChangedKeys()
    {
        var before = FieldInput();
        var after = FieldInput(label: "新名称", format: "0.##", isCost: true);
        var changes = FieldAdminRepository.DiffInputs(before, after);

        var keys = changes.Select(change => change.FieldName).ToArray();
        Assert.Equal(["DISPLAY_FORMAT", "F_DESC", "IS_COST"], keys);
        Assert.Equal("测试字段", changes.Single(change => change.FieldName == "F_DESC").OldValue);
        Assert.Equal("新名称", changes.Single(change => change.FieldName == "F_DESC").NewValue);
    }

    [Fact]
    public void DiffInputs_NoChanges_YieldsEmpty()
    {
        Assert.Empty(FieldAdminRepository.DiffInputs(FieldInput(), FieldInput()));
    }

    [Fact]
    public void DiffInputs_TrimsWhitespaceAndNormalizesNulls()
    {
        var before = FieldInput();
        var choosers = before.Choosers.ToArray();
        choosers[0] = choosers[0] with { Filter = "  " };
        var after = before with { Choosers = choosers };
        var changes = FieldAdminRepository.DiffInputs(before, after);
        Assert.Empty(changes);
    }

    [Fact]
    public void DescribeInput_FlattensChooserSlots()
    {
        var describe = FieldAdminRepository.DescribeInput(FieldInput());
        // Audit flattening keys are per FIELD_DATASOURCE entry (SERIAL_NO dimension)
        Assert.Equal("1", describe["CHOOSER[1].ACTIVE"]);
        Assert.Equal("CLIENT", describe["CHOOSER[1].SOURCE_T_ID"]);
        Assert.Equal("1401", describe["CHOOSER[1].SOURCE_M_IDX"]);
        Assert.Equal("CLIENT_ID=CLIENT_ID", describe["CHOOSER[1].RETURN_ITEMS"]);
        Assert.Equal("0", describe["CHOOSER[4].ACTIVE"]);
        Assert.Null(describe["CHOOSER[2].SOURCE_T_ID"]);
    }

    [Fact]
    public void DiffTables_ReportsChangedRemark()
    {
        var changes = FieldAdminRepository.DiffTables(
            new FieldAdminTableInput("测试表", "P", "TABLE", "备注A"),
            new FieldAdminTableInput("测试表", "P", "TABLE", "备注B"));
        var change = Assert.Single(changes);
        Assert.Equal("T_REMARK", change.FieldName);
        Assert.Equal("备注A", change.OldValue);
        Assert.Equal("备注B", change.NewValue);
    }

    [Theory]
    [InlineData("CONFIRM_TAG")]
    [InlineData("confirm_tag")]
    [InlineData("FINISHED_PERSON")]
    [InlineData("CREATE_DATE")]
    [InlineData("OWNER_G")]
    public async Task Delete_SystemColumn_RejectedBeforeOpeningConnection(string fieldId)
    {
        var repository = CreateRepository();
        var error = await Assert.ThrowsAsync<ArgumentException>(
            () => repository.DeleteAsync("COP_ORDER_M", fieldId, "IT", CancellationToken.None));
        Assert.Contains("系统列不允许删除", error.Message);
    }

    [Fact]
    public void SystemColumnUpdate_AllowsOnlyEditableMembers()
    {
        var current = FieldInput();
        Assert.Null(FieldAdminRepository.BuildSystemColumnUpdateError(
            current, current with { Label = "新名称", Width = 120, Remark = "备注", IsVisible = false }));
    }

    [Theory]
    [InlineData("DataType", "varchar")]
    [InlineData("Regex", "^\\d+$")]
    [InlineData("DefaultValue", "X")]
    public void SystemColumnUpdate_RejectsStructuralChange(string member, string value)
    {
        var current = FieldInput();
        var next = member switch
        {
            "DataType" => current with { DataType = value },
            "Regex" => current with { Regex = value },
            "DefaultValue" => current with { DefaultValue = value },
            _ => current,
        };
        Assert.NotNull(FieldAdminRepository.BuildSystemColumnUpdateError(current, next));
    }

    [Fact]
    public void SystemColumnUpdate_RejectsPermissionAndOptionEnumerationChange()
    {
        var current = FieldInput();
        Assert.NotNull(FieldAdminRepository.BuildSystemColumnUpdateError(
            current, current with { IsRequired = true }));
        Assert.NotNull(FieldAdminRepository.BuildSystemColumnUpdateError(
            current, current with { IsCost = true }));
        Assert.NotNull(FieldAdminRepository.BuildSystemColumnUpdateError(
            current, current with { Options = "O=外含税" }));
    }
}
