#Requires -Version 7.0
<#
.SYNOPSIS
    全系统广扫的单一入口：按顺序跑四层体检，逐层对比基线，最后汇总一份报告。

.DESCRIPTION
    四层分别落在三个脚本里（A 渲染扫描 / B+C 提交回环与负面边界 / D 配置面 fuzz）。
    分散执行时要记住每层的参数、基线与先后关系，换个人、换台机器就容易只跑其中一层，
    然后把"只跑了一层"读成"系统没问题"。本脚本只做串联与汇总，**不重复实现任何判据**：
    判据、成本与安全边界都留在各自脚本里。

    它是**体检，不是门禁**：不进 CI、不阻断提交。退出码只表达"这次有没有相对基线的新增"，
    供人工分诊使用；基线内的既有红不会让它失败（棘轮只减不增，见各层脚本）。

    依赖：`EOS.Web` dev server 与 `EOS.API` 在线（由维护者启动），node 在 PATH 里。
    B/C/D 层会写库，故只在确认服务指向本机开发库时使用。

.PARAMETER WebBaseUrl
    前端基址，默认 http://localhost。

.PARAMETER Modules
    只跑点名模块，逗号分隔（如 `1401,1606`）。D 层不支持按模块筛选，会被跳过并说明。

.PARAMETER Limit
    每层只跑前 N 个模块。用于先看一批。

.PARAMETER Layers
    要跑的层，默认全部：A / B / C / D。例如 `-Layers A` 只做只读渲染扫描。

.PARAMETER PlanOnly
    只打印将要执行的命令与基线状态，不执行。

.EXAMPLE
    pwsh scripts/sweep-all.ps1
    pwsh scripts/sweep-all.ps1 -Layers A
    pwsh scripts/sweep-all.ps1 -Modules 1401,1606 -Layers A,B
    pwsh scripts/sweep-all.ps1 -PlanOnly
#>
param(
    [string] $WebBaseUrl = 'http://localhost',
    [string] $Modules,
    [int] $Limit,
    [ValidateSet('A', 'B', 'C', 'D')]
    [string[]] $Layers = @('A', 'B', 'C', 'D'),
    [switch] $PlanOnly
)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8

$root = Split-Path -Parent $PSScriptRoot
$renderBaseline = Join-Path $root 'scripts/render-sweep-baseline.json'
$roundtripBaseline = Join-Path $root 'scripts/record-roundtrip-baseline.json'

$node = (Get-Command node -ErrorAction SilentlyContinue)
if (-not $node -and -not $PlanOnly) {
    Write-Output 'FAIL 找不到 node，无法执行 A/B/C 层'
    exit 1
}

# B 与 C 共用一次回环运行：--boundary 是"在同一次建单链上追加投毒"，
# 拆成两次只会把每张单据建两遍、把基线比对搞乱。
$runB = $Layers -contains 'B'
$runC = $Layers -contains 'C'
$runA = $Layers -contains 'A'
$runD = $Layers -contains 'D'

$moduleArg = if ($Modules) { @("--modules=$Modules") } else { @() }
$limitArg = if ($Limit -gt 0) { @("--limit=$Limit") } else { @() }

$steps = @()

if ($runA) {
    # 配置面静态检查放在渲染扫描之前：连库、秒级，同类配置错误比逐个打开页面更早暴露。
    # 它按全库判据，不受 -Modules / -Limit 影响。
    $steps += @{
        Name   = 'A 前置：仅可选入字段的回填目标（配置面，只读）'
        File   = 'scripts/check-chooser-self-mapping.ps1'
        Args   = @('scripts/check-chooser-self-mapping.ps1')
        Runner = 'pwsh'
        Baseline = $null
    }
    $steps += @{
        Name   = 'A 渲染扫描（只读，不写库）'
        File   = 'scripts/render-sweep.mjs'
        Args   = @('scripts/render-sweep.mjs', $WebBaseUrl, "--baseline=$renderBaseline") + $moduleArg + $limitArg
        Runner = 'node'
        Baseline = $renderBaseline
    }
}

if ($runB -or $runC) {
    $args = @('scripts/record-roundtrip.mjs', $WebBaseUrl, "--baseline=$roundtripBaseline") + $moduleArg + $limitArg
    if ($runC) { $args += '--boundary' }
    $label = if ($runB -and $runC) {
        'B+C 提交回环 + 负面边界（写库，用后清理）'
    }
    elseif ($runB) {
        'B 提交回环（写库，用后清理）'
    }
    else {
        'C 负面边界（写库，用后清理）'
    }
    $steps += @{
        Name   = $label
        File   = 'scripts/record-roundtrip.mjs'
        Args   = $args
        Runner = 'node'
        Baseline = $roundtripBaseline
    }
}

