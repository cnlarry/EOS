<#
.SYNOPSIS
    ADR-024 收尾批的落库与运行态复验（重启 EOS.API 之后跑）。

.DESCRIPTION
    一次性回答"三张迁移是否落库、五张未建报表口径是否生效、注释是否订正、
    条件是否去重、报表 PDF 是否还出得来"。**只读**：不写库、不改配置、不启停服务。

    为什么要有这个脚本：这批交付里有三张迁移和一个新的渲染路径，都**要新进程才生效**。
    "重启后随手点两下"验不全——五张报表的行数、注释、去重结果都得逐条比对。

.EXAMPLE
    pwsh scripts/verify-adr024-remainder.ps1
#>
[CmdletBinding()]
param(
    [string] $ApiUrl = 'http://localhost:5261',
    [string] $UserId = 'admin',
    [string] $Password = 'admin'
)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8

. (Join-Path $PSScriptRoot 'dev\eos-sql.ps1')

$failures = [System.Collections.Generic.List[string]]::new()
function Check([string] $Name, [bool] $Ok, [string] $Detail) {
    if ($Ok) { Write-Output ("PASS {0}  {1}" -f $Name, $Detail) }
    else { Write-Output ("FAIL {0}  {1}" -f $Name, $Detail); $failures.Add($Name) }
}
# `-match` 对数组返回的是命中项而不是布尔值，直接塞给 [bool] 参数会炸——统一走这个谓词。
function Has([object[]] $Items, [string] $Pattern) {
    return (@($Items | Where-Object { "$_" -match $Pattern }).Count -gt 0)
}

