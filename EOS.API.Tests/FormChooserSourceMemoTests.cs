using EOS.API.Data;
using EOS.API.Models;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// 选择器来源的记忆判定与来源选择口径（纯逻辑，不连库）。
///
/// 回显解析同组伴生字段时，来源从「恒取首个启用来源」改为「先看这张单记录下来的来源」；
/// 记忆必须让位于配置变更：记忆里的来源被停用/删除时回退首个启用来源，不能把配置变更
/// 变成回显错误；单来源字段取首个即唯一，不必记忆。
/// </summary>
public sealed class FormChooserSourceMemoTests
{
    private static FieldChooserSource Source(int serial, string? table, bool active = true) =>
        new(active, table, table, 1000 + serial, Filter: null, ReturnMapping: null, SerialNo: serial);

    private static FormFieldDefinition Field(
        IReadOnlyList<FieldChooserSource> choosers,
        int cellRole = 1,
        string? cellGroup = "GROUP",
        bool displayOnly = false) =>
        new(
            Key: "MAIN_FIELD",
            Label: "主字段",
            DataType: "nvarchar",
            DisplayLength: 100,
            DisplayFormat: null,
            IsRequired: false,
            VerifyIndex: null,
            Regex: null,
            DefaultValue: null,
            IsReadonly: false,
            IsVisible: true,
            OnlyChoose: false,
            ChooseMultiple: false,
            ChoosePage: null,
            Choosers: choosers,
            IsPrimaryKey: false,
            IsAutoIncrement: false,
            IsVirtual: false,
            IsCost: false,
            IsSecrecy: false,
            ServerFilled: false,
            MaxLength: 100,
            CellGroup: cellGroup,
            CellRole: cellRole,
            DisplayOnly: displayOnly);

    [Fact]
    public void 记忆命中_取记忆里的来源()
    {
        var field = Field([Source(1, "CLIENT"), Source(2, "SUPPLIER")]);

        var selected = FormChooserSourceMemo.SelectSource(field, 2);

        Assert.Equal(2, selected?.SerialNo);
        Assert.Equal("SUPPLIER", selected?.Table);
    }

    [Fact]
    public void 无记忆_取首个启用来源()
    {
        var field = Field([Source(1, "CLIENT"), Source(2, "SUPPLIER")]);

        Assert.Equal(1, FormChooserSourceMemo.SelectSource(field, null)?.SerialNo);
    }

    [Fact]
    public void 记忆指向已停用来源_回退首个启用来源()
    {
        var field = Field([Source(1, "CLIENT"), Source(2, "SUPPLIER", active: false)]);

        Assert.Equal(1, FormChooserSourceMemo.SelectSource(field, 2)?.SerialNo);
    }

    [Fact]
    public void 记忆指向已删除来源_回退首个启用来源()
    {
        var field = Field([Source(1, "CLIENT"), Source(2, "SUPPLIER")]);

        Assert.Equal(1, FormChooserSourceMemo.SelectSource(field, 99)?.SerialNo);
    }

    [Fact]
    public void 首个启用来源为空表_跳过它取下一个()
    {
        var field = Field([Source(1, "  "), Source(2, "SUPPLIER")]);

        Assert.Equal(2, FormChooserSourceMemo.SelectSource(field, null)?.SerialNo);
    }

    [Fact]
    public void 无可用来源_返回空()
    {
        var field = Field([Source(1, "CLIENT", active: false)]);

        Assert.Null(FormChooserSourceMemo.SelectSource(field, 1));
    }

    [Fact]
    public void 值得记忆_复合格主字段且多来源()
    {
        var field = Field([Source(1, "CLIENT"), Source(2, "SUPPLIER")]);

        Assert.True(FormChooserSourceMemo.ShouldRemember(field));
    }

    [Fact]
    public void 不值得记忆_单来源或非复合格主字段()
    {
        Assert.False(FormChooserSourceMemo.ShouldRemember(Field([Source(1, "CLIENT")])));
        Assert.False(FormChooserSourceMemo.ShouldRemember(Field([Source(1, "CLIENT"), Source(2, "SUPPLIER")], cellRole: 2)));
        Assert.False(FormChooserSourceMemo.ShouldRemember(Field([Source(1, "CLIENT"), Source(2, "SUPPLIER")], cellGroup: null)));
    }

    [Fact]
    public void 行键序列化_可直接作为记忆键的原样比对()
    {
        var key = FormChooserSourceMemo.SerializeKey(["SO2609001", "2"]);

        Assert.Equal("[\"SO2609001\",\"2\"]", key);
        // 内存字典键拆回来仍是「字段 + 行键」，行存活比对据此定位
        var entry = FormChooserSourceMemo.EntryKey("ORDER_TYPE", key);
        var (field, keyValues) = FormChooserSourceMemo.ParseEntryKey(entry);
        Assert.Equal("ORDER_TYPE", field);
        Assert.Equal(key, keyValues);
    }

    [Fact]
    public void 键超长时不记录_记忆是可选优化不能让保存失败()
    {
        var shortKey = FormChooserSourceMemo.SerializeKey(["SO2609001"]);
        var longKey = FormChooserSourceMemo.SerializeKey([new string('X', 300)]);

        Assert.True(FormChooserSourceMemo.KeyFits(shortKey));
        Assert.False(FormChooserSourceMemo.KeyFits(longKey));
        // 边界：恰好等于列宽应仍然可记
        var exactKey = FormChooserSourceMemo.SerializeKey([new string('Y', FormChooserSourceMemo.MaxKeyLength - 4)]);
        Assert.Equal(FormChooserSourceMemo.MaxKeyLength, exactKey.Length);
        Assert.True(FormChooserSourceMemo.KeyFits(exactKey));
    }

    /// <summary>
    /// 来源随保存请求下发：字段名对不上就会**静默**丢掉用户的来源选择（不报错、只是没记住），
    /// 因此按接口实际使用的 Web 默认选项钉住反序列化结果。
    /// </summary>
    [Fact]
    public void 保存请求_来源字段可被JSON绑定()
    {
        const string json = """
            {
              "values": { "PRO_NO": "P1" },
              "details": [ { "ITEM": "X1" } ],
              "idempotencyKey": "k1",
              "chooserSources": { "ORDER_TYPE": 2 },
              "detailChooserSources": [ { "PRO_NO": 3 }, null ]
            }
            """;

        var request = System.Text.Json.JsonSerializer.Deserialize<SaveRecordRequest>(
            json, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));

        Assert.NotNull(request);
        Assert.Equal(2, request!.ChooserSources!["ORDER_TYPE"]);
        Assert.NotNull(request.DetailChooserSources);
        Assert.Equal(2, request.DetailChooserSources!.Count);
        Assert.Equal(3, request.DetailChooserSources[0]!["PRO_NO"]);
        Assert.Null(request.DetailChooserSources[1]);
    }
}
