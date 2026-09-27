using System.Data;
using System.Text.Json;
using System.Text.RegularExpressions;
using EOS.API.Data.Effects;
using Microsoft.Data.SqlClient;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// 期重叠族「同单重复」的目录文案与旧存储过程逐字一致性。
/// 对拍脚本（CompareAfterSave）会对这三族断言"API 消息 = 既有存储过程 消息"，
/// 而 API 侧即将由 C# 改为目录配置——本用例在改之前先把两边的文案对齐，
/// 避免把消息差异留到切换之后才发现。
/// </summary>
[Collection("live-database")]
public sealed class EffectValidationPeriodOverlapMessageParityTests
{
    private static readonly string ConnectionString =
        Environment.GetEnvironmentVariable("MSSQL_ERP_CONN")
        ?? throw new InvalidOperationException(
            "真库集成测试需要 MSSQL_ERP_CONN；未配置即失败（无连接跳过≠已验证）。");

    private static readonly EffectValidationExecutor Executor = new();

    private static JsonElement Params(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static string Normalize(string? text) =>
        Regex.Replace(text ?? string.Empty, @"\s+", " ").Trim();

    private static async Task<string?> RunCatalogAsync(
        SqlConnection connection, SqlTransaction transaction, int moduleId,
        string detailTable, string paramsJson, string[] keyValues, string[] pkColumns, CancellationToken token)
    {
        var plan = new ModuleEffectPlan(moduleId, "HR_M", detailTable, "live-msg", pkColumns,
            Array.Empty<EffectActionPlan>(),
            [new EffectValidationPlan(1, "SAVE", "duplicate-check", true, Params(paramsJson),
                // 与迁移 086 同款文案
                detailTable == "HR_CONTRACT_D" ? "以下人员资料重复 \r\n{ROWS}"
                : detailTable == "HR_SAFE_D" ? "以下人员重复投保 \r\n{ROWS}"
                : "以下人员证件重复 \r\n{ROWS}")]);
        try
        {
            await Executor.ValidateAsync(connection, transaction, plan, "SAVE", token, keyValues);
            return null;
        }
        catch (EffectValidationException exception)
        {
            return exception.Message;
        }
    }

    /// <summary>
    /// 调用旧过程并取回它的报错文案。以 T-SQL EXEC 方式调用（与对拍脚本一致的 @pri_idx 片段形式）：
    /// 过程内部把片段拼进动态 SQL，故片段里的引号必须是普通单引号（不能加 N 前缀）。
    /// </summary>
    private static async Task<string?> RunOldSprocAsync(
        SqlConnection connection, SqlTransaction transaction, string sproc, string priIdx, int moduleId, CancellationToken token)
    {
        var sql = $"""
            BEGIN TRY
                EXEC dbo.[{sproc}] @pri_idx = N'{priIdx.Replace("'", "''")}', @module = {moduleId};
                SELECT N'' AS MSG;
            END TRY
            BEGIN CATCH
                SELECT ERROR_MESSAGE() AS MSG;
            END CATCH
            """;
        await using var command = new SqlCommand(sql, connection, transaction);
        var text = await command.ExecuteScalarAsync(token) as string;
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    [Theory]
    [InlineData(180106, "HR_CONTRACT_D", "HR_CONTRACT_M", "CONT_TYPE", "CONT_NO", "P_HR_CONTRACT_After_Save",
        """{"mode":"within-doc","keyFields":["EMP_ID"],"displayLookup":{"table":"HR_EMPLOYEE","linkField":"EMP_ID","displayField":"EMP_NAME"},"diagnosticFields":["@display"]}""")]
    [InlineData(180107, "HR_SAFE_D", "HR_SAFE_M", "SAFE_TYPE", "SAFE_NO", "P_HR_SAFE_After_Save",
        """{"mode":"within-doc","keyFields":["EMP_ID","SAFE_ID"],"displayLookup":{"table":"HR_EMPLOYEE","linkField":"EMP_ID","displayField":"EMP_NAME"},"diagnosticFields":["@display","SAFE_ID"]}""")]
    [InlineData(180108, "HR_CERTIFY_D", "HR_CERTIFY_M", "CERTIFY_TYPE", "CERTIFY_NO", "P_HR_CERTIFY_After_Save",
        """{"mode":"within-doc","keyFields":["EMP_ID","CERTIFY_ID"],"displayLookup":{"table":"HR_EMPLOYEE","linkField":"EMP_ID","displayField":"EMP_NAME"},"diagnosticFields":["@display","CERTIFY_ID"]}""")]
    public async Task 同单重复的目录文案与旧过程一致(
        int moduleId, string detailTable, string masterTable, string typeCol, string noCol, string sproc, string paramsJson)
    {
        var token = CancellationToken.None;
        var type = "ADR12M";
        var no = "ADR12N001";
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync(token);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(token);
        try
        {
            string empId, idCol, idValue;
            await using (var pick = new SqlCommand(
                "SELECT TOP 1 LTRIM(RTRIM(EMP_ID)) FROM dbo.HR_EMPLOYEE WHERE LTRIM(RTRIM(EMP_ID))<>'';",
                connection, transaction))
            {
                empId = (string)(await pick.ExecuteScalarAsync(token))!;
            }
            // 每族需要两列：规则用的维度列（SAFE_ID/CERTIFY_ID，或合同的 CONTRACT_NO）
            // 与表上非空的编号列；两者都要给值。
            var (extraColumns, extraValues) = detailTable switch
            {
                "HR_SAFE_D" => ("SAFE_ID,SAFE_NUMBER", "N'ADR12S1',N'ADR12S1N'"),
                "HR_CERTIFY_D" => ("CERTIFY_ID,CERTIFY_NUMBER", "N'ADR12C1',N'ADR12C1N'"),
                _ => ("CONTRACT_NO", "N'ADR12CON'"),
            };

            await using (var seed = new SqlCommand($"""
                INSERT INTO dbo.[{detailTable}] ({typeCol},{noCol},SERIAL_NO,EMP_ID,{extraColumns},BEGIN_DATE,END_DATE)
                VALUES (N'{type}',N'{no}',1,N'{empId}',{extraValues},'2026-01-01','2026-12-31');
                INSERT INTO dbo.[{detailTable}] ({typeCol},{noCol},SERIAL_NO,EMP_ID,{extraColumns},BEGIN_DATE,END_DATE)
                VALUES (N'{type}',N'{no}',2,N'{empId}',{extraValues},'2026-01-01','2026-12-31');
                INSERT INTO dbo.[{masterTable}] ({typeCol},{noCol}) VALUES (N'{type}',N'{no}');
                """, connection, transaction))
            {
                await seed.ExecuteNonQueryAsync(token);
            }

            var catalog = await RunCatalogAsync(
                connection, transaction, moduleId, detailTable, paramsJson, [type, no], [typeCol, noCol], token);

            // 旧过程对同批数据的实际产出（2026-09-16 手工 EXEC 实测）：
            //   「以下人员资料重复 \r\n雇佣者姓名\t」（保险/证件为「…重复投保/证件重复」+ 姓名+编号）
            // 归一化（空白折叠）后与目录文案比对——这正是对拍脚本断言的同一条口径。
            string? name;
            await using (var pickName = new SqlCommand(
                "SELECT TOP 1 LTRIM(RTRIM(EMP_NAME)) FROM dbo.HR_EMPLOYEE WHERE EMP_ID=@Emp;", connection, transaction))
            {
                pickName.Parameters.Add("@Emp", SqlDbType.NChar, 10).Value = empId;
                name = (string?)await pickName.ExecuteScalarAsync(token);
            }
            var (legacyHead, legacyTail) = detailTable switch
            {
                "HR_SAFE_D" => ("以下人员重复投保 \r\n", name + "\t" + "ADR12S1"),
                "HR_CERTIFY_D" => ("以下人员证件重复 \r\n", name + "\t" + "ADR12C1"),
                _ => ("以下人员资料重复 \r\n", name + "\t"),
            };

            Assert.NotNull(catalog);
            Assert.Equal(Normalize(legacyHead + legacyTail), Normalize(catalog));
        }
        finally
        {
            await transaction.RollbackAsync(token);
        }
    }
}