Write-Output '== ① 三张迁移是否落库 =='
$applied = @(Invoke-EosSqlQuery -Query @"
SELECT ScriptName FROM dbo.ERP_SCHEMA_JOURNAL
WHERE ScriptName LIKE '%278_%' OR ScriptName LIKE '%279_%' OR ScriptName LIKE '%280_%';
"@)
Check '迁移 278 未建口径' (Has $applied '278') ($applied -join ' / ')
Check '迁移 279 条件去重' (Has $applied '279') ''
Check '迁移 280 注释订正' (Has $applied '280') ''

Write-Output ''
Write-Output '== ② REPORT_USER_STATE 表名与注释（迁移 336 由 SYSDD_REPORT 改名） =='
$oldTableName = @(Invoke-EosSqlQuery -Query "SELECT CASE WHEN OBJECT_ID('dbo.SYSDD_REPORT','U') IS NULL THEN 'gone' ELSE 'still-there' END;")
Check '旧表名 SYSDD_REPORT 已不存在' (-not (Has $oldTableName 'still-there')) ($oldTableName -join '')
$tableComment = @(Invoke-EosSqlQuery -Query @"
SELECT CAST(value AS varchar(300)) FROM sys.extended_properties
WHERE major_id = OBJECT_ID('dbo.REPORT_USER_STATE') AND minor_id = 0 AND name = 'MS_Description';
"@)
$columnComment = @(Invoke-EosSqlQuery -Query @"
SELECT CAST(ep.value AS varchar(300)) FROM sys.extended_properties ep
JOIN sys.columns c ON c.object_id = ep.major_id AND c.column_id = ep.minor_id
WHERE ep.major_id = OBJECT_ID('dbo.REPORT_USER_STATE') AND c.name = 'M_IDX' AND ep.name = 'MS_Description';
"@)
Check '表注释不再自称"报表权限"' (-not (Has $tableComment '报表权限')) ($tableComment -join '')
Check 'M_IDX 注释不再是"权限ID"' (-not (Has $columnComment '权限ID')) ($columnComment -join '')

Write-Output ''
Write-Output '== ③ 重复条件 =='
$dupTargets = @(Invoke-EosSqlQuery -Query @"
SELECT CONCAT(M_IDX, ':', COUNT(*)) FROM dbo.SYSQR_DEFAULT WITH (NOLOCK)
WHERE M_IDX IN (180102, 180105, 1803091) AND ISNULL(F_ID, '') <> ''
GROUP BY M_IDX, F_ID, FILTER_TEMPLATE HAVING COUNT(*) > 1;
"@)
Check '目标模块无重复条件' (@($dupTargets | Where-Object { $_.Trim() }).Count -eq 0) ($dupTargets -join ' ')

# 范围外模块必须一行未动（1405/1502/180214 的条件总数）
$outside = @(Invoke-EosSqlQuery -Query @"
SELECT CONCAT(M_IDX, '=', COUNT(*)) FROM dbo.SYSQR_DEFAULT WITH (NOLOCK)
WHERE M_IDX IN (1405, 1502, 180214) GROUP BY M_IDX ORDER BY M_IDX;
"@)
Write-Output ("INFO 范围外模块条件行数：{0}（本批未动）" -f ($outside -join ', '))

Write-Output ''
Write-Output '== ④ 五张未建报表：口径 + 实际行数 =='
$expected = @{
    'Product_List_nomoju'      = 2269
    'Product_List_nobom'       = 2078
    'Product_List_nokehujijia' = 1425
    'Product_List_nochsjijia'  = 1072
    'Product_List_nosample'    = 2262
}
foreach ($reportId in $expected.Keys | Sort-Object) {
    $sql = @"
SELECT CAST(COUNT(*) AS varchar(20)) FROM dbo.PRODUCT p
WHERE p.PRO_NO NOT IN (SELECT PRO_NO FROM dbo.{0});
"@
    $table = switch -Regex ($reportId) {
        'nomoju' { 'MOU_PRO_M' } 'nobom' { 'BOM_STRU_M' }
        'nokehujijia' { 'CLIENT_PRICE_D' } 'nochsjijia' { 'SUPPLIER_PRICE_D' } 'nosample' { 'SAMPLE_PRO' }
    }
    $actual = @(Invoke-EosSqlQuery -Query ($sql -f $table))
    Check ("口径 {0}" -f $reportId) ("$actual" -eq "$($expected[$reportId])") ("期望 $($expected[$reportId]) 实得 $actual")
}

Write-Output ''
Write-Output '== ⑤ 报表 PDF 端点（新渲染路径） =='
# 判别"切换是否真生效"的锚：这三张报表在**命令式实现**下的 PDF 字节数已记录在案。
# 换了渲染链路后字节数必然变化（页数 368→353 等）；若仍逐字节相等，说明跑的还是老实现。
$legacySize = @{ 'Product_List' = 2775696; 'INV_Pro_Depot_1' = 507336; 'MOC_Produce_List' = 398488 }
$session = New-Object Microsoft.PowerShell.Commands.WebRequestSession
$login = @{ userId = $UserId; password = $Password; rememberMe = $false } | ConvertTo-Json -Compress
[void](Invoke-RestMethod -Uri "$ApiUrl/api/v1/auth/login" -Method Post -ContentType 'application/json' -Body $login -WebSession $session)
$body = @{ reportId = $null; headerId = $null; tailId = $null; values = @{}; valuesTo = @{};
    sortSerialNo = $null; sortDirect = $false; showGroup = $true; showDetail = $true } | ConvertTo-Json -Depth 8 -Compress
foreach ($reportId in $legacySize.Keys | Sort-Object) {
    $encoded = [uri]::EscapeDataString($reportId)
    $response = Invoke-WebRequest -Uri "$ApiUrl/api/v1/report/$encoded/pdf" -Method Post `
        -ContentType 'application/json' -Body $body -WebSession $session -SkipHttpErrorCheck
    $size = $response.RawContentLength
    Check ("PDF {0}" -f $reportId) ([int]$response.StatusCode -eq 200 -and $size -gt 1000) `
        ("HTTP {0} / {1} 字节" -f [int]$response.StatusCode, $size)
    Check ("已切到解释层 {0}" -f $reportId) ($size -ne $legacySize[$reportId]) `
        ("命令式基线 {0} 字节 → 现 {1} 字节{2}" -f $legacySize[$reportId], $size, `
            $(if ($size -eq $legacySize[$reportId]) { '（未重启或未生效）' } else { '' }))
}

Write-Output ''
if ($failures.Count -gt 0) {
    Write-Output ("FAIL {0} 项未通过：{1}" -f $failures.Count, ($failures -join '、'))
    exit 1
}
Write-Output 'PASS ADR-024 收尾批全部复验通过。'
