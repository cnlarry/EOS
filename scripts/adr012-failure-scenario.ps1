<#
.SYNOPSIS
    ADR-012 失败分支场景跑批：Status → Setup → 影子失败运行 → Teardown → Status 复核零残留。

.DESCRIPTION
    失败分支（ADR-012 三态对拍的第三态）验的是「引擎在任何写入前阻断、库内零残留」，
    其判据与成功路径不同，需要专门的违规夹具。本脚本把五步流程固定下来，避免每轮手敲：

      1. Status   前置核对（夹具未生效、目标单号不存在）
      2. Setup    造违规单据（夹具内自带预像表）
      3. 影子运行  EOS_SHADOW_RUN=1 EOS_SHADOW_FAILURE=1 + 模块/单号/事件，跑 FailureCase 测试
      4. Teardown 清理并自检零残留
      5. Status   后置复核

    夹具位于 logs/adr012-acceptance/fixture/（不入库，随仓库工作区保留）；
    夹具内的 `$(Mode)` 占位由本脚本替换后执行，故无需 sqlcmd -v。

.EXAMPLE
    pwsh scripts/adr012-failure-scenario.ps1 -FixtureName inv-loan-stock-failure-fixture.sql `
      -Module 130108 -Keys 'ADR12|ADR012LOANFAIL1' -RunId shadow-130108-20260915-001

.EXAMPLE
    pwsh scripts/adr012-failure-scenario.ps1 -FixtureName cop-receipt-bank-deapprove-failure-fixture.sql `
      -Module 170102 -Keys 'ADR12|ADR012RECFAIL01' -Event DEAPPROVE
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string] $FixtureName,
    [Parameter(Mandatory = $true)][int] $Module,
    [Parameter(Mandatory = $true)][string] $Keys,
    [ValidateSet('APPROVE_EFFECT', 'DEAPPROVE')][string] $Event = 'APPROVE_EFFECT',
    [string] $RunId,
    [string] $FixtureDir,
    [string] $TestsDll = "$env:TEMP\adr012-tests\EOS.API.Tests.dll"
)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
if (-not $FixtureDir) { $FixtureDir = Join-Path $root 'logs\adr012-acceptance\fixture' }
. (Join-Path $root 'scripts\dev\eos-sql.ps1')

$fixture = Join-Path $FixtureDir $FixtureName
if (-not (Test-Path -LiteralPath $fixture)) { throw "夹具不存在：$fixture" }
if (-not (Test-Path -LiteralPath $TestsDll)) { throw "测试程序集不存在：$TestsDll（先 dotnet build EOS.API.Tests -o 该目录）" }
if (-not $RunId) { $RunId = "shadow-$Module-$(Get-Date -Format yyyyMMdd)-f01" }

function Invoke-FixtureMode {
    param([string] $Mode)
    $tmp = Join-Path ([System.IO.Path]::GetTempPath()) ("eos-fix-{0}-{1}.sql" -f $Mode, ([guid]::NewGuid().ToString('N')))
    $sql = [System.IO.File]::ReadAllText($fixture).Replace('$(Mode)', $Mode)
    [System.IO.File]::WriteAllText($tmp, $sql, (New-Object System.Text.UTF8Encoding($false)))
    try {
        $run = Invoke-EosSqlFile -Path $tmp
        $run.Output | Where-Object { $_ -match '\S' } | ForEach-Object { Write-Output "    $_" }
        if ($run.ExitCode -ne 0) { throw "夹具 $Mode 失败 rc=$($run.ExitCode)" }
    }
    finally { Remove-Item -LiteralPath $tmp -Force -ErrorAction SilentlyContinue }
}

Write-Output "== [$Module] Status（前置）";  Invoke-FixtureMode 'Status'
Write-Output "== [$Module] Setup";            Invoke-FixtureMode 'Setup'

Write-Output "== [$Module] 影子失败运行（failure=1 event=$Event keys=$Keys）"
$env:EOS_SHADOW_RUN = '1'
$env:EOS_SHADOW_FAILURE = '1'
$env:EOS_SHADOW_MODULE = [string]$Module
$env:EOS_SHADOW_KEYS = $Keys
$env:EOS_SHADOW_EVENT = $Event
$env:EOS_SHADOW_RUN_ID = $RunId
try {
    $testOut = & dotnet vstest $TestsDll --TestCaseFilter:"FullyQualifiedName~FailureCase" 2>&1
    $testOut | Where-Object { $_ -match 'failure-case|已通过|失败:|Failed|Passed|error' } | ForEach-Object { Write-Output "    $_" }
    if ($testOut -match '失败:\s*[1-9]') { throw "影子失败运行未通过（详见上）" }
}
finally {
    $env:EOS_SHADOW_RUN = $null; $env:EOS_SHADOW_FAILURE = $null; $env:EOS_SHADOW_MODULE = $null
    $env:EOS_SHADOW_KEYS = $null; $env:EOS_SHADOW_EVENT = $null; $env:EOS_SHADOW_RUN_ID = $null
}

Write-Output "== [$Module] Teardown";        Invoke-FixtureMode 'Teardown'
Write-Output "== [$Module] Status（后置复核）"; Invoke-FixtureMode 'Status'

$report = Join-Path $root "logs\shadow\$RunId.json"
if (Test-Path -LiteralPath $report) {
    $j = Get-Content -Raw -Encoding UTF8 $report | ConvertFrom-Json
    Write-Output ("REPORT {0}: verdict={1} old={2} new={3} tables={4}" -f $RunId, $j.summary.verdict, $j.oldPath.status, $j.newPath.status, @($j.tables).Count)
    Write-Output ("  oldErr={0}" -f ((($j.oldPath.error ?? '') -replace "`r?`n", ' ').Trim()))
    Write-Output ("  newErr={0}" -f ((($j.newPath.error ?? '') -replace "`r?`n", ' ').Trim()))
    $class = if ($j.oldPath.status -eq 'blocked' -and $j.newPath.status -eq 'blocked') { 'DUAL' }
             elseif ($j.oldPath.status -eq 'skipped') { 'ENGINE_ONLY' }
             else { 'LEGACY_MISSING' }
    Write-Output "  class=$class"
    if ($class -eq 'DUAL') { Write-Output 'PASS 双路阻断（可计入失败分支覆盖）' } else { Write-Output "WARN class=$class（不计入双路失败覆盖）" }
}
else { Write-Output "WARN 未找到报告：$report" }
