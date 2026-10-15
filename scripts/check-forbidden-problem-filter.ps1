#!/usr/bin/env pwsh
#Requires -Version 7
<#
.SYNOPSIS
    拒绝出口门禁：保证 403 权限拒绝始终带结构化错误体。

.DESCRIPTION
    控制器里裸写的 Forbid() 默认返回空响应体，会导致「界面报错、文件日志查不到」
    （缺少 error.code 时请求日志只记 Information，而文件日志仅保留 Warning+）。
    ForbiddenProblemFilter 把这层统一兜住，因此真正脆弱的不是那两百多处调用点，
    而是 Program.cs 里这一行注册——一旦被误删或顺序颠倒，全站拒绝会静默退回空体。

    检查三条：
      1. Program.cs 已注册 ForbiddenProblemFilter；
      2. 其注册顺序早于 ProblemDetailsContextFilter（先产出 ProblemDetails，
         再由后者附加请求上下文并登记错误码）；
      3. EOS.API 下不得新增绕过统一出口的裸 403 状态码返回（无响应体）。

.PARAMETER SelfTest
    用合成内容做正反自检，不读取仓库文件。

.EXAMPLE
    pwsh scripts/check-forbidden-problem-filter.ps1
    pwsh scripts/check-forbidden-problem-filter.ps1 -SelfTest
#>
param(
    [switch]$SelfTest
)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8

$script:Failed = 0

function Write-Check {
    param([string]$Status, [string]$Name, [string]$Detail = '')
    if ($Status -eq 'FAIL') { $script:Failed++ }
    $line = "  [$Status] $Name"
    if ($Detail) { $line = "$line — $Detail" }
    Write-Output $line
}

function Get-FirstMatchLine {
    param([string]$Content, [string]$Pattern)
    $lines = $Content -split "`r?`n"
    for ($i = 0; $i -lt $lines.Count; $i++) {
        # 注释行不算注册：否则把注册注释掉后门禁仍判 PASS（假绿），判别性不成立
        if ($lines[$i].TrimStart().StartsWith('//')) { continue }
        if ($lines[$i] -match $Pattern) { return $i + 1 }
    }
    return $null
}

<#
    判定一段 Program.cs 内容里的过滤器注册是否符合要求。
    返回 @{ Registered = bool; OrderOk = bool }
#>
function Test-FilterRegistration {
    param([string]$Content)
    $forbidLine = Get-FirstMatchLine -Content $Content -Pattern 'Add<ForbiddenProblemFilter>\s*\('
    $contextLine = Get-FirstMatchLine -Content $Content -Pattern 'Add<ProblemDetailsContextFilter>\s*\('
    return @{
        Registered = $null -ne $forbidLine
        OrderOk    = ($null -ne $forbidLine -and $null -ne $contextLine -and $forbidLine -lt $contextLine)
    }
}

# 裸 403：StatusCode 只给状态码、不给响应体，绕过统一错误出口
$NakedForbidPattern = 'StatusCode\s*\(\s*(StatusCodes\.Status403Forbidden|403)\s*\)'

function Test-NoNakedForbidden {
    param([string]$Root)
    $violations = @()
    $files = Get-ChildItem -Path (Join-Path $Root 'EOS.API') -Recurse -Filter '*.cs' -File -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -notmatch '[\\/](bin|obj)[\\/]' }
    foreach ($file in $files) {
        $lines = Get-Content -LiteralPath $file.FullName -Encoding utf8
        for ($i = 0; $i -lt $lines.Count; $i++) {
            if ($lines[$i].TrimStart().StartsWith('//')) { continue }
            if ($lines[$i] -match $NakedForbidPattern) {
                $relative = $file.FullName.Substring($Root.Length).TrimStart('\', '/')
                $violations += "$relative`:$($i + 1)"
            }
        }
    }
    return $violations
}

