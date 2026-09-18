using System.Text.Json;
using EOS.API.Data;
using EOS.API.Models;
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
            BusinessRule: new ModuleBusinessRule(1401, true, "CLIENT_ID", "X"),
            AutoApprove: false,
            GroupExpressions: ["", "", "", "", ""],
            FormTabs: null,
            FormColumns: null,
            FormButtons: [new WorkbenchButton("approve")],
            IfCopy: false,
            SearchMaster: false,
            SearchDetail: false,
NewUrl: "/workbench/1401/new",
        ModiUrl: "/workbench/1401/edit",
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
        Assert.True(roundTrip.BusinessRule!.AutoBillNo);
        Assert.Equal("CLIENT_ID", roundTrip.BusinessRule!.BillNoField);
        Assert.Equal("approve", roundTrip.FormButtons!.Single().Action);
        Assert.True(roundTrip.FilterFieldKeys!.Contains("SALES_ID"));
        Assert.Equal("module-1401-v1", roundTrip.DefinitionVersion);
    }

    [Fact]
    public void Definition_RoundTrips_BusinessConfigSections()
    {
        var actions = JsonSerializer.SerializeToElement(new[]
        {
            new
            {
                seq = 1,
                eventCode = "APPROVE_EFFECT",
                effectKey = "field-accumulate",
                effectName = "收料量回写采购单",
                enabled = true,
                failMode = "BLOCK",
                ops = new[]
                {
                    new
                    {
                        opSeq = 1,
                        targetTable = "PUR_PURCHASE_D",
                        targetField = "RECEIVE_QTY",
                        opCode = "ACCUM",
                        sourceScope = "DETAIL",
                        sourceField = "QTY",
                        sourceAgg = "SUM",
                    },
                },
            },
        });
        var rules = JsonSerializer.SerializeToElement(new[]
        {
            new
            {
                seq = 1,
                stage = "APPROVE",
                validationKey = "qty-not-exceed",
                enabled = true,
                @params = "{\"mode\":\"usage-not-exceed\",\"checks\":[]}",
            },
        });
        var definition = new WorkbenchDefinition(
            ModuleId: 1607,
            Title: "收料单",
            MasterTable: "PUR_RECEIVE_M",
            DetailTable: "PUR_RECEIVE_D",
            MasterFields: [],
            DetailFields: [],
            DefaultSort: null,
            HasAdd: true,
            HasEdit: true,
            DetailNoSave: false,
            MasterPkOrder: ["RECEIVE_TYPE", "RECEIVE_NO"],
            DetailNoFields: "",
            HasWorkflow: true,
            UserId: "",
            ExecTag: "Z",
            GroupExpressions: ["", "", "", "", ""],
            DefinitionVersion: "module-1607-v1",
            BusinessActions: actions,
            ValidationRules: rules);

        var json = JsonSerializer.Serialize(definition);
        var roundTrip = JsonSerializer.Deserialize<WorkbenchDefinition>(json, WorkbenchDefinitionProvider.JsonOptions);

        Assert.NotNull(roundTrip);
        Assert.True(roundTrip!.BusinessActions.HasValue);
        Assert.Equal(1, roundTrip.BusinessActions.Value.GetArrayLength());
        Assert.Equal("field-accumulate", roundTrip.BusinessActions.Value[0].GetProperty("effectKey").GetString());
        Assert.True(roundTrip.ValidationRules.HasValue);
        Assert.Equal("qty-not-exceed", roundTrip.ValidationRules.Value[0].GetProperty("validationKey").GetString());
    }
}
