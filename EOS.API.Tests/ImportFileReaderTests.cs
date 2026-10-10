using System.Text;
using EOS.API.Features.Import;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// 导入文件读取（CSV / xlsx）的行为：客户旧系统导出的表格形态很杂，这里钉住"读到的是什么"。
/// 业务判定不在这里——它由写路径负责，本层只保证格子与列对齐。
/// </summary>
public sealed class ImportFileReaderTests
{
    private static ImportFileData Read(string fileName, string text, Encoding? encoding = null)
    {
        var bytes = (encoding ?? new UTF8Encoding(false)).GetBytes(text);
        return TabularFileReader.Read(fileName, new MemoryStream(bytes));
    }

    [Fact]
    public void Csv_引号内的逗号与换行不当作分隔()
    {
        var data = Read("a.csv", "料号,说明\nA1,\"x,1\"\nA2,\"第一行\n第二行\"\n");

        Assert.Equal(["料号", "说明"], data.Columns);
        Assert.Equal(2, data.Rows.Count);
        Assert.Equal(["A1", "x,1"], data.Rows[0]);
        Assert.Equal(["A2", "第一行\n第二行"], data.Rows[1]);
        Assert.False(data.Truncated);
    }

    [Fact]
    public void Csv_双引号转义还原成一个引号()
    {
        var data = Read("a.csv", "A\n\"说 \"\"你好\"\"\"\n");

        Assert.Equal(["说 \"你好\""], data.Rows[0]);
    }

    [Fact]
    public void Csv_按首个物理行判定分隔符_支持分号与制表符()
    {
        Assert.Equal(["A", "B"], Read("a.csv", "A;B\n1;2").Columns);
        Assert.Equal(["A", "B"], Read("a.csv", "A\tB\n1\t2").Columns);
        // 扩展名已表明用制表符：即使内容里有逗号也不改判
        Assert.Equal(["A,B", "C"], Read("a.tsv", "A,B\tC\n1\t2").Columns);
    }

    [Fact]
    public void Csv_空表头回落列序号_参差行按最大列数补齐()
    {
        var data = Read("a.csv", ",B,C\n1\n1,2,3,4\n");

        Assert.Equal(["列1", "B", "C", "列4"], data.Columns);
        Assert.Equal(2, data.Rows.Count);
        Assert.Equal(["1", "", "", ""], data.Rows[0]);
        Assert.Equal(["1", "2", "3", "4"], data.Rows[1]);
    }

    [Fact]
    public void Csv_全空行被丢弃_不影响行号语义()
    {
        var data = Read("a.csv", "A,B\n1,2\n,\n\n3,4\n");

        Assert.Equal(2, data.Rows.Count);
        Assert.Equal(["1", "2"], data.Rows[0]);
        Assert.Equal(["3", "4"], data.Rows[1]);
    }

    [Fact]
    public void Csv_带BOM的UTF8不把BOM读进第一个列名()
    {
        var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes("料号,名称\nA1,螺丝\n")).ToArray();
        var data = TabularFileReader.Read("a.csv", new MemoryStream(bytes));

        Assert.Equal(["料号", "名称"], data.Columns);
        Assert.Equal(["A1", "螺丝"], data.Rows[0]);
    }

    [Fact]
    public void Csv_非UTF8的GB18030文本按原编码读出中文()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var gb = Encoding.GetEncoding("GB18030");

        var data = Read("旧系统导出.csv", "料号,名称\nA1,螺丝\n", gb);

        Assert.Equal(["料号", "名称"], data.Columns);
        Assert.Equal(["A1", "螺丝"], data.Rows[0]);
    }

    [Fact]
    public void Csv_超过行数上限即截断并把总数如实回报()
    {
        var builder = new StringBuilder("A\n");
        for (var index = 0; index < ImportLimits.MaxRows + 5; index++)
        {
            builder.Append(index).Append('\n');
        }

        var data = Read("big.csv", builder.ToString());

        Assert.True(data.Truncated);
        Assert.Equal(ImportLimits.MaxRows + 5, data.TotalRows);
        Assert.Equal(ImportLimits.MaxRows, data.Rows.Count);
    }

    [Fact]
    public void 旧版xls给出可照做的提示而不是解析失败()
    {
        var exception = Assert.Throws<ImportFileException>(() => Read("a.xls", "whatever"));
        Assert.Equal("IMPORT_FILE_UNSUPPORTED", exception.Code);
        Assert.Contains(".xlsx", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void 空文件与不支持的扩展名各有独立错误码()
    {
        Assert.Equal("IMPORT_FILE_EMPTY",
            Assert.Throws<ImportFileException>(() => TabularFileReader.Read("a.csv", new MemoryStream())).Code);
        Assert.Equal("IMPORT_FILE_UNSUPPORTED",
            Assert.Throws<ImportFileException>(() => Read("a.json", "{}")).Code);
    }

    [Fact]
    public void Xlsx_读取列与数据行_数字不带小数尾巴()
    {
        using var stream = new MemoryStream();
        MiniExcelLibs.MiniExcel.SaveAs(stream, new[]
        {
            new { 料号 = "A1", 数量 = 12.0 },
            new { 料号 = "A2", 数量 = 3.5 },
        });
        stream.Position = 0;

        var data = TabularFileReader.Read("客户资料.xlsx", stream);

        Assert.Equal(["料号", "数量"], data.Columns);
        Assert.Equal(2, data.Rows.Count);
        Assert.Equal(["A1", "12"], data.Rows[0]);
        Assert.Equal(["A2", "3.5"], data.Rows[1]);
    }
}
