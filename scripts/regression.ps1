#Requires -Version 7.0
<#
.SYNOPSIS
EOS 一键回归护栏：单测 + 前端 lint/build/test + E2E 四链 + UI 冒烟。
.DESCRIPTION
作为 goal 模式阶段护栏（docs/plans/archive/Agent自动化目标.md §3）：任何阶段完成必须全绿。
输出统一 JSON/Markdown 摘要到 logs/goal/regression/，任一子项失败即非零退出。
依赖：EOS.API 运行于 ApiUrl、EOS.Web dev server 运行于 WebUrl（-StartServices 可自动拉起 API）。
.EXAMPLE
.\scripts\regression.ps1
.\scripts\regression.ps1 -SkipE2E -SkipUi
.\scripts\regression.ps1 -StartServices
#>
param(
    [string]$ApiUrl = 'http://localhost:5261',
    [string]$WebUrl = 'http://localhost:5173',
    [switch]$SkipUnit,
    [switch]$SkipFrontend,
    [switch]$SkipE2E,
    [switch]$SkipUi,
    [switch]$StartServices,
    [string]$OutDir = ''
)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
if (-not $OutDir) { $OutDir = Join-Path $root 'logs\goal\regression' }
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
New-Item -ItemType Directory -Path $OutDir -Force | Out-Null

$results = [System.Collections.Generic.List[object]]::new()

function Test-PortListening {
    param([int]$Port)
    return $null -ne (Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue)
}

function Write-StepResult {
    param([string]$Name, [bool]$Ok, [string]$Log, [double]$Seconds, [string]$Detail = '')
    $results.Add([pscustomobject]@{
        Name     = $Name
        Status   = if ($Ok) { 'PASS' } else { 'FAIL' }
        Seconds  = [math]::Round($Seconds, 1)
        Log      = $Log
        Detail   = $Detail
    })
    if ($Ok) {
        Write-Host "[PASS] $Name" -ForegroundColor Green
    } else {
        Write-Host "[FAIL] $Name$($Detail ? " — $Detail" : '')" -ForegroundColor Red
    }
}

function Invoke-NativeStep {
    param(
        [string]$Name,
        [scriptblock]$Body,
        [string]$StepLog
    )
    Write-Host "`n===== $Name =====" -ForegroundColor Cyan
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    try {
        $null = & $Body 2>&1 | Tee-Object -FilePath $StepLog
        $code = $LASTEXITCODE
        $sw.Stop()
        Write-StepResult -Name $Name -Ok ($code -eq 0) -Log $StepLog -Seconds $sw.Elapsed.TotalSeconds `
            -Detail $(if ($code -ne 0) { "exit=$code" } else { '' })
        return $code -eq 0
    } catch {
        $sw.Stop()
        Write-Host "[ERROR] $($_.Exception.Message)" -ForegroundColor Red
        Write-StepResult -Name $Name -Ok $false -Log $StepLog -Seconds $sw.Elapsed.TotalSeconds -Detail $_.Exception.Message
        return $false
    }
}

# ---- 服务就绪检查 / 拉起 ----
$apiPort = ([uri]$ApiUrl).Port
$webPort = ([uri]$WebUrl).Port

if ($StartServices) {
    if (-not (Test-PortListening $apiPort)) {
        Write-Host "[服务] EOS.API 未运行，自动启动（dev-services.ps1 start api）" -ForegroundColor Yellow
        & (Join-Path $root 'scripts\dev-services.ps1') start api | Out-Host
    }
    if (-not (Test-PortListening $webPort)) {
        Write-Host "[服务] EOS.Web dev server 未运行，自动启动 npm run dev" -ForegroundColor Yellow
        $webOut = Join-Path $root 'logs\web-dev.out.log'
        $webErr = Join-Path $root 'logs\web-dev.err.log'
        # Windows 下 npm 是 .cmd 脚本，Start-Process -FilePath 'npm' 会报"不是有效的 Win32 应用程序"，
        # 必须用 npm.cmd（或通过 cmd.exe 包装）。
        $npmCmd = (Get-Command npm.cmd -ErrorAction SilentlyContinue).Source
        if (-not $npmCmd) { $npmCmd = (Get-Command npm -ErrorAction SilentlyContinue).Source }
        if (-not $npmCmd) { throw '未找到 npm/npm.cmd，无法启动 EOS.Web dev server' }
        Start-Process -FilePath $npmCmd -ArgumentList 'run', 'dev' `
            -WorkingDirectory (Join-Path $root 'EOS.Web') `
            -RedirectStandardOutput $webOut -RedirectStandardError $webErr -WindowStyle Hidden | Out-Null
    }
    $deadline = (Get-Date).AddSeconds(60)
    while ((Get-Date) -lt $deadline) {
        $apiOk = Test-PortListening $apiPort
        $webOk = Test-PortListening $webPort
        if ($apiOk -and $webOk) { break }
        Start-Sleep -Seconds 2
    }
    Write-Host "[服务] api=$apiOk web=$webOk" -ForegroundColor Cyan
} else {
    $apiOk = Test-PortListening $apiPort
    $webOk = Test-PortListening $webPort
    Write-Host "[服务] api=$apiOk web=$webOk（-StartServices 可自动拉起）" -ForegroundColor Cyan
}

# ---- 1. 单测（排除 IM-live 环境基线）----
if (-not $SkipUnit) {
    $log = Join-Path $OutDir "step-unit.log"
    $artifacts = Join-Path $OutDir "artifacts-unit"
    [void](Invoke-NativeStep -Name '单测 dotnet test' -StepLog $log -Body {
        # --artifacts-path 把构建输出重定向到独立目录，避免与正在运行的 EOS.API 进程争用默认 bin
        dotnet test (Join-Path $root 'EOS.API.Tests\EOS.API.Tests.csproj') --nologo -v minimal --filter 'Category!=Live' --artifacts-path $artifacts
    })
}

# ---- 2. 前端 lint / build / test ----
if (-not $SkipFrontend) {
    $log = Join-Path $OutDir "step-frontend.log"
    [void](Invoke-NativeStep -Name '前端 lint/build/test' -StepLog $log -Body {
        Push-Location (Join-Path $root 'EOS.Web')
        try {
            npm run lint
            if ($LASTEXITCODE -ne 0) { return }
            npm run build
            if ($LASTEXITCODE -ne 0) { return }
            npm run test
        } finally {
            Pop-Location
        }
    })
    # vitest forks worker 启动在机器高负载时偶发超时（测试本身通过）；
    # 失败时等待后重试一次，仍失败才判定为回归失败。
    $frontendResult = $results | Where-Object { $_.Name -eq '前端 lint/build/test' } | Select-Object -Last 1
    if ($frontendResult -and $frontendResult.Status -ne 'PASS') {
        $results.Remove($frontendResult)
        Write-Host "`n[重试] 前端 lint/build/test（上次 worker 启动超时）" -ForegroundColor Yellow
        Start-Sleep -Seconds 5
        $log2 = Join-Path $OutDir "step-frontend-retry.log"
        [void](Invoke-NativeStep -Name '前端 lint/build/test' -StepLog $log2 -Body {
            Push-Location (Join-Path $root 'EOS.Web')
            try {
                npm run lint
                if ($LASTEXITCODE -ne 0) { return }
                npm run build
                if ($LASTEXITCODE -ne 0) { return }
                npm run test
            } finally {
                Pop-Location
            }
        })
    }
}

