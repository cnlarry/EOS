#Requires -Version 7.0
<#
.SYNOPSIS
ADR-006 步骤 5：存量统一表单白名单模块批量 validate，产出机器可读报告 + 豁免清单。
.DESCRIPTION
对 appsettings.json UnifiedFormEditor.EnabledModuleIds 全量调用
POST /api/v1/workbench-definitions/validate（2306 CanSetup 门），聚合为：
- logs/goal/form-rework/validate-report-<ts>.json   （逐模块逐 check 明细）
- logs/goal/form-rework/validate-summary-<ts>.csv   （module,title,passed,errors,warnings）
- logs/goal/form-rework/exemptions.json             （阻断级未过项豁免登记骨架）
控制台输出汇总：总数/通过/阻断未过/警告模块数。
.EXAMPLE
.\scripts\batch-validate-form-rework.ps1
#>
param(
    [string]$ApiUrl = 'http://localhost:5261'
)
$ErrorActionPreference = 'Stop'
Set-Location (Join-Path $PSScriptRoot '..')
. (Join-Path $PSScriptRoot '..\EOS.API.Tests\AcceptanceCommon.ps1')

$session = New-EosSession -ApiUrl $ApiUrl -UserId 'admin' -Password 'admin'
$whitelist = Get-EosWhitelist
Write-Host "白名单模块数：$($whitelist.Count)"

$results = [System.Collections.Generic.List[object]]::new()
$index = 0
foreach ($id in $whitelist) {
    $index++
    $body = @{ moduleId = [int]$id } | ConvertTo-Json -Compress
    $response = Invoke-RestMethod -Uri "$ApiUrl/api/v1/workbench-definitions/validate" -Method Post `
        -ContentType 'application/json' -Body $body -WebSession $session -SkipHttpErrorCheck
    if ($response -is [string]) { $response = $response | ConvertFrom-Json }
    $checks = @($response.checks)
    $errors = @($checks | Where-Object { $_.severity -ne 'warning' -and -not $_.passed } | ForEach-Object { $_.code })
    $warnings = @($checks | Where-Object { $_.severity -eq 'warning' } | ForEach-Object { $_.code })
    $results.Add([pscustomobject]@{
        moduleId = [int]$id; title = [string]$response.title; passed = [bool]$response.passed
        errors = $errors; warnings = $warnings; checks = $checks
    })
    if ($index % 25 -eq 0) { Write-Host "  … $index/$($whitelist.Count)" }
}

$timestamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$outDir = Join-Path (Get-Location) 'logs\goal\form-rework'
New-Item -ItemType Directory -Force -Path $outDir | Out-Null

$full = Join-Path $outDir "validate-report-$timestamp.json"
$results | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $full -Encoding utf8

$summary = $results | Select-Object moduleId, title, passed,
    @{ n = 'errors'; e = { ($_.errors -join ';') } },
    @{ n = 'warnings'; e = { ($_.warnings -join ';') } }
$summaryPath = Join-Path $outDir "validate-summary-$timestamp.csv"
$summary | Export-Csv -LiteralPath $summaryPath -NoTypeInformation -Encoding utf8

$blocked = @($results | Where-Object { -not $_.passed })
$warned = @($results | Where-Object { $_.warnings.Count -gt 0 })
$exemptions = [pscustomobject]@{
    generatedAt = (Get-Date).ToString('s')
    note        = 'ADR-006 步骤 5 豁免登记：阻断级未过项（不影响保存正确性的历史元数据问题），随逐模块验收消化。'
    modules     = @($blocked | ForEach-Object { [pscustomobject]@{ moduleId = $_.moduleId; title = $_.title; errors = $_.errors } })
}
$exemptPath = Join-Path $outDir 'exemptions.json'
$exemptions | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $exemptPath -Encoding utf8

Write-Host ''
Write-Host ("总模块：{0}；通过：{1}；阻断未过：{2}；含警告：{3}" -f `
    $results.Count, ($results.Count - $blocked.Count), $blocked.Count, $warned.Count)
if ($blocked.Count -gt 0) {
    Write-Host '阻断未过明细：' -ForegroundColor Yellow
    $blocked | ForEach-Object { Write-Host ("  {0} {1} → {2}" -f $_.moduleId, $_.title, ($_.errors -join ',')) }
}
Write-Host "报告：$full"
Write-Host "摘要：$summaryPath"
Write-Host "豁免：$exemptPath"
