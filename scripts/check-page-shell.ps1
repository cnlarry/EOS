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
    4) `card erp-list-card` / `erp-data-table`：**手抄统一组件的骨架**——在页面里直接拼这两个
       类名，而不是用 `ErpListCard` / `ErpTable`。DOM 看着一样，但组件后续长出的能力（列宽
       持久化、选择列、键盘导航……）这些页面一个都拿不到，将来改骨架还得记得改 N 处。
       产出这些类名的组件定义文件豁免（见 $classNameOwners）。

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
        @{ Name = '手拼卡内工具条（card-header + py- / d-flex）'; Regex = 'card-header\s+(py-|d-flex)' },
        # 页面根必须挂 `erp-base-page`（满屏/流式骨架的唯一入口）：漏挂的页面会退回"各页自己算高度"，
        # 表现为不占满、出页级滚动条、边距与别的页不一致——正是这一条门禁要拦的。
        # 登录页不在工作区内（自管 100vh），排除。
        @{ Name = '页面根未挂 erp-base-page（app.css 的页面骨架类）'; Regex = '^(?!.*erp-base-page).*className=[^>]*\berp-(?!(?:login|error)-page)(?:[a-z0-9]+(?:-[a-z0-9]+)*-page|designer(?![-\w])|dashboard(?![-\w]))' }
    )

    # 「手抄骨架」判据：这两个类名只应由统一组件产出，别处手写就是抄了骨架没复用组件
    $ownedClassNameChecks = @(
        @{ Name = '手抄列表卡骨架（card erp-list-card）'; Regex = 'card\s+erp-list-card' },
        @{ Name = '手抄数据表（erp-data-table）'; Regex = 'erp-data-table' }
    )
    # 豁免：这三个文件就是产出上述类名的组件本身
    $classNameOwners = @(
        'components/common/ErpListCard.tsx',
        'components/common/ErpDataTable.tsx',
        'components/common/ResizableTable.tsx'
    )

    function Remove-SourceComments([string] $text) {
        # 块注释先换成等长空白（保留行号），再删行注释。字符串里的 // 会被一并处理，
        # 后果只是"少报"，不会误报——门禁宁可漏也不误伤。
        $noBlock = [regex]::Replace($text, '/\*[\s\S]*?\*/', { param($m) ($m.Value -replace '[^\r\n]', ' ') })
        return [regex]::Replace($noBlock, '//[^\r\n]*', '')
    }

    function Get-ShellHits([string] $text, [bool] $includeOwned = $true) {
        $clean = Remove-SourceComments $text
        $lines = $clean -split "`r?`n"
        $hits = @()
        $active = if ($includeOwned) { $checks + $ownedClassNameChecks } else { $checks }
        for ($i = 0; $i -lt $lines.Count; $i++) {
            foreach ($check in $active) {
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
            '<div className="card-header py-2 d-flex align-items-center gap-2">',
            '<section className="card erp-list-card">',
            '<table className="table table-vcenter card-table erp-data-table">'
        )
        $negative = @(
            "// 页面根不要写 calc(100dvh - 96px) 这种魔数",
            "/* 手写遮罩 rgba(0, 0, 0, 0.4) 已被 Modal 取代 */",
            "/* 也不要在页面里手抄 card erp-list-card / erp-data-table 骨架 */",
            '<div className="card-header erp-card-toolbar">',
            '<div className="card-header">',
            '<ErpListCard ariaLabel="字段设置工具栏" search={<span>x</span>}>',
            '<ErpDataTable className="erp-designer-detail-table">',
            '<div className="erp-base-page erp-page-fill erp-workbench-page">',
            '<div className="erp-base-page erp-dashboard">'
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
    # 「页面根未挂 erp-base-page」的具名例外：这几处是把骨架类当**子区域布局**复用
    # （模型管理页的分区、版式设计器的加载态），不是页面根，不该要求它们挂 erp-base-page。
    # 键是"相对路径 + 该行类名签名"，不记行号——行号会漂移，签名不会。
    $basePageExemptions = @(
        'features/assistant-admin/ModelAdminPage.tsx|erp-workbench-page erp-model-admin-page',
        'features/assistant-admin/ModelAdminPage.tsx|erp-workbench-page erp-workbench-even',
        'features/form-designer/FormDesignerPage.tsx|erp-designer erp-designer-loading'
    )
    $found = @()
    foreach ($file in $files) {
        $text = Get-Content -LiteralPath $file.FullName -Raw -Encoding utf8
        $relative = $file.FullName.Substring($SrcDir.Length).TrimStart('\', '/') -replace '\\', '/'
        # 组件定义文件豁免「手抄骨架」判据——那两个类名本来就由它们产出
        $isOwner = $classNameOwners -contains $relative
        foreach ($hit in (Get-ShellHits $text (-not $isOwner))) {
            if ($hit.Name -like '页面根未挂*' -and ($basePageExemptions | Where-Object { $hit.Text -like "*$($_ -replace '^[^|]+\|', '')*" -and $_ -like "$relative|*" })) {
                continue
            }
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

    Write-Host "PASS 页面外壳：$($files.Count) 个前端源文件中无手写壳（视口高度魔数 / 弹窗遮罩 / 手拼卡内工具条 / 手抄组件骨架）" -ForegroundColor Green
    exit 0
}
catch {
    Write-Host "FAIL 页面外壳门禁自身出错：$($_.Exception.Message)" -ForegroundColor Red
    exit 1
}
