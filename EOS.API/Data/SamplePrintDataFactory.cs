using System.Text.Json;
using EOS.API.Models;

namespace EOS.API.Data;

/// <summary>
/// sample.json → PrintData 工厂：
/// 格式包回归护栏与设计器 PDF 预览共用同一样例数据源。
/// </summary>
public static class SamplePrintDataFactory
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public static PrintData Build(string moduleId, string sampleJson)
    {
        using var doc = JsonDocument.Parse(sampleJson);
        var root = doc.RootElement;

        var master = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var prop in root.GetProperty("master").EnumerateObject())
            master[prop.Name] = ReadJsonValue(prop.Value);

        var details = new List<IReadOnlyDictionary<string, object?>>();
        foreach (var row in root.GetProperty("details").EnumerateArray())
        {
            var dict = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            foreach (var prop in row.EnumerateObject())
                dict[prop.Name] = ReadJsonValue(prop.Value);
            details.Add(dict);
        }

        var masterFields = root.TryGetProperty("masterFields", out var mf)
            ? mf.EnumerateArray().Select(e => new PrintField(
                e.GetProperty("key").GetString()!, e.GetProperty("label").GetString()!,
                e.TryGetProperty("displayFormat", out var df) ? df.GetString() : null)).ToList()
            : new List<PrintField>();
        var detailFields = root.TryGetProperty("detailFields", out var df2)
            ? df2.EnumerateArray().Select(e => new PrintField(
                e.GetProperty("key").GetString()!, e.GetProperty("label").GetString()!,
                e.TryGetProperty("displayFormat", out var fmt) ? fmt.GetString() : null)).ToList()
            : new List<PrintField>();

        ClientPrintProfile? profile = null;
        if (root.TryGetProperty("clientProfile", out var cp) && cp.ValueKind == JsonValueKind.Object)
        {
            static string? Str(JsonElement obj, string name)
            {
                if (!obj.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
                    return null;
                return value.GetString();
            }
            profile = new ClientPrintProfile(
                Str(cp, "clientName"), Str(cp, "clientNameCn"), Str(cp, "clientNameEn"),
                Str(cp, "deliAddrCn"), Str(cp, "deliAddrEn"), Str(cp, "tel"), Str(cp, "fax"),
                Str(cp, "linkman"), Str(cp, "headerId"),
                cp.TryGetProperty("printPrice", out var pp) && pp.ValueKind == JsonValueKind.True);
        }

        return new PrintData(
            int.Parse(moduleId, System.Globalization.CultureInfo.InvariantCulture),
            root.GetProperty("title").GetString()!,
            root.GetProperty("headerCompany").GetString(),
            root.GetProperty("headerCompanyEn").GetString(),
            root.GetProperty("headerText").GetString(),
            root.GetProperty("footerText").GetString(),
            root.GetProperty("logoPath").ValueKind == JsonValueKind.Null
                ? null : root.GetProperty("logoPath").GetString(),
            root.GetProperty("tailText").GetString(),
            masterFields, detailFields, master, details, profile);
    }

    /// <summary>
    /// 预览数据场景扩展：
    /// rows=明细行数（默认保持样例行数）；variant=longText（长文本）/ empty（空明细）。
    /// 仅复制/改写明细，主表与字段白名单不变。
    /// </summary>
    public static PrintData Expand(PrintData data, int? rows, string? variant)
    {
        var count = rows ?? data.Details.Count;
        var details = new List<Dictionary<string, object?>>();
        for (var i = 0; i < count; i++)
        {
            var baseRow = data.Details.Count > 0
                ? data.Details[Math.Min(i, data.Details.Count - 1)]
                : new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            var row = new Dictionary<string, object?>(baseRow, StringComparer.OrdinalIgnoreCase)
            {
                ["PRO_NO"] = $"HTP6-{i + 1:0000}",
                ["QTY"] = (i + 1) * 100,
                ["AMOUNT_TAX"] = (decimal)((i + 1) * 10) + 0.5m,
            };
            details.Add(row);
        }

        switch (variant?.Trim().ToLowerInvariant())
        {
            case "longtext":
                foreach (var row in details)
                {
                    row["PRO_NAME"] =
                        "这是一段超长的品名描述文本，用于验证设计器预览在长文本下的换行表现，" +
                        "请确认表格行不会因此被撑破或溢出、分页与表头重复正常。";
                    row["PRO_SPEC"] =
                        "超长规格参数说明 ABCDEFG-1234567890-abcdefghijklmnopqrstuvwxyz-①②③④⑤";
                }
                break;
            case "empty":
                details.Clear();
                break;
        }
        return data with { Details = details };
    }

    private static object? ReadJsonValue(JsonElement value)
    {
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.TryGetDecimal(out var d) ? d : value.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null,
            _ => value.GetRawText(),
        };
    }
}
