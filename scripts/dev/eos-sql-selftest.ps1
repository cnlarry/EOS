<#
.SYNOPSIS
    eos-sql.ps1 托管回退路径的结果集回归自检。

.DESCRIPTION
    托管回退（System.Data.SqlClient）是本机唯一可用的连库路径，它的结果集读取方式一旦
    退化，调用方**不会报错，只会少拿行**：查询串里多写几个 SELECT 的脚本，看到的仍然
    是"通过"，只是后面的结果集根本没参与判定。这类假绿靠门禁自身的 `-SelfTest` 抓不到
    ——门禁的自检喂的是合成数据，不经过托管路径。

    因此这里直接断言托管路径的取数契约：
      ① 多个结果集按顺序全部返回（不是只返回第一个）；
      ② 单结果集的行数、列分隔与内容不变（回归）；
      ③ NULL 变空串；
      ④ 空结果集不产生行，也不影响其它结果集；
      ⑤ 公开入口 `Invoke-EosSqlQuery` 走托管路径时同样返回全部结果集。

    判别力：把 `Invoke-EosManagedQuery` 换回 `SqlDataAdapter.Fill(DataTable)`，
    ①④⑤ 必须变红（②③ 保持绿——它们本来就是旧实现的行为）。

.PARAMETER ConnectionString
    可选连接串；默认取 MSSQL_ERP_CONN（与仓库其它脚本一致）。

.EXAMPLE
    pwsh scripts/dev/eos-sql-selftest.ps1     # exit 0 = 托管路径契约成立
#>
[CmdletBinding()]
param(
    [string] $ConnectionString
)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8

. (Join-Path $PSScriptRoot 'eos-sql.ps1')

$failures = New-Object System.Collections.Generic.List[string]
$target = Get-EosSqlTarget -ConnectionString $ConnectionString

function Assert-ManagedRows {
    <# 执行一条查询并断言托管路径返回的行**逐个相等**（含顺序与列分隔）。 #>
    param([string] $Name, [string] $Sql, [string[]] $Expected)
    $actual = @(Invoke-EosManagedQuery -ConnectionString $target.AdoConnectionString -Sql $Sql)
    if ($actual.Count -ne $Expected.Count) {
        $failures.Add("$Name ：期望 $($Expected.Count) 行，实际 $($actual.Count) 行")
        return
    }
    for ($index = 0; $index -lt $Expected.Count; $index++) {
        if ($actual[$index] -cne $Expected[$index]) {
            $failures.Add("$Name ：第 $($index + 1) 行期望 [$($Expected[$index])]，实际 [$($actual[$index])]")
            return
        }
    }
    Write-Host "  [PASS] $Name"
}

# ① 多结果集：三个结果集必须按顺序全部返回
Assert-ManagedRows -Name '① 多结果集全部返回且顺序不变' -Sql "SELECT 'AAA' AS x; SELECT 'BBB' AS y; SELECT 'CCC' AS z;" -Expected @('AAA', 'BBB', 'CCC')

# ② 单结果集回归：行数、行序、列以制表符分隔
Assert-ManagedRows -Name '② 单结果集行数与内容不变' -Sql "SELECT 'A1' AS a, 'A2' AS b UNION ALL SELECT 'B1', 'B2';" -Expected @("A1`tA2", "B1`tB2")

# ③ NULL 变空串（整行 NULL 时该行为空串，列级 NULL 只影响该列）
Assert-ManagedRows -Name '③ NULL 变空串' -Sql "SELECT CAST(NULL AS nvarchar(10)) AS n; SELECT 'x' AS a, NULL AS b;" -Expected @('', "x`t")

# ④ 空结果集不产生行，也不吞掉后面的结果集
Assert-ManagedRows -Name '④ 空结果集不影响其它结果集' -Sql "SELECT 'x' AS v WHERE 1 = 0; SELECT 'y' AS v;" -Expected @('y')

# ⑤ 公开入口：强制走托管路径，同样返回全部结果集
#    这里刻意把 sqlcmd 判为不可用（清空探测缓存），否则探针可能落到 sqlcmd 路径上，
#    测不到本自检要守的那段实现。脚本会打印一行 [eos-sql] sqlcmd unusable，属预期。
$script:EosSqlCmd = $null
$script:EosSqlCmdProbe = @{}
$viaQuery = @(Invoke-EosSqlQuery -ConnectionString $ConnectionString -Query "SELECT 'AAA' AS x; SELECT 'BBB' AS y; SELECT 'CCC' AS z;")
if ($viaQuery.Count -ne 3 -or ($viaQuery -join ',') -cne 'AAA,BBB,CCC') {
    $failures.Add("⑤ Invoke-EosSqlQuery（托管路径）期望 3 行 AAA/BBB/CCC，实际 $($viaQuery.Count) 行：$($viaQuery -join ',')")
} else {
    Write-Host '  [PASS] ⑤ Invoke-EosSqlQuery（托管路径）返回全部结果集'
}

if ($failures.Count -gt 0) {
    Write-Host '== eos-sql 托管回退自检 =='
    foreach ($line in $failures) { Write-Host "  [FAIL] $line" }
    Write-Host '-- FAIL'
    exit 1
}

Write-Host '-- SELFTEST OK（托管回退返回全部结果集）'
exit 0