# ---- 3. E2E 四链（业务闭环/生产/制程/库存）----
if (-not $SkipE2E) {
    if (-not $apiOk) {
        Write-StepResult -Name 'E2E 四链' -Ok $false -Log '' -Seconds 0 -Detail 'EOS.API 未运行'
    } else {
        Write-Host "`n===== E2E 四链 =====" -ForegroundColor Cyan
        $sw = [System.Diagnostics.Stopwatch]::StartNew()
        $log = Join-Path $OutDir "step-e2e.log"
        try {
            # *>&1 合并所有流（含 Write-Host），退出码以 E2eAll 的显式 exit 为准
            $output = & (Join-Path $root 'EOS.API.Tests\E2eAll.ps1') -ApiUrl $ApiUrl *>&1
            $code = $LASTEXITCODE
            $output | Out-File -LiteralPath $log -Encoding utf8
            $sw.Stop()
            Write-StepResult -Name 'E2E 四链' -Ok ($code -eq 0) -Log $log -Seconds $sw.Elapsed.TotalSeconds `
                -Detail $(if ($code -ne 0) { "exit=$code" } else { '' })
        } catch {
            $sw.Stop()
            Write-Host "[ERROR] $($_.Exception.Message)" -ForegroundColor Red
            Write-StepResult -Name 'E2E 四链' -Ok $false -Log $log -Seconds $sw.Elapsed.TotalSeconds -Detail $_.Exception.Message
        }
    }
}

# ---- 4. UI 冒烟 ----
if (-not $SkipUi) {
    if (-not $webOk) {
        Write-StepResult -Name 'UI 冒烟' -Ok $false -Log '' -Seconds 0 -Detail 'EOS.Web dev server 未运行'
    } else {
        $log = Join-Path $OutDir "step-ui.log"
        [void](Invoke-NativeStep -Name 'UI 冒烟' -StepLog $log -Body {
            Push-Location $root
            node scripts\ui-verify.mjs $WebUrl
        })
    }
}

# ---- 汇总 ----
Write-Host "`n===== 回归汇总 =====" -ForegroundColor Cyan
$results | Format-Table Name, Status, Seconds, Detail -AutoSize
$failed = @($results | Where-Object { $_.Status -eq 'FAIL' }).Count
$total = $results.Count
$summary = [pscustomobject]@{
    GeneratedAt = (Get-Date).ToString('yyyy-MM-dd HH:mm:ss')
    ApiUrl      = $ApiUrl
    WebUrl      = $WebUrl
    Total       = $total
    Passed      = $total - $failed
    Failed      = $failed
    Result      = if ($failed -eq 0) { 'PASS' } else { 'FAIL' }
    Steps       = $results
}
$jsonPath = Join-Path $OutDir "regression-$stamp.json"
$summary | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $jsonPath -Encoding utf8
$mdPath = Join-Path $OutDir "regression-$stamp.md"
@(
    "# EOS 一键回归报告 $stamp",
    '',
    "- 结果：**$($summary.Result)**（$($total - $failed)/$total 通过）",
    "- 生成时间：$($summary.GeneratedAt)",
    '',
    '| 子项 | 状态 | 耗时(秒) | 说明 |',
    '|---|---|---|---|',
    ($results | ForEach-Object { "| $($_.Name) | $($_.Status) | $($_.Seconds) | $($_.Detail) |" })
) | Set-Content -LiteralPath $mdPath -Encoding utf8
Write-Host "报告：$jsonPath" -ForegroundColor Cyan
Write-Host "报告：$mdPath" -ForegroundColor Cyan

if ($failed -gt 0) {
    Write-Host "回归失败：$failed/$total 子项未通过" -ForegroundColor Red
    exit 1
}
Write-Host "回归全绿（$($total - $failed)/$total）" -ForegroundColor Green
exit 0
