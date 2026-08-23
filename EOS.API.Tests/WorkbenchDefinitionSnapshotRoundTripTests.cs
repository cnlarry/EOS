using System.Text.Json;
using EOS.API.Data;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace EOS.API.Tests;

public sealed class WorkbenchDefinitionSnapshotRoundTripTests
{
    [Fact]
    public void Definition_SerializesAndDeserializes_ModuleLevelBaseline()
    {
        var definition = new WorkbenchDefinition(
            ModuleId: 1401,
            Title: "客户基本资料",
            MasterTable: "CLIENT",
            DetailTable: "CLIENT_CONTACT",
            MasterFields:
            [
                new WorkbenchField("CLIENT_ID", "客户编号", "nvarchar", 100, null, true),
                new WorkbenchField("CLIENT_NAME", "客户名称", "nvarchar", 120, null, false),
            ],
            DetailFields: [new WorkbenchField("LINKMAN", "联系人", "nvarchar", 100, null, false)],
            DefaultSort: "[CLIENT_ID] ASC",
            HasAdd: true,
            HasEdit: true,
            DetailNoSave: false,
            MasterPkOrder: ["CLIENT_ID"],
            DetailNoFields: "",
            HasWorkflow: false,
            ModuleFilter: "CLIENT.SALES_ID='A'",
            FilterFieldKeys: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "CLIENT_ID", "SALES_ID" },
            UserId: "",
            ExecTag: "Z",
            HasOwnerColumn: true,
            HasOwnerGroupColumn: true,
            BusinessRule: new ModuleBusinessRule(1401, null, "P_WF_TEST", true, "CLIENT_ID", "X"),
            AutoApprove: false,
            GroupExpressions: ["", "", "", "", ""],
            FormTabs: null,
            FormColumns: null,
            FormButtons: [new WorkbenchButton("approve")],
            IfCopy: false,
            SearchMaster: false,
            SearchDetail: false,
            NewUrl: "/document-workbench/1401/new",
            ModiUrl: "/document-workbench/1401/edit",
            DefinitionVersion: "module-1401-v1");

        var json = JsonSerializer.Serialize(definition);
        var roundTrip = JsonSerializer.Deserialize<WorkbenchDefinition>(json, WorkbenchDefinitionProvider.JsonOptions);

        Assert.NotNull(roundTrip);
        Assert.Equal(1401, roundTrip!.ModuleId);
        Assert.Equal("CLIENT", roundTrip.MasterTable);
        Assert.Equal("CLIENT_CONTACT", roundTrip.DetailTable);
        Assert.Equal(["CLIENT_ID"], roundTrip.MasterPkOrder);
        Assert.Equal("CLIENT.SALES_ID='A'", roundTrip.ModuleFilter);
        Assert.Equal(2, roundTrip.MasterFields.Count);
        Assert.Equal("P_WF_TEST", roundTrip.BusinessRule!.WorkflowSproc);
        Assert.Equal("approve", roundTrip.FormButtons!.Single().Action);
        Assert.True(roundTrip.FilterFieldKeys!.Contains("SALES_ID"));
        Assert.Equal("module-1401-v1", roundTrip.DefinitionVersion);
    }
}
