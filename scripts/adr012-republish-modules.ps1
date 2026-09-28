param(
    [int[]]$ModuleIds = @(2914, 110103, 180102, 180105, 180110, 180111),
    [string]$ApiUrl = 'http://localhost:5261'
)

<#
    重新发布指定模块的 Definition 快照，使工作区里的校验规则配置进入运行时定义。

    校验规则（MODULE_VALIDATION_RULE）随模块发布并入 Definition JSON 的 validationRules 段，
    只有重新发布后才会被保存管线执行；本脚本逐模块发布并回读库内当前快照，确认
    duplicate-check 实例确实已随快照生效，避免"配了不跑"。

    需要 API 在线且管理员账号可登录（复用 EOS.API.Tests/AcceptanceCommon.ps1 的登录与调用封装）。
#>

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8

. (Join-Path $PSScriptRoot '..\EOS.API.Tests\AcceptanceCommon.ps1')
. (Join-Path $PSScriptRoot 'dev\eos-sql.ps1')

$session = New-EosSession -ApiUrl $ApiUrl
$response = Invoke-EosApi -Method 'POST' -Path '/api/v1/workbench-definitions/publish' `
    -Body @{ moduleIds = $ModuleIds } -Session $session -ApiUrl $ApiUrl

if ($response.Status -ne 200) {
    Write-Host ("FAIL 发布接口返回 " + $response.Status)
    if ($response.Raw) { Write-Host $response.Raw }
    exit 1
}

$failures = 0
foreach ($result in @($response.Content)) {
    # 内容未变时发布服务复用当前版本（决策 #109）：既非失败，也不产生新版本。
    $state = if ($result.published) { 'PUBLISHED' } elseif ($result.passed) { 'REUSED' } else { 'REJECTED' }
    $checks = (@($result.checks) | Where-Object { -not $_.passed } | ForEach-Object { "$($_.code):$($_.message)" }) -join '; '
    Write-Host ("  {0,-9} module={1} version={2} definition={3} {4}" -f `
        $state, $result.moduleId, $result.version, $result.definitionVersion, $checks)
    if (-not $result.published -and -not $result.passed) { $failures++ }
}

# 快照回读：确认该模块工作区里的 SAVE 期校验规则都已随发布的定义进入运行时
# （模块可能没有校验规则——如仅改模块元数据的模块——此时按"无规则"通过，而不是误判未生效）。
$live = Invoke-EosSqlQuery @"
SET NOCOUNT ON;
SELECT CONCAT(s.M_IDX, ' v', s.VERSION, ' ', ISNULL(r.VALIDATION_KEY, '(无校验规则)'), ' ', 
              CASE WHEN r.VALIDATION_KEY IS NULL THEN '-'
                   WHEN s.DEFINITION_JSON LIKE '%' + r.VALIDATION_KEY + '%' THEN 'IN'
                   ELSE 'MISSING' END)
FROM dbo.WORKBENCH_DEFINITION_SNAPSHOT s
LEFT JOIN dbo.MODULE_VALIDATION_RULE r ON r.M_IDX = s.M_IDX AND r.STAGE = N'SAVE'
WHERE s.IS_CURRENT = 1 AND s.M_IDX IN ($($ModuleIds -join ','))
ORDER BY s.M_IDX, r.VALIDATION_KEY;
"@

$missing = 0
foreach ($line in @($live)) {
    $text = ($line -join '')
    Write-Host ("  live      " + $text)
    if ($text -match 'MISSING') { $missing++ }
}

if ($failures -gt 0 -or $missing -gt 0) {
    Write-Host ("FAIL 发布失败=$failures 未生效=$missing")
    exit 1
}

Write-Host ("PASS 已发布并生效：$($ModuleIds.Count) 个模块")
exit 0
