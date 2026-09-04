using System.Text.Json;
using EOS.API.Features.Assistant.Admin;
using EOS.API.Features.Assistant.Tools;
using EOS.API.Models;
using Xunit;

namespace EOS.API.Tests;

public sealed class ChangeSetServiceTests
{
    private sealed class FakeCatalog : IModulePlanCatalog
    {
        public Task<IReadOnlyList<FieldAdminTable>> ListTablesAsync(CancellationToken token) =>
            Task.FromResult<IReadOnlyList<FieldAdminTable>>(
                [new("COP_ORDER_M", "客户订单", "P", "TABLE", 2, 1, 0)]);

        public Task<IReadOnlyList<FieldAdminFieldSummary>> ListFieldsAsync(string tableId, CancellationToken token) =>
            Task.FromResult<IReadOnlyList<FieldAdminFieldSummary>>(
            [
                new(tableId, "ORDER_NO", "订单号", "nvarchar", false, true, true, true, false, false, false, true, true),
            ]);

        public Task<IReadOnlyList<FieldAdminUnmanagedField>> ListUnmanagedAsync(string tableId, CancellationToken token) =>
            Task.FromResult<IReadOnlyList<FieldAdminUnmanagedField>>([new("TMP_FLAG", "bit")]);
    }

    private sealed class FakeWriter : IChangeSetWriter
    {
        public int TableWrites { get; private set; }
        public int FieldWrites { get; private set; }

        public Task RegisterTableAsync(string tableId, string description, string? kind, string updatedBy, CancellationToken token)
        {
            TableWrites++;
            return Task.CompletedTask;
        }

        public Task<(int Created, int Skipped, IReadOnlyList<string> Reasons)> AddFieldsAsync(
            string tableId, IReadOnlyList<string> fieldIds, string updatedBy, CancellationToken token)
        {
            FieldWrites++;
            return Task.FromResult<(int, int, IReadOnlyList<string>)>((fieldIds.Count, 0, []));
        }
    }

    private static JsonElement Changeset(string json) =>
        JsonDocument.Parse(json).RootElement;

    [Fact]
    public async Task Trial_PreviewsWithoutWriting()
    {
        var writer = new FakeWriter();
        var service = new ChangeSetService(new FakeCatalog(), writer);

        var trial = await service.TrialAsync(Changeset("""
            {"version":1,"goal":"加一页","tables":[
              {"table":"COP_VISIT_M","action":"register_table","description":"回访单"},
              {"table":"COP_ORDER_M","action":"add_fields","fields":["TMP_FLAG","ORDER_NO","MISSING_COL"]}
            ]}
            """), CancellationToken.None);

        Assert.False(trial.Blocked);
        Assert.Equal(2, trial.Tables.Count);
        Assert.Contains(trial.Tables[1].WouldCreate, line => line == "TMP_FLAG");
        Assert.Contains(trial.Tables[1].WouldSkip, line => line.Contains("ORDER_NO"));
        Assert.Contains(trial.Tables[1].WouldSkip, line => line.Contains("MISSING_COL"));
        Assert.Equal(0, writer.TableWrites + writer.FieldWrites);
    }

    [Fact]
    public async Task Trial_BlocksUnknownAction_AndUnregisteredTable()
    {
        var service = new ChangeSetService(new FakeCatalog(), new FakeWriter());

        var trial = await service.TrialAsync(Changeset("""
            {"version":1,"goal":"加一页","tables":[
              {"table":"COP_ORDER_M","action":"drop_table"},
              {"table":"NOPE_M","action":"add_fields","fields":["A"]}
            ]}
            """), CancellationToken.None);

        Assert.True(trial.Blocked);
        Assert.All(trial.Tables, table => Assert.Equal("blocked", table.Status));
    }

    [Fact]
    public async Task Trial_RejectsIllegalIdentifiers()
    {
        var service = new ChangeSetService(new FakeCatalog(), new FakeWriter());

        var trial = await service.TrialAsync(Changeset("""
            {"version":1,"goal":"加一页","tables":[{"table":"A;DROP","action":"register_table"}]}
            """), CancellationToken.None);

        Assert.True(trial.Blocked);
    }

    [Fact]
    public async Task Execute_RefusesBlockedTrial_WithoutWriting()
    {
        var writer = new FakeWriter();
        var service = new ChangeSetService(new FakeCatalog(), writer);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ExecuteAsync(
            Changeset("""{"version":1,"goal":"加一页","tables":[{"table":"COP_ORDER_M","action":"drop_table"}]}"""),
            "tester", CancellationToken.None));
        Assert.Equal(0, writer.TableWrites + writer.FieldWrites);
    }

    [Fact]
    public async Task Execute_WritesInOrder_AfterPassingTrial()
    {
        var writer = new FakeWriter();
        var service = new ChangeSetService(new FakeCatalog(), writer);

        var result = await service.ExecuteAsync(Changeset("""
            {"version":1,"goal":"加一页","tables":[
              {"table":"COP_VISIT_M","action":"register_table","description":"回访单"},
              {"table":"COP_ORDER_M","action":"add_fields","fields":["TMP_FLAG"]}
            ]}
            """), "tester", CancellationToken.None);

        Assert.Equal(1, result.TablesRegistered);
        Assert.Equal(1, result.FieldsCreated);
        Assert.Equal(1, writer.TableWrites);
        Assert.Equal(1, writer.FieldWrites);
    }
}
