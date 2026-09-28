# 发布工作台定义快照（开发库自助发布，替代人工在 2301 点「发布配置」）。
#
# 用途：配置表（业务动作/校验/字段元数据）改动后，影子对拍读的是
# WORKBENCH_DEFINITION_SNAPSHOT 的已发布快照，必须重新发布才生效。本脚本走与 2301
# 发布按钮相同的服务端接口（校验 → 版本递增 → 写快照 → 清脏），无需人工介入。
#
# 用法（仓库根执行）：
#   pwsh -File scripts/publish-workbench-snapshots.ps1 -ModuleIds '170102,170202'
#   pwsh -File scripts/publish-workbench-snapshots.ps1 -ModuleIds '1608,1612' -ApiUrl http://localhost:5261
#
# 前置：EOS.API 已在运行（默认 http://localhost:5261），使用开发账号 admin/admin。
# 注意：只做「发布」，不启停服务；发布失败（校验未过）时返回非 0 退出码。

param(
    [Parameter(Mandatory = $true)]
    [string]$ModuleIds,

    [string]$ApiUrl = 'http://localhost:5261',

    [string]$UserId = 'admin',

    [string]$Password = 'admin'
)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8

$ids = $ModuleIds.Split(',', [System.StringSplitOptions]::RemoveEmptyEntries) |
    ForEach-Object { [int]$_.Trim() }
if ($ids.Count -eq 0) { throw '未提供任何模块号。' }

$session = New-Object Microsoft.PowerShell.Commands.WebRequestSession
Invoke-RestMethod -Uri "$ApiUrl/api/v1/auth/login" -Method Post -ContentType 'application/json' `
    -Body (@{ userId = $UserId; password = $Password; rememberMe = $false } | ConvertTo-Json) `
    -WebSession $session | Out-Null

$response = Invoke-WebRequest -Uri "$ApiUrl/api/v1/workbench-definitions/publish" -Method Post `
    -ContentType 'application/json' -Body (@{ moduleIds = @($ids) } | ConvertTo-Json -Compress) `
    -WebSession $session -SkipHttpErrorCheck

Write-Host "HTTP $($response.StatusCode)"
$results = ($response.Content | ConvertFrom-Json)
$ok = $true
foreach ($item in $results) {
    $checks = @($item.checks | Where-Object { -not $_.passed })
    # 内容未变的模块复用当前版本（决策 #109）：published=false 但 passed=true 属成功。
    if (-not $item.passed) {
        $ok = $false
        $detail = ($checks | ForEach-Object { "$($_.code): $($_.message)" }) -join '；'
        Write-Host "FAIL 模块 $($item.moduleId) $($item.title) 未发布：$($item.error) $detail" -ForegroundColor Red
        continue
    }
    $state = if ($item.published) { 'PASS' } else { 'REUSE' }
    Write-Host "$state 模块 $($item.moduleId) $($item.title) → $($item.definitionVersion)" -ForegroundColor Green
}

if (-not $ok) { exit 1 }
exit 0
