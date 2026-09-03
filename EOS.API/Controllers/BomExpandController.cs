using System.Data;
using EOS.API.Errors;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;

namespace EOS.API.Controllers;

[ApiController]
[Authorize]
[Route("api/v1/bom")]
public sealed class BomExpandController(Data.DbConnectionFactory connections) : ControllerBase
{
    /// <summary>
    /// BOM 多级展开（受控递归 CTE）：
    /// 产品号来自查询参数（标识符校验），用量 = 父数量 × ELEMENT_QTY / BASE_QTY ×（1+损耗率），
    /// 防环（路径前缀检查），阶数上限 99。
    /// </summary>
    [HttpGet("expand")]
    public async Task<IActionResult> Expand([FromQuery]string proNo,CancellationToken token,[FromQuery]double qty=1,[FromQuery]int maxLevel=99)
    {
        proNo=(proNo??"").Trim();
        if(proNo.Length==0||proNo.Length>60)return BadRequest(ApiProblem.Create(StatusCodes.Status400BadRequest,"INVALID_PRO_NO","产品编号不能为空。"));
        maxLevel=Math.Clamp(maxLevel,1,99);
        qty=Math.Max(0.0001,qty);
        await using var connection=connections.Create();
        await connection.OpenAsync(token);
        const string sql="""
            WITH Bom(Level,RootNo,ProNo,ElementProNo,ElementQty,BaseQty,LostRate,Path) AS (
              SELECT 1,m.PRO_NO,m.PRO_NO,d.ELEMENT_PRO_NO,d.ELEMENT_QTY,ISNULL(d.BASE_QTY,1),ISNULL(d.LOST_RATE,0),CAST(m.PRO_NO AS nvarchar(900))
              FROM dbo.BOM_STRU_M m WITH (NOLOCK)
              INNER JOIN dbo.BOM_STRU_D d WITH (NOLOCK)
                ON d.PRO_NO=m.PRO_NO AND d.EDITION=m.EDITION
              WHERE m.PRO_NO=@ProNo AND m.EDITION=(SELECT MAX(EDITION) FROM dbo.BOM_STRU_M WHERE PRO_NO=m.PRO_NO)
              UNION ALL
              SELECT b.Level+1,b.RootNo,m.PRO_NO,d.ELEMENT_PRO_NO,d.ELEMENT_QTY,ISNULL(d.BASE_QTY,1),ISNULL(d.LOST_RATE,0),CAST(b.Path+'/'+m.PRO_NO AS nvarchar(900))
              FROM Bom b
              INNER JOIN dbo.BOM_STRU_M m WITH (NOLOCK) ON m.PRO_NO=b.ElementProNo
              INNER JOIN dbo.BOM_STRU_D d WITH (NOLOCK)
                ON d.PRO_NO=m.PRO_NO AND d.EDITION=m.EDITION
              WHERE b.Level<@MaxLevel AND b.Path NOT LIKE '%/'+m.PRO_NO+'/%'
            )
            SELECT b.Level,b.ProNo,b.ElementProNo,
                   LTRIM(RTRIM(ISNULL(p.PRO_NAME,''))),LTRIM(RTRIM(ISNULL(p.PRO_SPEC,''))),
                   LTRIM(RTRIM(ISNULL(p.UNIT_ID,''))),
                   b.ElementQty,b.BaseQty,b.LostRate
            FROM Bom b
            LEFT JOIN dbo.PRODUCT p WITH (NOLOCK) ON p.PRO_NO=b.ElementProNo
            ORDER BY b.Path;
            """;
        await using var command=new SqlCommand(sql,connection);
        command.Parameters.Add("@ProNo",SqlDbType.NVarChar,60).Value=proNo;
        command.Parameters.Add("@MaxLevel",SqlDbType.Int).Value=maxLevel;
        await using var reader=await command.ExecuteReaderAsync(token);
        var rows=new List<Dictionary<string,object?>>();
        var factor=qty;
        while(await reader.ReadAsync(token))
        {
            var level=reader.GetInt32(0);
            var elementQty=Convert.ToDouble(reader.GetValue(6));
            var baseQty=Convert.ToDouble(reader.GetValue(7));
            var lostRate=Convert.ToDouble(reader.GetValue(8));
            var required=elementQty/baseQty*(1+lostRate);
            var row=new Dictionary<string,object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["LEVEL"]=level,
                ["PRO_NO"]=reader.GetString(1),
                ["ELEMENT_PRO_NO"]=reader.GetString(2),
                ["PRO_NAME"]=reader.IsDBNull(3)?"":reader.GetString(3),
                ["PRO_SPEC"]=reader.IsDBNull(4)?"":reader.GetString(4),
                ["UNIT_ID"]=reader.IsDBNull(5)?"":reader.GetString(5),
                ["ELEMENT_QTY"]=elementQty,
                ["BASE_QTY"]=baseQty,
                ["LOST_RATE"]=lostRate,
                ["REQUIRED_QTY"]=Math.Round(required*qty,4),
            };
            rows.Add(row);
        }
        return Ok(new{proNo,qty,maxLevel,rows});
    }
}
