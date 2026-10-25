#Requires -Version 7.0
<#
.SYNOPSIS
    构建前端产物并放入 EOS.API/wwwroot：把"API 同时托管 SPA"这个部署形态做成一条可复现命令。

.DESCRIPTION
    生产形态只有一个进程：EOS.API 用 UseDefaultFiles / UseStaticFiles / MapFallbackToFile 提供前端。
    那要求 `EOS.Web` 的构建产物落在 `EOS.API/wwwroot` 下。手工 `npm run build` + 复制目录有三处
    容易出错：漏拷 `index.html`、上一版带哈希的 assets 没清干净（浏览器可能拿到旧 chunk）、
    wwwroot 里残留半份产物（缺某个 chunk 时页面白屏但服务端一切正常）。

    本脚本只做这一件事，并把三处都做成断言：

    - `EOS.Web/dist/index.html` 必须存在（否则先构建失败，不要复制半成品）；
    - 复制前清空 `EOS.API/wwwroot`（保留 `.gitkeep`），不留上一版 assets；
    - 复制后回读：`index.html` 必须在，且它引用的每个 `assets/*` 文件都必须真的在 wwwroot 里。

    它**不**启动服务、不碰数据库、不改任何配置——部署形态的其余口径见
    `docs/guide/75-部署与环境配置.md`。

.PARAMETER Install
    复制前先跑 `npm ci`（依赖有变化或换机时用；日常构建不需要，省一轮安装）。

.PARAMETER SkipBuild
    跳过 `npm run build`，只把现有 `EOS.Web/dist` 搬到目标目录（离线核对产物布局时用）。

.PARAMETER Destination
    产物落地目录，默认 `EOS.API/wwwroot`（本机要让 API 兼作前端宿主的形态）。
    发布到部署目录时指向 `<部署目录>/wwwroot`——那时不保留 `.gitkeep`，打包目录里不该有它。

.EXAMPLE
    pwsh scripts/build-web.ps1

.EXAMPLE
    pwsh scripts/build-web.ps1 -Install

.EXAMPLE
    pwsh scripts/build-web.ps1 -Destination D:\deploy\eos\wwwroot
#>
param(
    [switch] $Install,
    [switch] $SkipBuild,
    [string] $Destination
)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8

$root = Split-Path -Parent $PSScriptRoot
$webDir = Join-Path $root 'EOS.Web'
$wwwroot = if ([string]::IsNullOrWhiteSpace($Destination)) { Join-Path $root 'EOS.API/wwwroot' } else { [IO.Path]::GetFullPath($Destination) }
# 默认落地目录就是仓库里的 wwwroot（有 .gitkeep 占位）；自定义部署目录里不需要它
$keepGitkeep = ($wwwroot.TrimEnd('\', '/') -eq ([IO.Path]::GetFullPath((Join-Path $root 'EOS.API/wwwroot'))).TrimEnd('\', '/'))
$dist = Join-Path $webDir 'dist'

function Fail([string] $message) {
    Write-Host "FAIL $message" -ForegroundColor Red
    exit 1
}

try {
    if (-not (Test-Path $webDir)) { Fail "找不到前端工程目录：$webDir" }

    Push-Location $webDir
    try {
        if ($Install) {
            Write-Host '== npm ci ==' -ForegroundColor Cyan
            npm ci
            if ($LASTEXITCODE -ne 0) { Fail "npm ci 失败（exit=$LASTEXITCODE）" }
        }
        if (-not $SkipBuild) {
            Write-Host '== npm run build ==' -ForegroundColor Cyan
            npm run build
            if ($LASTEXITCODE -ne 0) { Fail "npm run build 失败（exit=$LASTEXITCODE）" }
        }
    }
    finally {
        Pop-Location
    }

    # 构建没出产物就别复制：把空目录搬进 wwwroot 等于交付一个白屏站点
    $distIndex = Join-Path $dist 'index.html'
    if (-not (Test-Path $distIndex)) { Fail "构建产物里没有 index.html：$distIndex" }
    $distAssets = Join-Path $dist 'assets'
    if (-not (Test-Path $distAssets)) { Fail "构建产物里没有 assets 目录：$distAssets" }

    Write-Host '== 清空目标目录 ==' -ForegroundColor Cyan
    if (Test-Path $wwwroot) {
        Get-ChildItem -LiteralPath $wwwroot -Force |
            Where-Object { -not ($keepGitkeep -and $_.Name -eq '.gitkeep') } |
            ForEach-Object { Remove-Item -LiteralPath $_.FullName -Recurse -Force }
    }
    else {
        New-Item -ItemType Directory -Path $wwwroot | Out-Null
    }

    Write-Host '== 复制产物 ==' -ForegroundColor Cyan
    Copy-Item -Path (Join-Path $dist '*') -Destination $wwwroot -Recurse -Force

    # 回读断言：只复制不核对，正是"漏一个 chunk 页面白屏"的来源
    $index = Join-Path $wwwroot 'index.html'
    if (-not (Test-Path $index)) { Fail "复制后 wwwroot 里没有 index.html：$index" }

    $html = Get-Content -LiteralPath $index -Raw -Encoding utf8
    $referenced = [regex]::Matches($html, '(?:src|href)="\.?/?(assets/[^"]+)"') |
        ForEach-Object { $_.Groups[1].Value } |
        Select-Object -Unique
    if ($referenced.Count -eq 0) { Fail 'index.html 里没有引用任何 assets/*——产物布局与预期不符' }

    $missing = @()
    foreach ($relative in $referenced) {
        $target = Join-Path $wwwroot ($relative -replace '/', [IO.Path]::DirectorySeparatorChar)
        if (-not (Test-Path $target)) { $missing += $relative }
    }
    if ($missing.Count -gt 0) { Fail ("wwwroot 缺少 index.html 引用的资源：{0}" -f ($missing -join '、')) }

    $fileCount = (Get-ChildItem -LiteralPath $wwwroot -Recurse -File -Force | Measure-Object).Count
    Write-Host ("PASS 前端产物已就位：{0} 个文件，index.html 引用的 {1} 个 assets 全部存在" -f $fileCount, $referenced.Count) -ForegroundColor Green
    Write-Host "     部署形态下未登录打开任意页面路径应返回 200 + text/html（见 docs/guide/75）。" -ForegroundColor DarkGray
    exit 0
}
catch {
    Fail "构建/复制失败：$($_.Exception.Message)"
}
