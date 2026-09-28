param(
    [string]$ApiUrl = 'http://localhost:5261',
    [switch]$SkipCompare,
    [switch]$SkipLedger
)

<#
    B2 期重叠族（hr-contract 180106 / hr-safe 180107 / hr-certify 180108）落地验证串。

    前置：EOS.API 已用**含 `period-overlap` 模板的构建**重启（否则发布校验会以
    "校验规则键 'period-overlap' 不在封闭校验模板目录内" 拒绝）。

    步骤：
      1. 环境预检（库/API/构建一致性）；
      2. 确认迁移 086 已随启动应用（记账 + 三族规则行）；
      3. 重新发布三族（校验规则随发布从工作区刷新，普通发布即可）；
      4. 领域规则登记一致性门禁 + 目录三方一致性门禁；
      5. 保存侧对拍（此时三族 C# 仍在，属过渡双跑：目录与 C# 判据一致）；
      6. 账本刷新。
    之后才是「删这三族 C# 分支 → 再重启 → 再对拍（单权威）」。
#>

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8

. (Join-Path $PSScriptRoot 'dev\eos-sql.ps1')

$moduleIds = @(180106, 180107, 180108)
$failures = 0

Write-Host '== 1. 环境预检 ==' -ForegroundColor Cyan
& (Join-Path $PSScriptRoot 'probe-env.ps1') | ForEach-Object { Write-Host "  $_" }

Write-Host '== 2. 迁移 086 与三族规则行 ==' -ForegroundColor Cyan
$journal = (Invoke-EosSqlQuery "SET NOCOUNT ON; SELECT CONVERT(nvarchar(10), COUNT(*)) FROM dbo.ERP_SCHEMA_JOURNAL WHERE ScriptName LIKE '%086%';") -join ''
$ruleCount = (Invoke-EosSqlQuery "SET NOCOUNT ON; SELECT CONVERT(nvarchar(10), COUNT(*)) FROM dbo.MODULE_VALIDATION_RULE WHERE M_IDX IN (180106,180107,180108);") -join ''
$overlapCount = (Invoke-EosSqlQuery "SET NOCOUNT ON; SELECT CONVERT(nvarchar(10), COUNT(*)) FROM dbo.MODULE_VALIDATION_RULE WHERE M_IDX IN (180106,180107,180108) AND VALIDATION_KEY = N'period-overlap' AND ENABLED = 1;") -join ''
Write-Host "  journal086=$journal rules=$ruleCount enabled-period-overlap=$overlapCount"
if ($journal -ne '1' -or $ruleCount -ne '6' -or $overlapCount -ne '3') {
    Write-Host '  FAIL 迁移 086 尚未生效（确认 API 已用新构建重启）'
    $failures++
}

Write-Host '== 3. 重新发布三族 ==' -ForegroundColor Cyan
$republish = & (Join-Path $PSScriptRoot 'adr012-republish-modules.ps1') -ModuleIds $moduleIds -ApiUrl $ApiUrl *>&1
$republish | ForEach-Object { Write-Host "  $_" }
if ($LASTEXITCODE -ne 0 -or ($republish -join "`n") -match 'FAIL ') { $failures++ }

Write-Host '== 4. 结构性门禁 ==' -ForegroundColor Cyan
foreach ($script in 'check-domain-rule-registry.ps1', 'check-effect-catalog-consistency.ps1') {
    $output = & (Join-Path $PSScriptRoot $script) *>&1
    $output | Select-Object -Last 2 | ForEach-Object { Write-Host "  $_" }
    if ($LASTEXITCODE -ne 0) { $failures++ }
}

if (-not $SkipCompare) {
    Write-Host '== 5. 保存侧对拍 ==' -ForegroundColor Cyan
    $compare = & (Join-Path $PSScriptRoot '..\EOS.API.Tests\CompareAfterSave.ps1') -ApiUrl $ApiUrl *>&1
    $compare | Select-Object -Last 10 | ForEach-Object { Write-Host "  $_" }
    $text = $compare -join "`n"
    if ($text -notmatch 'CompareAfterSave 全部通过' -or $text -match '断言失败') {
        Write-Host '  FAIL 对拍未全部通过'
        $failures++
    }
}

if (-not $SkipLedger) {
    Write-Host '== 6. 账本刷新 ==' -ForegroundColor Cyan
    $ledger = & (Join-Path $PSScriptRoot 'adr012-acceptance-ledger.ps1') *>&1
    ($ledger | Select-String -Pattern '###|dual-path valid|stale|failure branch|dual blocked|unobtainable' |
        Select-Object -First 12) | ForEach-Object { Write-Host "  $($_.Line.Trim())" }
    if ($LASTEXITCODE -ne 0) { $failures++ }
}

if ($failures -gt 0) {
    Write-Host "FAIL B2 验证串有 $failures 步未通过"
    exit 1
}
Write-Host 'PASS B2 期重叠族验证串全部通过（此后可删三族 C# 分支并复验单权威）'
exit 0
