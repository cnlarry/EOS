<#
.SYNOPSIS
    前端「页面外壳只有一套」门禁：JSX 里不得再手写字面意义上的壳。

.DESCRIPTION
    下面三类写法都是「同一件事各页各写一遍」的典型。它们编译、lint、单测、构建全都照不出来，
    只在界面上表现为"这一页跟别处不太一样"（头部矮 4px、高度算错、遮罩浓淡不同）：

    1) `calc(100dvh`：页面根写死视口高度。外壳一变（标签栏 / 页头调整）这个数字就算错，
       而且改的人不知道要同步它。高度交给页面级语义类的 flex 链（见 app.css 的「页面骨架」段）。
    2) `rgba(0, 0, 0, 0.4)`：手写弹窗遮罩的特征值。弹窗一律用 `components/ui/Modal`，
       遮罩浓淡由它一处决定。
    3) `card-header py-` / `card-header d-flex`：手拼卡内工具条。命令栏下方那一行用
       `.card-header.erp-card-toolbar`（app.css 定义一次）。

    判据（只扫源码文本，不解析 CSS）：
    - 范围：`EOS.Web/src` 下的 `.ts` / `.tsx`（含测试文件——用例里复制这些写法同样是债）；
    - 注释里的同名字样不算：扫描前剥离 `//` 与 `/* */` 注释（注释里写"不要这么写"是正常写法）。

.OUTPUTS
    PASS/FAIL + 清单（含 文件:行号）。命中即 exit 1。

.EXAMPLE
    pwsh scripts/check-page-shell.ps1
    pwsh scripts/check-page-shell.ps1 -SelfTest   # 额外做正反自检
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
        Write-Host "SKIP 页面外壳：找不到源码目录 $SrcDir" -ForegroundColor Yellow
        exit 0
    }

    $checks = @(
        @{ Name = '页面根写死视口高度（calc(100dvh…)）'; Regex = 'calc\(\s*100dvh' },
        @{ Name = '手写弹窗遮罩（rgba(0, 0, 0, 0.4)）'; Regex = 'rgba\(\s*0\s*,\s*0\s*,\s*0\s*,\s*0?\.4\s*\)' },
        @{ Name = '手拼卡内工具条（card-header + py- / d-flex）'; Regex = 'card-header\s+(py-|d-flex)' }
    )

    function Remove-SourceComments([string] $text) {
        # 块注释先换成等长空白（保留行号），再删行注释。字符串里的 // 会被一并处理，
        # 后果只是"少报"，不会误报——门禁宁可漏也不误伤。
        $noBlock = [regex]::Replace($text, '/\*[\s\S]*?\*/', { param($m) ($m.Value -replace '[^\r\n]', ' ') })
        return [regex]::Replace($noBlock, '//[^\r\n]*', '')
    }

    function Get-ShellHits([string] $text) {
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
            "<div className={`"d-flex flex-column erp-full-list-page`"} style={{ height: 'calc(100dvh - 96px)' }}>",
            "<div style={{ background: 'rgba(0, 0, 0, 0.4)', zIndex: 1000 }}>",
            '<div className="card-header py-2 d-flex align-items-center gap-2">'
        )
        $negative = @(
            "// 页面根不要写 calc(100dvh - 96px) 这种魔数",
            "/* 手写遮罩 rgba(0, 0, 0, 0.4) 已被 Modal 取代 */",
            '<div className="card-header erp-card-toolbar">',
            '<div className="card-header">'
        )
        foreach ($sample in $positive) {
            if (@(Get-ShellHits $sample).Count -eq 0) {
                Write-Host "FAIL 自检：正例未被抓住 -> $sample" -ForegroundColor Red
                $selfTestFailed = $true
            }
        }
        foreach ($sample in $negative) {
            if (@(Get-ShellHits $sample).Count -gt 0) {
                Write-Host "FAIL 自检：反例被误报 -> $sample" -ForegroundColor Red
                $selfTestFailed = $true
            }
        }
        if ($selfTestFailed) { exit 1 }
        Write-Host "PASS 页面外壳自检：正例全抓、反例无误报" -ForegroundColor Green
    }

    $files = Get-ChildItem -LiteralPath $SrcDir -Recurse -Include *.ts, *.tsx -File |
        Where-Object { $_.FullName -notmatch '[\\/]node_modules[\\/]' }
    $found = @()
    foreach ($file in $files) {
        $text = Get-Content -LiteralPath $file.FullName -Raw -Encoding utf8
        foreach ($hit in (Get-ShellHits $text)) {
            $relative = $file.FullName.Substring($SrcDir.Length).TrimStart('\', '/')
            $found += [pscustomobject]@{
                Where = "$relative`:$($hit.Line)"
                Name  = $hit.Name
                Text  = $hit.Text
            }
        }
    }

    if ($found.Count -gt 0) {
        Write-Host "FAIL 页面外壳：JSX 里出现手写壳写法 $($found.Count) 处（应改用统一外壳）" -ForegroundColor Red
        $found | ForEach-Object { Write-Host ("  {0}  [{1}]" -f $_.Where, $_.Name); Write-Host ("      {0}" -f $_.Text) }
        exit 1
    }

    Write-Host "PASS 页面外壳：$($files.Count) 个前端源文件中无手写壳（视口高度魔数 / 弹窗遮罩 / 手拼卡内工具条）" -ForegroundColor Green
    exit 0
}
catch {
    Write-Host "FAIL 页面外壳门禁自身出错：$($_.Exception.Message)" -ForegroundColor Red
    exit 1
}
