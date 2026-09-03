using System.Data;
using EOS.API.Errors;
using EOS.API.Models;
using EOS.API.Telemetry;
using Microsoft.Data.SqlClient;

namespace EOS.API.Data;

/// <summary>
/// 通用单据打印数据（报表打印模板的第一步）：
/// 主表 + 明细 + 列定义（FIELDS 可见列，成本/保密/禁止过滤）+ 页头/页脚
/// （REPORT_LAYOUT，按模块默认报表或 DEFAULT）。
/// 全部标识符来自服务端 Definition 白名单，键值参数化；主表读取应用用户数据范围
/// （DATA_FILTER + EXEC_TAG）——范围外单据返回 null（404），不输出越权打印。
/// </summary>
public sealed class PrintService(
    DbConnectionFactory connections,
    WorkbenchScopeFilter scopeFilter,
    ILogger<PrintService> logger)
{

    public async Task<PrintData?> GetPrintDataAsync(
        WorkbenchDefinition definition,
        IReadOnlyList<string> keyValues,
        string? headerId,
        string? tailId,
        bool canViewCost,
        bool canViewSecrecy,
        IReadOnlySet<string> deniedMasterFields,
        IReadOnlySet<string> deniedDetailFields,
        string? dataFilter,
        CancellationToken token)
    {
        using var timing = DbTimingCollector.Instance.Measure();
        await using var connection=connections.Create();
        await connection.OpenAsync(token);
        var moduleId=definition.ModuleId;
        var title=definition.Title;
        var masterTable=definition.MasterTable;
        var detailTable=definition.DetailTable;

        var pkOrder=definition.MasterPkOrder;
        if(pkOrder.Count==0||pkOrder.Count!=keyValues.Count)return null;
        var masterFields=await ReadFieldsAsync(connection,masterTable,canViewCost,canViewSecrecy,deniedMasterFields,token);
        if(!scopeFilter.TryBuildRecordScopePredicate(definition,dataFilter,out var scopePredicate,out var scopeParameters))
            throw new DataFilterUnsupportedException("当前用户的权限数据范围尚不支持，已拒绝打印。");
        var master=await ReadRowAsync(connection,masterTable,pkOrder,keyValues,masterFields.Select(field=>field.Key).ToList(),
            scopePredicate,scopeParameters,token);
        if(master is null)return null;

        IReadOnlyList<PrintField> detailFields=[];
        IReadOnlyList<IReadOnlyDictionary<string,object?>> details=[];
        if(detailTable is { Length: >0 }&&WorkbenchSql.Identifier.IsMatch(detailTable))
        {
            detailFields=await ReadFieldsAsync(connection,detailTable,canViewCost,canViewSecrecy,deniedDetailFields,token);
            details=await ReadRowsAsync(connection,detailTable,pkOrder,keyValues,detailFields.Select(field=>field.Key).ToList(),token);
        }
        var (headerCompany,headerCompanyEn,headerText,footerText,logoPath,tailText)=await ReadHeaderFooterAsync(connection,moduleId,headerId,tailId,token);
        headerText=ReplacePlaceholders(headerText,master);
        footerText=ReplacePlaceholders(footerText,master);
        var masterResolved=await ResolvePartyNameAsync(connection,masterTable,master,token);
        var clientProfile=await ResolveClientProfileAsync(connection,masterTable,masterResolved,token);
        logger.LogDebug("打印数据 module={ModuleId} master={Master} details={DetailCount} clientProfile={HasClient}",
            moduleId,masterTable,details.Count,clientProfile is not null);
        return new PrintData(moduleId,title,headerCompany,headerCompanyEn,headerText,footerText,logoPath,tailText,
            OrderPrintFields(masterFields),OrderPrintFields(detailFields),masterResolved,details,clientProfile);
    }

    /// <summary>
    /// 打印版式往来单位名称补齐：部分单据主表未携带客户/厂商名称列（如 PUR_PURCHASE_M
    /// 无 SUPPLIER_NAME），或主表名称字段未落库（如统一表单建单未回填 CLIENT_NAME）；
    /// 按主表 CLIENT_ID/SUPPLIER_ID 从 CLIENT/SUPPLIER 回查名称注入载荷
    /// （表/列名为服务端常量，值参数化），供版式"客户/厂商"行展示。
    /// </summary>
    private static async Task<IReadOnlyDictionary<string,object?>> ResolvePartyNameAsync(
        SqlConnection connection,string masterTable,IReadOnlyDictionary<string,object?> master,CancellationToken token)
    {
        var result=new Dictionary<string,object?>(master,StringComparer.OrdinalIgnoreCase);
        string? partyTable=null;
        string? idField=null;
        string? nameField=null;
        if(master.ContainsKey("CLIENT_ID")){partyTable="CLIENT";idField="CLIENT_ID";nameField="CLIENT_NAME";}
        else if(master.ContainsKey("SUPPLIER_ID")){partyTable="SUPPLIER";idField="SUPPLIER_ID";nameField="SUPPLIER_NAME";}
        if(partyTable is null)return result;
        var idValue=Convert.ToString(master.GetValueOrDefault(idField!))?.Trim();
        if(string.IsNullOrWhiteSpace(idValue))return result;
        await using var command=new SqlCommand(
            $"SELECT LTRIM(RTRIM([{nameField}])) FROM dbo.[{partyTable}] WITH (NOLOCK) WHERE [{idField}]=@id;",connection);
        command.Parameters.Add("@id",SqlDbType.NVarChar,50).Value=idValue;
        var name=await command.ExecuteScalarAsync(token) as string;
        if(!string.IsNullOrWhiteSpace(name))result[nameField!]=name;
        return result;
    }

    /// <summary>
    /// 往来单位资料 + 客户级页头默认（P6）：按主表 CLIENT_ID/SUPPLIER_ID 从
    /// CLIENT/SUPPLIER 回查——HEADER_ID / FULL_NAME_CN|EN / DELI_ADDR_CN|EN /
    /// TEL / FAX / LINKMAN / PRINT_PRICE。返回 ClientPrintProfile 供正文往来单位行/单价显隐使用，
    /// HEADER_ID 供控制器解析客户级页头默认（页头公司名不得被客户全称覆盖）。
    /// 表/列名为服务端常量白名单，值参数化。CLIENT.PRINT_PRICE 启用前需审计取值分布。
    /// </summary>
    private static async Task<ClientPrintProfile?> ResolveClientProfileAsync(
        SqlConnection connection,string masterTable,IReadOnlyDictionary<string,object?> master,CancellationToken token)
    {
        string? partyTable=null;
        string? idField=null;
        if(master.ContainsKey("CLIENT_ID")){partyTable="CLIENT";idField="CLIENT_ID";}
        else if(master.ContainsKey("SUPPLIER_ID")){partyTable="SUPPLIER";idField="SUPPLIER_ID";}
        if(partyTable is null)return null;
        var idValue=Convert.ToString(master.GetValueOrDefault(idField!))?.Trim();
        if(string.IsNullOrWhiteSpace(idValue))return null;

        var isClient=partyTable.Equals("CLIENT",StringComparison.OrdinalIgnoreCase);
        var columns=isClient
            ? "CLIENT_NAME,FULL_NAME_CN,FULL_NAME_EN,DELI_ADDR_CN,DELI_ADDR_EN,TEL,FAX,LINKMAN,HEADER_ID,ISNULL(PRINT_PRICE,0)"
            : "SUPPLIER_NAME,FULL_NAME_CN,FULL_NAME_EN,DELI_ADDR_CN,DELI_ADDR_EN,TEL,FAX,LINKMAN,HEADER_ID,0";
        await using var command=new SqlCommand(
            $"SELECT {columns} FROM dbo.[{partyTable}] WITH (NOLOCK) WHERE [{idField}]=@id;",connection);
        command.Parameters.Add("@id",SqlDbType.NVarChar,50).Value=idValue;
        await using var reader=await command.ExecuteReaderAsync(token);
        if(!await reader.ReadAsync(token))return null;
        var name=reader.IsDBNull(0)?null:reader.GetString(0).Trim();
        var fullCn=reader.IsDBNull(1)?null:reader.GetString(1).Trim();
        var fullEn=reader.IsDBNull(2)?null:reader.GetString(2).Trim();
        var addrCn=reader.IsDBNull(3)?null:reader.GetString(3).Trim();
        var addrEn=reader.IsDBNull(4)?null:reader.GetString(4).Trim();
        var tel=reader.IsDBNull(5)?null:reader.GetString(5).Trim();
        var fax=reader.IsDBNull(6)?null:reader.GetString(6).Trim();
        var linkman=reader.IsDBNull(7)?null:reader.GetString(7).Trim();
        var headerId=reader.IsDBNull(8)?null:reader.GetString(8).Trim();
        var printPrice=!reader.IsDBNull(9)&&Convert.ToBoolean(reader.GetValue(9));
        return new ClientPrintProfile(name,fullCn,fullEn,addrCn,addrEn,tel,fax,linkman,headerId,printPrice);
    }

    /// <summary>
    /// 打印字段排序：单号/日期/编号/名称/金额/数量类字段优先（单据版式关键信息前置）。
    /// </summary>
    private static IReadOnlyList<PrintField> OrderPrintFields(IReadOnlyList<PrintField> fields)
    {
        static int Rank(string key)
        {
            if(key.Contains("NO",StringComparison.OrdinalIgnoreCase))return 0;
            if(key.Contains("DATE",StringComparison.OrdinalIgnoreCase)||key.Contains("TIME",StringComparison.OrdinalIgnoreCase))return 1;
            if(key.Contains("ID",StringComparison.OrdinalIgnoreCase))return 2;
            if(key.Contains("NAME",StringComparison.OrdinalIgnoreCase))return 3;
            if(key.Contains("AMOUNT",StringComparison.OrdinalIgnoreCase)||key.Contains("PRICE",StringComparison.OrdinalIgnoreCase))return 4;
            if(key.Contains("QTY",StringComparison.OrdinalIgnoreCase)||key.Contains("QUANTITY",StringComparison.OrdinalIgnoreCase))return 5;
            return 9;
        }
        return fields.OrderBy(field=>Rank(field.Key)).ToList();
    }

    /// <summary>
    /// 页头/页脚占位符基础映射：
    /// {1} 制表人、{2} 最后更新、{5} 审核人、{7} 列印人（当前登录员工）。
    /// 其余 {n} 保留原文。
    /// </summary>
    private static string? ReplacePlaceholders(string? text,IReadOnlyDictionary<string,object?> master)
    {
        if(string.IsNullOrWhiteSpace(text))return text;
        var result=text;
        result=result.Replace("{1}",FieldValue(master,"CREATE_PERSON"));
        result=result.Replace("{2}",FieldValue(master,"LAST_UPDATE_BY"));
        result=result.Replace("{5}",FieldValue(master,"CONFIRM_PERSON"));
        result=result.Replace("{7}",Environment.UserName);
        return result;
    }

    private static string FieldValue(IReadOnlyDictionary<string,object?> row,string key)=>
        row.TryGetValue(key,out var value)&&value is not null?Convert.ToString(value)!.Trim():"";

    private static async Task<(string? Company,string? CompanyEn,string? Header,string? Footer,string? Logo,string? Tail)> ReadHeaderFooterAsync(
        SqlConnection connection,int moduleId,string? headerIdOverride,string? tailIdOverride,CancellationToken token)
    {
        const string sql="""
            SELECT TOP 1 r.HEADER_ID,r.TAIL_ID
            FROM dbo.REPORT r WITH (NOLOCK)
            WHERE r.R_M_IDX=@ModuleId AND r.IS_DEFAULT=1
            ORDER BY r.REPORT_ID;
            """;
        await using var command=new SqlCommand(sql,connection);
        command.Parameters.Add("@ModuleId",SqlDbType.Int).Value=moduleId;
        await using var reader=await command.ExecuteReaderAsync(token);
        string? defaultHeaderId=null;
        string? defaultTailId=null;
        if(await reader.ReadAsync(token))
        {
            defaultHeaderId=reader.IsDBNull(0)?"":reader.GetString(0).Trim();
            defaultTailId=reader.IsDBNull(1)?"":reader.GetString(1).Trim();
        }
        await reader.DisposeAsync();
        var headerId=string.IsNullOrWhiteSpace(headerIdOverride)
            ? (string.IsNullOrWhiteSpace(defaultHeaderId)?"DEFAULT":defaultHeaderId)
            : headerIdOverride.Trim();
        const string headerSql="SELECT LTRIM(RTRIM(ISNULL(CONTENT,''))),LTRIM(RTRIM(ISNULL(IMAGE_PATH,''))) FROM dbo.REPORT_LAYOUT WITH (NOLOCK) WHERE KIND=N'HEADER' AND LAYOUT_ID=@Id;";
        await using var headerCommand=new SqlCommand(headerSql,connection);
        headerCommand.Parameters.Add("@Id",SqlDbType.NVarChar,50).Value=headerId;
        await using var headerReader=await headerCommand.ExecuteReaderAsync(token);
        string? company=null;
        string? companyEn=null;
        string? headerText=null;
        string? logoPath=null;
        if(await headerReader.ReadAsync(token))
        {
            var contentJson=headerReader.GetString(0);
            logoPath=headerReader.IsDBNull(1)?null:headerReader.GetString(1).Trim();
            var (c,ce,h)=ParseHeaderContent(contentJson);
            company=c;
            companyEn=ce;
            headerText=h;
        }
        await headerReader.DisposeAsync();
        var tailId=string.IsNullOrWhiteSpace(tailIdOverride)?defaultTailId:tailIdOverride.Trim();
        string? tailText=null;
        if(!string.IsNullOrEmpty(tailId))
        {
            const string tailSql="SELECT LTRIM(RTRIM(ISNULL(CONTENT,''))) FROM dbo.REPORT_LAYOUT WITH (NOLOCK) WHERE KIND=N'TAIL' AND LAYOUT_ID=@Id;";
            await using var tailCommand=new SqlCommand(tailSql,connection);
            tailCommand.Parameters.Add("@Id",SqlDbType.NVarChar,50).Value=tailId;
            tailText=await tailCommand.ExecuteScalarAsync(token) as string;
        }
        tailText=string.IsNullOrWhiteSpace(tailText)?null:tailText.Trim();
        return (company,companyEn,headerText,tailText,string.IsNullOrWhiteSpace(logoPath)?null:logoPath.Trim(),tailText);
    }

    /// <summary>解析 REPORT_LAYOUT 页头 CONTENT JSON（companyName/companyNameEn/headerText）。</summary>
    private static (string? Company,string? CompanyEn,string? HeaderText) ParseHeaderContent(string? json)
    {
        if(string.IsNullOrWhiteSpace(json))return(null,null,null);
        try
        {
            using var doc=System.Text.Json.JsonDocument.Parse(json);
            var root=doc.RootElement;
            var company=root.TryGetProperty("companyName",out var c)?c.GetString():null;
            var companyEn=root.TryGetProperty("companyNameEn",out var ce)?ce.GetString():null;
            var headerText=root.TryGetProperty("headerText",out var h)?h.GetString():null;
            return(company,companyEn,headerText);
        }
        catch
        {
            return(null,null,null);
        }
    }

    private static async Task<IReadOnlyList<PrintField>> ReadFieldsAsync(
        SqlConnection connection,string table,bool canViewCost,bool canViewSecrecy,
        IReadOnlySet<string> deniedFields,CancellationToken token)
    {
        const string sql="""
            SELECT LTRIM(RTRIM(f.F_ID)),COALESCE(NULLIF(LTRIM(RTRIM(f.F_DESC)),''),LTRIM(RTRIM(f.F_ID))),
                   COALESCE(f.IS_COST,0),COALESCE(f.IS_SECRECY,0),LTRIM(RTRIM(ISNULL(f.DISPLAY_FORMAT,'')))
            FROM dbo.FIELDS f WITH (NOLOCK)
            WHERE f.T_ID=@Table AND COALESCE(f.IS_VISIBLE,1)=1 AND COALESCE(f.IS_VIRTUAL,0)=0
              AND EXISTS (SELECT 1 FROM sys.columns c JOIN sys.objects o ON c.object_id=o.object_id AND o.type IN ('U','V') JOIN sys.schemas s ON o.schema_id=s.schema_id WHERE s.name=N'dbo' AND o.name=@Table AND c.name=f.F_ID)
            ORDER BY COALESCE(f.VERIFY_INDEX,999),f.F_ID;
            """;
        await using var command=new SqlCommand(sql,connection);
        command.Parameters.Add("@Table",SqlDbType.NVarChar,100).Value=table;
        await using var reader=await command.ExecuteReaderAsync(token);
        var result=new List<PrintField>();
        while(await reader.ReadAsync(token))
        {
            var key=reader.GetString(0);
            if(deniedFields.Contains(key))continue;
            if(Convert.ToBoolean(reader.GetValue(2))&&!canViewCost)continue;
            if(Convert.ToBoolean(reader.GetValue(3))&&!canViewSecrecy)continue;
            var displayFormat=reader.GetString(4);
            result.Add(new PrintField(key,reader.GetString(1),
                string.IsNullOrWhiteSpace(displayFormat)?null:displayFormat.Trim()));
        }
        return result;
    }

    private static async Task<Dictionary<string,object?>?> ReadRowAsync(
        SqlConnection connection,string table,IReadOnlyList<string> pkOrder,IReadOnlyList<string> keyValues,
        IReadOnlyList<string> fields,string scopePredicate,IReadOnlyList<object> scopeParameters,CancellationToken token)
    {
        var where=string.Join(" AND ",pkOrder.Select((pk,index)=>$"[{pk}]=@k{index}"));
        var scope=string.IsNullOrWhiteSpace(scopePredicate)?"":" AND ("+scopePredicate+")";
        var columns=string.Join(',',fields.Select(field=>$"[{field}]"));
        await using var command=new SqlCommand($"SELECT {columns} FROM dbo.[{table}] WITH (NOLOCK) WHERE {where}{scope};",connection);
        for(var i=0;i<pkOrder.Count;i++)command.Parameters.AddWithValue($"@k{i}",keyValues[i]);
        for(var i=0;i<scopeParameters.Count;i++)command.Parameters.AddWithValue($"@df{i}",scopeParameters[i]??DBNull.Value);
        await using var reader=await command.ExecuteReaderAsync(token);
        if(!await reader.ReadAsync(token))return null;
        var row=new Dictionary<string,object?>(StringComparer.OrdinalIgnoreCase);
        for(var i=0;i<reader.FieldCount;i++)row[reader.GetName(i)]=reader.IsDBNull(i)?null:reader.GetValue(i);
        return row;
    }

    private static async Task<IReadOnlyList<Dictionary<string,object?>>> ReadRowsAsync(
        SqlConnection connection,string table,IReadOnlyList<string> pkOrder,IReadOnlyList<string> keyValues,
        IReadOnlyList<string> fields,CancellationToken token)
    {
        var where=string.Join(" AND ",pkOrder.Select((pk,index)=>$"[{pk}]=@k{index}"));
        var columns=string.Join(',',fields.Select(field=>$"[{field}]"));
        await using var command=new SqlCommand($"SELECT {columns} FROM dbo.[{table}] WITH (NOLOCK) WHERE {where} ORDER BY [{fields[0]}];",connection);
        for(var i=0;i<pkOrder.Count;i++)command.Parameters.AddWithValue($"@k{i}",keyValues[i]);
        await using var reader=await command.ExecuteReaderAsync(token);
        var rows=new List<Dictionary<string,object?>>();
        while(await reader.ReadAsync(token))
        {
            var row=new Dictionary<string,object?>(StringComparer.OrdinalIgnoreCase);
            for(var i=0;i<reader.FieldCount;i++)row[reader.GetName(i)]=reader.IsDBNull(i)?null:reader.GetValue(i);
            rows.Add(row);
        }
        return rows;
    }
}
