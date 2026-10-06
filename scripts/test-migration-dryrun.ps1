<#
.SYNOPSIS
    DbUp 迁移干跑：把迁移脚本放进一层事务里执行再整体回滚，验证语法与命中行数。

.DESCRIPTION
    背景：EOS.ERP 的库对象变更走 DbUp（EOS.API/Data/Migrations/），迁移一旦有语法错误，
    服务会在启动时失败。本脚本让迁移可以在落地前先跑一次：

      1. 读入迁移文件，剥掉它自己的 COMMIT，外面包一层 BEGIN TRANSACTION ... ROLLBACK；
      2. 用 sqlcmd 执行（跨 GO 批仍属同一会话，事务与回滚都有效）；
      3. 若给了 -CheckSql，则在同一脚本前后各跑一次并比对结果——用于证明确实"执行了但没落库"。

    只读性：脚本结束时整库回滚，不改变任何数据。执行前请确认目标对象已存在（迁移依赖的表/列）。

.PARAMETER Path
    迁移文件绝对路径。

.PARAMETER CheckSql
    可选探针语句；脚本会在干跑前后各执行一次，结果不一致即判 FAIL（说明回滚没兜住）。

.PARAMETER ShowRowCounts
    额外打印迁移脚本里每条语句的"rows affected"（sqlcmd 默认不显示，开启后便于核对命中行数）。

.EXAMPLE
    pwsh scripts/test-migration-dryrun.ps1 `
      -Path 'D:\repo\EOS.API\Data\Migrations\076_clear_unreachable_aftersave_sprocs.sql' `
      -CheckSql "SELECT COUNT(*) FROM dbo.MODULES WHERE M_IDX IN (1505,2306,180218) AND EFFECT_ENGINE_TAG = 1"
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string] $Path,
    [string] $CheckSql,
    [string] $ConnectionString,
    [switch] $ShowRowCounts
)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8
. (Join-Path $PSScriptRoot 'dev\eos-sql.ps1')

if (-not [System.IO.Path]::IsPathRooted($Path)) {
    Write-Output "FAIL -Path 必须是绝对路径（大脚本经管道会给 sqlcmd 静默丢行）：$Path"
    exit 2
}
if (-not (Test-Path -LiteralPath $Path)) {
    Write-Output "FAIL 迁移文件不存在：$Path"
    exit 2
}

$sql = [System.IO.File]::ReadAllText($Path)
if ($sql -notmatch '(?im)^\s*(BEGIN\s+TRANSACTION|BEGIN\s+TRAN)') {
    Write-Output 'WARN 迁移自身没有 BEGIN TRANSACTION；干跑由外层事务兜底，安全但请确认迁移有意如此。'
}

# 静态快检：开了事务却不提交，是"启动成功但永不生效"的经典成因（见下方 @@TRANCOUNT 探针）。
$beginCount = ([regex]::Matches($sql, '(?im)^\s*BEGIN\s+(TRANSACTION|TRAN)\b')).Count
$commitCount = ([regex]::Matches($sql, '(?im)^\s*COMMIT(\s+(TRANSACTION|TRAN|WORK))?\s*;?\s*$')).Count
if ($beginCount -gt $commitCount) {
    Write-Output "WARN 静态快检：BEGIN TRANSACTION $beginCount 处 / COMMIT $commitCount 处，不配平（DbUp 外层 COMMIT 只会把 @@TRANCOUNT 减一，改动会被随后的连接归还整体回滚）。"
}

# 外层包一层事务，末尾整库回滚。
# 注意：**不要剥掉迁移自己的 COMMIT**——SQL Server 的嵌套事务里，内层 COMMIT 只把 @@TRANCOUNT
# 减一，并不会真正提交，外层 ROLLBACK 照样能兜住。剥掉反而会让"少写 COMMIT"这种缺陷失去信号：
# 那正是"每次启动都报迁移成功、改动却永远不生效"的成因，靠末尾的 @@TRANCOUNT 探针来抓。
$wrapped = "SET XACT_ABORT ON;`nBEGIN TRANSACTION;`n$sql`nSELECT 'EOS_DRYRUN_TRANCOUNT=' + CONVERT(nvarchar(10), @@TRANCOUNT);`nROLLBACK;`n"
if ($ShowRowCounts) { $wrapped = "SET NOCOUNT OFF;`n$wrapped" }

$temp = Join-Path ([System.IO.Path]::GetTempPath()) ("eos-migration-dryrun-{0}.sql" -f ([guid]::NewGuid().ToString('N')))
[System.IO.File]::WriteAllText($temp, $wrapped, (New-Object System.Text.UTF8Encoding($false)))

$target = Get-EosSqlTarget -ConnectionString $ConnectionString
Write-Output ("== 迁移干跑 ==`n  file    : {0}`n  target  : {1}/{2} (auth={3})" -f $Path, $target.Server, $target.Database, $target.Auth)

try {
    $before = $null
    if ($CheckSql) {
        $before = (Invoke-EosSqlQuery -Query $CheckSql -ConnectionString $ConnectionString) -join '|'
        Write-Output "  check前置: $before"
    }

    $run = Invoke-EosSqlFile -Path $temp -ConnectionString $ConnectionString
    $run.Output | Where-Object { $_ -match '\S' } | ForEach-Object { Write-Output "  $_" }
    if ($run.ExitCode -ne 0) {
        Write-Output "FAIL 迁移干跑报错 rc=$($run.ExitCode)（库未变更：外层事务已回滚）"
        exit 1
    }

    # 事务配平：外层 BEGIN 让 @@TRANCOUNT = 1；迁移自己的 BEGIN 未配平（少 COMMIT）时停在 2。
    # 这正是"启动报迁移成功、实际什么都没落库"的成因——DbUp 的 COMMIT 只把计数减一。
    $tranCount = $null
    foreach ($line in $run.Output) {
        if ($line -match 'EOS_DRYRUN_TRANCOUNT=(\d+)') { $tranCount = [int]$Matches[1] }
    }
    if ($null -eq $tranCount) {
        Write-Output 'WARN 未取到 @@TRANCOUNT 探针（脚本可能以 THROW/报错提前结束），跳过事务配平核对。'
    }
    elseif ($tranCount -ne 1) {
        Write-Output "FAIL 迁移自身的事务未配平：@@TRANCOUNT=$tranCount（期望 1）。少 COMMIT 会让改动连同 DbUp 的 journal 行一起被回滚，表现为'每次启动都成功却永不生效'。"
        exit 1
    }
    else {
        Write-Output '  tran    : 事务配平（@@TRANCOUNT=1）'
    }

    if ($CheckSql) {
        $after = (Invoke-EosSqlQuery -Query $CheckSql -ConnectionString $ConnectionString) -join '|'
        Write-Output "  check后置: $after"
        if ($before -ne $after) {
            Write-Output "FAIL 干跑前后探针结果不同（$before -> $after）：回滚未兜住，请检查迁移是否含 GO 外的独立事务/DDL"
            exit 1
        }
        Write-Output 'PASS 迁移可执行且整库回滚（探针前后一致）'
    }
    else {
        Write-Output 'PASS 迁移可执行（外层事务已回滚；未提供 -CheckSql 故未做落库比对）'
    }
    exit 0
}
finally {
    Remove-Item -LiteralPath $temp -Force -ErrorAction SilentlyContinue
}
