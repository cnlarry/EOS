<#
.SYNOPSIS
    按清理脚本留下的备份文件，把占位公式行**逐列按原值**插回。

.DESCRIPTION
    回退保真：
      · 逐列按原值插入（含 NULL 与空串的区分——两者在结构列上语义不同）；
      · OP_ID 用 SET IDENTITY_INSERT 保真（OP_ID 是 IDENTITY 列，库内没有任何外键引用它，
        但保真后可与备份逐列核对，不必依赖"换个 ID 也语义等价"的判断）。
    幂等：按 OP_ID 判存在，已存在的行跳过；重复执行插 0 行。
    插回后逐列读回比对，任何一列与备份不同即报错——不允许"插了但插错"。

.PARAMETER BackupFile
    clean-placeholder-formula-rows.ps1 生成的备份文件。

.PARAMETER ConnectionString
    连接串；缺省用 eos-sql.ps1 的解析逻辑。

.EXAMPLE
    pwsh scripts/restore-placeholder-formula-rows.ps1 -BackupFile logs/placeholder-op-rows-20260925-193000.json
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string] $BackupFile,
    [string] $ConnectionString
)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8

. (Join-Path $PSScriptRoot 'dev/eos-sql.ps1')
$target = Get-EosSqlTarget -ConnectionString $ConnectionString

if (-not (Test-Path -LiteralPath $BackupFile)) { throw "备份文件不存在：$BackupFile" }
$payload = Get-Content -LiteralPath $BackupFile -Raw -Encoding UTF8 | ConvertFrom-Json
$rows = @($payload.rows)
if ($rows.Count -eq 0) {
    Write-Host "备份里没有行（$BackupFile）：无需回退。"
    return
}

# 列清单与类型；字符串列一律按 nvarchar(max) 传参，避免截断
$columns = [ordered]@{
    opId            = 'bigint'
    actionId        = 'bigint'
    opSeq           = 'int'
    targetTable     = 'string'
    targetField     = 'string'
    opCode          = 'string'
    sourceScope     = 'string'
    sourceTable     = 'string'
    sourceField     = 'string'
    sourceAgg       = 'string'
    sourceConstant  = 'string'
    sourceTerms     = 'string'
    matchStruct     = 'string'
    conditionStruct = 'string'
    remark          = 'string'
}
$insertColumns = 'OP_ID,ACTION_ID,OP_SEQ,TARGET_TABLE,TARGET_FIELD,OP_CODE,SOURCE_SCOPE,SOURCE_TABLE,SOURCE_FIELD,SOURCE_AGG,SOURCE_CONSTANT,SOURCE_TERMS_STRUCT,MATCH_STRUCT,CONDITION_STRUCT,REMARK'
$insertValues = '@opId,@actionId,@opSeq,@targetTable,@targetField,@opCode,@sourceScope,@sourceTable,@sourceField,@sourceAgg,@sourceConstant,@sourceTerms,@matchStruct,@conditionStruct,@remark'

function Add-Parameters {
    param($Command, $Row)

    foreach ($name in $columns.Keys) {
        $type = $columns[$name]
        $parameter = if ($type -eq 'string') {
            $Command.Parameters.Add("@$name", [System.Data.SqlDbType]::NVarChar, -1)
        } elseif ($type -eq 'int') {
            $Command.Parameters.Add("@$name", [System.Data.SqlDbType]::Int)
        } else {
            $Command.Parameters.Add("@$name", [System.Data.SqlDbType]::BigInt)
        }
        $value = $Row.$name
        $parameter.Value = if ($null -eq $value) {
            [DBNull]::Value
        } elseif ($type -eq 'string') {
            [string] $value
        } elseif ($type -eq 'int') {
            [int] $value
        } else {
            [long] $value
        }
    }
}

$connection = New-Object System.Data.SqlClient.SqlConnection $target.AdoConnectionString
$connection.Open()
try {
    $inserted = 0
    $skipped = 0
    foreach ($row in $rows) {
        $check = $connection.CreateCommand()
        $check.CommandText = 'SELECT COUNT(*) FROM dbo.MODULE_BUSINESS_ACTION_OP WHERE OP_ID=@opId;'
        $checkParameter = $check.Parameters.Add('@opId', [System.Data.SqlDbType]::BigInt)
        $checkParameter.Value = [long] $row.opId
        if ([int] $check.ExecuteScalar() -gt 0) {
            $skipped++
            continue
        }

        # IDENTITY_INSERT 必须在与 INSERT 同一个会话里开关，因此放在同一批
        $insert = $connection.CreateCommand()
        $insert.CommandText = "SET IDENTITY_INSERT dbo.MODULE_BUSINESS_ACTION_OP ON; INSERT INTO dbo.MODULE_BUSINESS_ACTION_OP ($insertColumns) VALUES ($insertValues); SET IDENTITY_INSERT dbo.MODULE_BUSINESS_ACTION_OP OFF;"
        Add-Parameters -Command $insert -Row $row
        [void] $insert.ExecuteNonQuery()
        $inserted++

        # 读回逐列比对：插回必须与备份一模一样。
        # 列名按备份里的键名起别名，否则按 $row.<键> 取不到值——读回会全是空，比对形同虚设。
        $read = $connection.CreateCommand()
        $read.CommandText = "SELECT OP_ID AS opId, ACTION_ID AS actionId, OP_SEQ AS opSeq, TARGET_TABLE AS targetTable, TARGET_FIELD AS targetField, OP_CODE AS opCode, SOURCE_SCOPE AS sourceScope, SOURCE_TABLE AS sourceTable, SOURCE_FIELD AS sourceField, SOURCE_AGG AS sourceAgg, SOURCE_CONSTANT AS sourceConstant, SOURCE_TERMS_STRUCT AS sourceTerms, MATCH_STRUCT AS matchStruct, CONDITION_STRUCT AS conditionStruct, REMARK AS remark FROM dbo.MODULE_BUSINESS_ACTION_OP WHERE OP_ID=@opId;"
        $readParameter = $read.Parameters.Add('@opId', [System.Data.SqlDbType]::BigInt)
        $readParameter.Value = [long] $row.opId
        $reader = $read.ExecuteReader()
        [void] $reader.Read()
        $mismatch = @()
        foreach ($name in $columns.Keys) {
            $actual = $reader[$name]
            $expected = $row.$name
            $same = if ($null -eq $expected) { $actual -is [DBNull] } else { -not ($actual -is [DBNull]) -and ([string] $actual -ceq [string] $expected) }
            if (-not $same) { $mismatch += "$name=[$actual] 期望=[$expected]" }
        }
        $reader.Close()
        if ($mismatch.Count -gt 0) {
            throw "OP_ID=$($row.opId) 插回后与备份不一致：$($mismatch -join '；')"
        }
    }
} finally {
    $connection.Close()
}

Write-Host "回退完成：插入 $inserted 行，跳过（已存在）$skipped 行。备份文件 $BackupFile"
$modules = @($rows | ForEach-Object { [int] $_.moduleId } | Sort-Object -Unique)
Write-Host ("涉及模块：{0}；插回的行已逐列与备份核对一致。" -f ($modules -join ','))
Write-Host ('提示：这些模块的库内配置又回到清理前的样子，发布侧下一次重发布将按语义等价复用当前版本。')
