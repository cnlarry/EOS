<#
.SYNOPSIS
    前端「工作区导航」门禁：跨模块跳转与返回只允许一种写法（ADR-040）。

.DESCRIPTION
    标签工作区按「模块窗口」建模：一个模块一个标签，跨模块只切/开标签，返回只回本模块列表。
    下面三类写法都是「在标签模型里偷偷开第二条路」，编译、lint、单测、构建全都照不出来，
    只会在界面上表现为"点返回跳到别处 / 点一下冒出两个同名标签 / 标签列表被另一个窗口覆盖"：

    1) `navigate(-1)`：把**历史后退**当成页面返回。历史里混着各标签的地址，退一步可能落到
       别的标签、甚至已关闭标签的地址上。返回只有一种含义：回本模块列表（表单页）或关闭自己
       （对照窗口），见 ADR-040 §2.2 规则 3。
    2) `safeBackPath` / `withBackPath` / `encodeBackPath`：已退役的 `back=` 参数机制。
       它让目标页记住"你从哪儿来"，于是同一颗返回按钮有多种去向；跨模块的来源现在由标签栏承载。
    3) `window.open('/(workbench|report|admin)…')`：用浏览器窗口打开应用内业务页。
       那会在同一会话里起**第二个外壳**，两边共享 localStorage、按 last-write-wins 互相覆盖
       标签列表（ADR-040 §1.3 S7）。应用内跳转一律走 `useOpenTab()`。
       打印（`/print/…`）例外：A4 打印不属于工作区，仍开浏览器窗口。

    判据（只扫源码文本）：
    - 范围：`EOS.Web/src` 下的 `.ts` / `.tsx`（含测试文件——用例里复制这些写法同样是债）；
    - 注释里的同名字样不算：扫描前剥离 `//` 与 `/* */` 注释。

.OUTPUTS
    PASS/FAIL + 清单（含 文件:行号）。命中即 exit 1。

.EXAMPLE
    pwsh scripts/check-tab-navigation.ps1
    pwsh scripts/check-tab-navigation.ps1 -SelfTest   # 额外做正反自检
#>
[CmdletBinding()]
param(
    # 前端源码根（默认仓库内的 EOS.Web/src）
    [string] $SrcDir,
    # 用合成样本做判别力自检：正例必须被抓、反例不得误报
    [switch] $SelfTest
)

$ErrorActionPreference = 'Stop'
try {
    if (-not $SrcDir) {
        $repoRoot = Split-Path -Parent $PSScriptRoot
        $SrcDir = Join-Path $repoRoot 'EOS.Web/src'
    }
    if (-not (Test-Path -LiteralPath $SrcDir)) {
        Write-Host "SKIP 工作区导航：找不到源码目录 $SrcDir" -ForegroundColor Yellow
        exit 0
    }

    $checks = @(
        @{ Name = '把历史后退当返回（navigate(-1)）'; Regex = 'navigate\(\s*-1\s*\)' },
        @{ Name = '已退役的 back= 参数机制'; Regex = '\b(safeBackPath|withBackPath|encodeBackPath)\b' },
        @{ Name = '用浏览器窗口打开应用内业务页'; Regex = 'window\.open\(\s*[`"'']/(workbench|report|admin)\b' }
    )

    function Remove-SourceComments([string] $text) {
        # 块注释先换成等长空白（保留行号），再删行注释。字符串里的 // 会被一并处理，
        # 后果只是"少报"，不会误报——门禁宁可漏也不误伤。
        $noBlock = [regex]::Replace($text, '/\*[\s\S]*?\*/', { param($m) ($m.Value -replace '[^\r\n]', ' ') })
        return [regex]::Replace($noBlock, '//[^\r\n]*', '')
    }

    function Get-NavigationHits([string] $text) {
        $clean = Remove-SourceComments $text
        $lines = $clean -split "`r?`n"
        $hits = @()
        for ($i = 0; $i -lt $lines.Count; $i++) {
            foreach ($check in $checks) {
                if ($lines[$i] -match $check.Regex) {
                    $hits += [pscustomobject]@{ Line = $i + 1; Name = $check.Name; Text = $lines[$i].Trim() }
                }
            }
        }
        return $hits
    }

    if ($SelfTest) {
        $selfTestFailed = $false
        $positive = @(
            'const goBack = () => navigate(-1)',
            'const back = safeBackPath(searchParams.get("back"))',
            'openTab(withBackPath(target, back))',
            "window.open('/workbench/1606', '_blank')",
            'window.open(`/report/RPT_1`, ''_blank'')'
        )
        $negative = @(
            '// 返回只回本模块列表，不要写 navigate(-1)',
            '/* 以前的 safeBackPath 已经退役 */',
            "window.open(`/print/1606?key=1`, '_blank')",
            'const url = `/workbench/${moduleId}`',
            "onClick={() => openTab(workbenchView(moduleId, key), { locate: true })}"
        )
        foreach ($sample in $positive) {
            if (@(Get-NavigationHits $sample).Count -eq 0) {
                Write-Host "FAIL 自检：正例未被抓住 -> $sample" -ForegroundColor Red
                $selfTestFailed = $true
            }
        }
        foreach ($sample in $negative) {
            if (@(Get-NavigationHits $sample).Count -gt 0) {
                Write-Host "FAIL 自检：反例被误报 -> $sample" -ForegroundColor Red
                $selfTestFailed = $true
            }
        }
        if ($selfTestFailed) { exit 1 }
        Write-Host "PASS 工作区导航自检：正例全抓、反例无误报" -ForegroundColor Green
    }

    $files = Get-ChildItem -LiteralPath $SrcDir -Recurse -Include *.ts, *.tsx -File |
        Where-Object { $_.FullName -notmatch '[\\/]node_modules[\\/]' }
    $found = @()
    foreach ($file in $files) {
        $text = Get-Content -LiteralPath $file.FullName -Raw -Encoding utf8
        $relative = $file.FullName.Substring($SrcDir.Length).TrimStart('\', '/') -replace '\\', '/'
        foreach ($hit in @(Get-NavigationHits $text)) {
            $found += "  $relative`:$($hit.Line)  [$($hit.Name)]  $($hit.Text)"
        }
    }

    if ($found.Count -gt 0) {
        Write-Host "FAIL 工作区导航：跨模块跳转/返回必须走 ADR-040 的唯一写法" -ForegroundColor Red
        $found | ForEach-Object { Write-Host $_ -ForegroundColor Red }
        Write-Host "  说明：应用内跳转用 useOpenTab()（跨模块带 locate，对照窗口带 asRecord）；" -ForegroundColor Yellow
        Write-Host "        表单返回用 navigate(workbenchList(moduleId))，对照窗口用 useCloseTab()。" -ForegroundColor Yellow
        exit 1
    }

    Write-Host "PASS 工作区导航：$($files.Count) 个源文件均无 navigate(-1) / back= 机制 / 业务页 window.open" -ForegroundColor Green
    exit 0
}
catch {
    Write-Host "FAIL 工作区导航门禁自身出错：$($_.Exception.Message)" -ForegroundColor Red
    exit 1
}