if ($runD) {
    if ($Modules) {
        Write-Output 'SKIP D 层：配置面 fuzz 不支持按模块筛选（它克隆的是模板模块）'
    }
    else {
        $steps += @{
            Name   = 'D 配置面 fuzz（写库，克隆到保留号段并清场）'
            File   = 'scripts/module-config-fuzz.ps1'
            Args   = @('scripts/module-config-fuzz.ps1', '-BaseUrl', $WebBaseUrl)
            Runner = 'pwsh'
            Baseline = $null
        }
    }
}

$missing = @($steps | Where-Object { $_.Baseline -and -not (Test-Path -LiteralPath $_.Baseline) })
if ($missing.Count -gt 0) {
    Write-Output 'FAIL 基线文件缺失（缺失时该层会把全部既有红当成新增，失去棘轮意义）：'
    $missing | ForEach-Object { Write-Output "  $($_.Baseline)（来自 $($_.Name)）" }
    Write-Output '  首次使用先跑该层并加 --update-baseline 冻结基线；之后只减不增。'
    exit 1
}

Write-Output '===== 全系统广扫（体检，非门禁）====='
Write-Output "前端基址：$WebBaseUrl"
Write-Output "层：$($Layers -join ', ')"
if ($Modules) { Write-Output "模块：$Modules" }
if ($Limit -gt 0) { Write-Output "上限：前 $Limit 个" }
Write-Output ''

if ($PlanOnly) {
    $index = 0
    foreach ($step in $steps) {
        $index++
        Write-Output "[$index] $($step.Name)"
        Write-Output "    $($step.Runner) $($step.Args -join ' ')"
    }
    Write-Output ''
    Write-Output '（-PlanOnly：以上命令未执行）'
    exit 0
}

$stamp = Get-Date -Format 'yyyyMMddHHmmss'
$outDir = Join-Path $root 'logs/sweep-all'
New-Item -ItemType Directory -Path $outDir -Force | Out-Null
$results = @()
$failedLayers = 0

foreach ($step in $steps) {
    Write-Output ''
    Write-Output "===== $($step.Name) ====="
    $started = Get-Date
    & $step.Runner @($step.Args)
    $code = $LASTEXITCODE
    $elapsed = [int]((Get-Date) - $started).TotalSeconds
    $verdict = if ($code -eq 0) { 'PASS' } else { "FAIL(rc=$code)" }
    if ($code -ne 0) { $failedLayers++ }
    Write-Output "-- $($step.Name)：$verdict，用时 ${elapsed}s"
    $results += [pscustomobject]@{
        Layer = $step.Name
        Result = $verdict
        Seconds = $elapsed
        Script = $step.File
        Baseline = $(if ($step.Baseline) { Split-Path -Leaf $step.Baseline } else { '—' })
    }
}

$report = Join-Path $outDir "sweep-all-$stamp.md"
$lines = @(
    '# 全系统广扫汇总',
    '',
    "生成时间：$((Get-Date).ToString('yyyy-MM-dd HH:mm:ss'))",
    "前端基址：$WebBaseUrl",
    "范围：$($Layers -join ', ')$(if ($Modules) { "；模块 $Modules" })$(if ($Limit -gt 0) { "；前 $Limit 个" })",
    '',
    '| 层 | 结果 | 用时 | 脚本 | 基线 |',
    '|---|---|---|---|---|'
)
$results | ForEach-Object { $lines += "| $($_.Layer) | $($_.Result) | $($_.Seconds)s | ``$($_.Script)`` | $($_.Baseline) |" }
$lines += ''
$lines += '各层的判据、明细与失败截图在各自的报告里（`logs/render-sweep/`、`logs/record-roundtrip/`）。'
$lines += '本表只表达"相对基线有没有新增"，不替代人工分诊。'
Set-Content -LiteralPath $report -Value ($lines -join "`n") -Encoding utf8

Write-Output ''
Write-Output '===== 汇总 ====='
$results | ForEach-Object { Write-Output ("{0}：{1}（{2}s）" -f $_.Layer, $_.Result, $_.Seconds) }
Write-Output "报告：$report"

exit ($(if ($failedLayers -gt 0) { 1 } else { 0 }))