if ($SelfTest) {
    Write-Output '== 拒绝出口门禁 自检 =='

    $good = @'
builder.Services.AddControllers(options =>
{
    options.Filters.Add<ApiExceptionFilter>();
    options.Filters.Add<ForbiddenProblemFilter>();
    options.Filters.Add<ProblemDetailsContextFilter>();
});
'@
    $r = Test-FilterRegistration -Content $good
    if ($r.Registered -and $r.OrderOk) { Write-Check 'PASS' '自检：正确注册且顺序在前' }
    else { Write-Check 'FAIL' '自检：正确注册且顺序在前' }

    $missing = @'
builder.Services.AddControllers(options =>
{
    options.Filters.Add<ProblemDetailsContextFilter>();
});
'@
    $r = Test-FilterRegistration -Content $missing
    if (-not $r.Registered) { Write-Check 'PASS' '自检：未注册时判 FAIL' }
    else { Write-Check 'FAIL' '自检：未注册时判 FAIL' '漏判会让全站拒绝退回空体' }

    $wrongOrder = @'
builder.Services.AddControllers(options =>
{
    options.Filters.Add<ProblemDetailsContextFilter>();
    options.Filters.Add<ForbiddenProblemFilter>();
});
'@
    $r = Test-FilterRegistration -Content $wrongOrder
    if (-not $r.OrderOk) { Write-Check 'PASS' '自检：顺序颠倒时判 FAIL' }
    else { Write-Check 'FAIL' '自检：顺序颠倒时判 FAIL' '顺序错了错误码不会被登记' }

    $commented = @'
builder.Services.AddControllers(options =>
{
    // options.Filters.Add<ForbiddenProblemFilter>();
    options.Filters.Add<ProblemDetailsContextFilter>();
});
'@
    $r = Test-FilterRegistration -Content $commented
    if (-not $r.Registered) { Write-Check 'PASS' '自检：注册被注释掉时判 FAIL' }
    else { Write-Check 'FAIL' '自检：注册被注释掉时判 FAIL' '注释行若被当作注册，门禁会假绿' }

    $naked = 'if (!ok) return StatusCode(StatusCodes.Status403Forbidden);'
    if ($naked -match $NakedForbidPattern) { Write-Check 'PASS' '自检：裸 403 被识别' }
    else { Write-Check 'FAIL' '自检：裸 403 被识别' }

    $fine = 'return StatusCode(StatusCodes.Status403Forbidden, ApiProblem.Forbidden("缺权限"));'
    if ($fine -notmatch $NakedForbidPattern) { Write-Check 'PASS' '自检：带响应体的 403 不误判' }
    else { Write-Check 'FAIL' '自检：带响应体的 403 不误判' }

    if ($script:Failed -gt 0) { Write-Output "-- 自检 FAIL=$($script:Failed)"; exit 1 }
    Write-Output '-- 自检全部通过'
    exit 0
}

try {
    $root = Split-Path -Parent $PSScriptRoot
    Write-Output '== 拒绝出口门禁 =='

    $programPath = Join-Path $root 'EOS.API/Program.cs'
    if (-not (Test-Path -LiteralPath $programPath)) {
        Write-Check 'FAIL' 'Program.cs 存在' "未找到 $programPath"
    }
    else {
        $program = Get-Content -LiteralPath $programPath -Raw -Encoding utf8
        $result = Test-FilterRegistration -Content $program
        if ($result.Registered) { Write-Check 'PASS' 'ForbiddenProblemFilter 已注册' }
        else { Write-Check 'FAIL' 'ForbiddenProblemFilter 已注册' '缺失会让所有 Forbid() 退回空响应体' }

        if ($result.OrderOk) { Write-Check 'PASS' '注册顺序早于 ProblemDetailsContextFilter' }
        else { Write-Check 'FAIL' '注册顺序早于 ProblemDetailsContextFilter' '顺序颠倒则错误码不会被登记，日志不落盘' }
    }

    $violations = Test-NoNakedForbidden -Root $root
    if ($violations.Count -eq 0) { Write-Check 'PASS' '无绕过统一出口的裸 403 返回' }
    else { Write-Check 'FAIL' '无绕过统一出口的裸 403 返回' ($violations -join '; ') }
}
catch {
    Write-Check 'FAIL' '门禁执行异常' $_.Exception.Message
}

if ($script:Failed -gt 0) {
    Write-Output "-- FAIL=$($script:Failed)"
    exit 1
}
Write-Output '-- FAIL=0 全部通过'
exit 0
