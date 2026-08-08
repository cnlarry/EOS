using System.Data;
using System.Security.Claims;
using EOS.API.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;

namespace EOS.API.Controllers;

[ApiController]
[Authorize]
[Route("api/settings")]
public sealed class SettingsController(DbConnectionFactory connections) : ControllerBase
{
    [HttpGet("system")]
    public async Task<IActionResult> GetSystemSettings(CancellationToken token)
    {
        if(!await CanSetupAsync(token))return Forbid();
        await using var connection=connections.Create();
        await connection.OpenAsync(token);
        await using var command=new SqlCommand("SELECT TOP 1 * FROM dbo.SYSSS WITH (NOLOCK);",connection);
        await using var reader=await command.ExecuteReaderAsync(token);
        if(!await reader.ReadAsync(token))return NotFound();
        var result=new Dictionary<string,object?>(StringComparer.OrdinalIgnoreCase);
        for(var i=0;i<reader.FieldCount;i++)
            result[reader.GetName(i)]=reader.IsDBNull(i)?null:reader.GetValue(i);
        return Ok(result);
    }

    [HttpPut("system")]
    public async Task<IActionResult> UpdateSystem([FromBody]Dictionary<string,string?> values,CancellationToken token)
    {
        if(!await CanSetupAsync(token))return Forbid();
        if(values.Count>100)return BadRequest(new{code="TOO_MANY_FIELDS",message="参数数量超出限制。"});
        await using var connection=connections.Create();
        await connection.OpenAsync(token);
        var columns=await GetColumnsAsync(connection,token);
        var sets=new List<string>();
        await using var command=new SqlCommand();
        command.Connection=connection;
        foreach(var (key,raw) in values)
        {
            if(!columns.TryGetValue(key,out var dataType))continue;
            var parameter=$"@s{command.Parameters.Count}";
            sets.Add($"[{key}]={parameter}");
            command.Parameters.AddWithValue(parameter,Normalize(dataType,raw));
        }
        if(sets.Count==0)return BadRequest(new{code="NO_VALID_FIELDS",message="没有可更新的参数。"});
        command.CommandText=$"UPDATE dbo.SYSSS SET {string.Join(',',sets)};";
        await command.ExecuteNonQueryAsync(token);
        return NoContent();
    }

    private async Task<bool> CanSetupAsync(CancellationToken token)
    {
        var userId=User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if(userId is null)return false;
        await using var connection=connections.Create();
        await connection.OpenAsync(token);
        const string sql="""
            SELECT TOP 1 1 FROM dbo.SYSDD WITH (NOLOCK)
            WHERE USER_ID=@UserId AND M_IDX=110111 AND LTRIM(RTRIM(ISNULL(EXEC_TAG,'')))<>'A' AND SETUP_TAG=1;
            """;
        await using var command=new SqlCommand(sql,connection);
        command.Parameters.Add("@UserId",SqlDbType.NChar,10).Value=userId.Trim();
        return await command.ExecuteScalarAsync(token) is not null;
    }

    private static async Task<Dictionary<string,string>> GetColumnsAsync(SqlConnection connection,CancellationToken token)
    {
        const string sql="""
            SELECT COLUMN_NAME,DATA_TYPE FROM INFORMATION_SCHEMA.COLUMNS
            WHERE TABLE_SCHEMA='dbo' AND TABLE_NAME='SYSSS';
            """;
        await using var command=new SqlCommand(sql,connection);
        await using var reader=await command.ExecuteReaderAsync(token);
        var result=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        while(await reader.ReadAsync(token))result[reader.GetString(0)]=reader.GetString(1);
        return result;
    }

    private static object Normalize(string dataType,string? raw)
    {
        if(string.IsNullOrWhiteSpace(raw))return DBNull.Value;
        var type=dataType.ToLowerInvariant();
        try
        {
            if(type.Contains("bit",StringComparison.Ordinal))
                return raw.Equals("true",StringComparison.OrdinalIgnoreCase)||raw=="1"||raw=="是";
            if(type.Contains("int",StringComparison.Ordinal))
                return int.Parse(raw,System.Globalization.CultureInfo.InvariantCulture);
            if(type.Contains("float",StringComparison.Ordinal)||type.Contains("real",StringComparison.Ordinal)
               ||type.Contains("decimal",StringComparison.Ordinal)||type.Contains("numeric",StringComparison.Ordinal))
                return decimal.Parse(raw,System.Globalization.CultureInfo.InvariantCulture);
            if(type.Contains("datetime",StringComparison.Ordinal)||type.Contains("date",StringComparison.Ordinal))
                return DateTime.Parse(raw,System.Globalization.CultureInfo.InvariantCulture);
            return raw;
        }
        catch{return raw;}
    }
}
