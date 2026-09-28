<#
.SYNOPSIS
    前端样式类门禁：JSX 里用的 erp-* 类名必须在 CSS 里有定义（禁止悬空类名）。

.DESCRIPTION
    「悬空类名」指组件里写了 className="erp-xxx"，但样式表里从没有 `.erp-xxx` 规则。
    这类写法编译、lint、单测、构建全都照不出来，只在界面上表现为「页面像没写样式」——
    实际外观退化成 Bootstrap 默认值，而这正是「看起来像 demo」的物理机制：
    页面以为自己在用设计语言，其实什么都没生效。

    判据（只查 erp- 前缀，即本项目自有的语义类）：
    - 使用侧：TS/TSX 里 `className=...` 的**字面量部分**中出现的 erp-* 类名；
    - 定义侧：EOS.Web 下所有 .css 文件里出现的 `.erp-*` 选择器（含 :not(...) 等组合选择器内）。

    存量按**具名棘轮**执行：`-DanglingBaseline` 列出已知悬空类名（键为 相对路径|类名，
    不含行号——行号会随无关改动漂移）。新增即 exit 1；已清理的条目只提示下调基线，
    不会因为"修好了"而失败。用 `-DumpBaseline` 可打印当前清单的基线字面量。

    不查的事项（明确边界，避免误以为覆盖了）：
    - 不查 `closest('.erp-x')` / `querySelector` 这类选择器字符串（只在测试里出现）；
    - 不查动态拼出的完整类名（如 `erp-tile-${kind}`）：末段以 `-` 结尾时按**前缀**匹配，
      只要存在同前缀的定义即通过；
    - 不查 Bootstrap/Tabler 原生类（无 erp- 前缀）。

    -SelfTest 做判别力自检：把解析 + 判定用在合成样本上，正例必须被抓住、反例不得误报。

.OUTPUTS
    PASS/FAIL + 清单（含 文件:行号）。存在新增悬空类名即 exit 1。

.EXAMPLE
    pwsh scripts/check-css-classes.ps1            # exit 0 = 无新增悬空类名
    pwsh scripts/check-css-classes.ps1 -SelfTest   # 额外做正反自检
#>
[CmdletBinding()]
param(
    # 前端工程根目录（默认仓库内的 EOS.Web）
    [string] $WebDir,
    [switch] $SelfTest,
    # 打印当前悬空类名清单的基线字面量（用于登记/下调基线）
    [switch] $DumpBaseline,
    # 已知悬空类名存量（键为 相对路径|类名）。只许减不许增：新增失败，清理后请下调本列表。
    [string[]] $DanglingBaseline = @(
        'src/components/common/ErpFieldChooser.tsx|erp-field-chooser-list',
        'src/components/common/TabbedPanel.tsx|erp-tabbed-panel',
        'src/components/layout/AppShell.tsx|erp-nav-child-depth-*',
        'src/components/layout/AppShell.tsx|erp-nav-children-depth-*',
        'src/components/layout/WorkspaceTabBar.tsx|erp-workspace-tab',
        'src/features/admin/ReportAdminPage.tsx|erp-list-header',
        'src/features/assistant/AssistantMemoryPanel.tsx|erp-assistant-memory',
        'src/features/assistant/AssistantMemoryPanel.tsx|erp-assistant-memory-form',
        'src/features/assistant/AssistantMemoryPanel.tsx|erp-assistant-memory-item',
        'src/features/document-workbench/DocumentWorkbenchPage.tsx|erp-group-dropdown',
        'src/features/field-admin/FieldPickerSelect.tsx|erp-picker-option',
        'src/features/menu-admin/MenuAdminPage.tsx|erp-menu-form-card',
        'src/features/menu-admin/MenuAdminPage.tsx|erp-nav-child-depth-*',
        'src/features/menu-admin/MenuAdminPage.tsx|erp-nav-children-depth-*',
        'src/features/print/PrintViewPage.tsx|erp-pdf-frame',
        'src/features/print/PrintViewPage.tsx|erp-print-page',
        'src/features/rights-admin/GroupAdminPages.tsx|erp-list-header',
        'src/features/rights-admin/RightsMatrix.tsx|erp-nav-child-depth-*',
        'src/features/rights-admin/RightsMatrix.tsx|erp-nav-children-depth-*',
        'src/features/rights-admin/UserGroupAdminPage.tsx|erp-list-header',
        'src/features/settings/SystemSettingsTabs.tsx|erp-settings-tabs',
        'src/features/user-admin/UserAdminPage.tsx|erp-list-header'
    )
)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8

if (-not $WebDir) {
    $WebDir = Join-Path (Split-Path -Parent $PSScriptRoot) 'EOS.Web'
}
$WebDir = (Resolve-Path -LiteralPath $WebDir).Path

# 例外清单：确需豁免的类名（当前为空）。加入前先确认它确实由外部样式表提供。
$allowList = @()

# 类名 token：左侧必须不是标识符或连字符，否则 `not-an-erp-class` 会被误当成 `erp-class`
$classTokenPattern = '(?<![\w-])erp-[A-Za-z0-9_-]+'

<#
    从源码文本里取出所有 className 字面量片段。
    支持 `className="..."` 与 `className={...}`（花括号按深度配平，故 `` `x ${c ? 'y' : ''}` `` 可正确截取）；
    变量/表达式形态（className={styles.x}）不含字面量类名，直接跳过。
#>
function Get-ClassChunks {
    param([string] $Text)

    $chunks = New-Object System.Collections.Generic.List[object]
    foreach ($match in [regex]::Matches($Text, 'className\s*=\s*')) {
        $start = $match.Index + $match.Length
        if ($start -ge $Text.Length) { continue }
        $first = $Text[$start]
        if ($first -eq '"' -or $first -eq "'") {
            $end = $Text.IndexOf($first, $start + 1)
            if ($end -lt 0) { continue }
            $chunks.Add([pscustomobject]@{ Text = $Text.Substring($start + 1, $end - $start - 1); Index = $start })
            continue
        }
        if ($first -eq '{') {
            $depth = 0
            $i = $start
            while ($i -lt $Text.Length) {
                $char = $Text[$i]
                if ($char -eq '{') { $depth++ }
                elseif ($char -eq '}') {
                    $depth--
                    if ($depth -eq 0) { break }
                }
                $i++
            }
            if ($i -ge $Text.Length) { continue }
            $chunks.Add([pscustomobject]@{ Text = $Text.Substring($start + 1, $i - $start - 1); Index = $start })
        }
    }
    return $chunks
}

<#
    判定一组「使用」是否都在「定义」内。
    返回悬空类名数组。末段以 `-` 结尾的前缀（动态拼接）只要存在同前缀定义即视为已定义。
#>
function Get-DanglingClass {
    param(
        [string[]] $Used,
        [System.Collections.Generic.HashSet[string]] $Defined
    )

    $dangling = New-Object System.Collections.Generic.List[string]
    foreach ($name in $Used) {
        if ($allowList -contains $name) { continue }
        if ($name.EndsWith('-')) {
            $matched = $false
            foreach ($candidate in $Defined) {
                if ($candidate.StartsWith($name)) { $matched = $true; break }
            }
            if (-not $matched) { $dangling.Add($name + '*') }
            continue
        }
        if (-not $Defined.Contains($name)) { $dangling.Add($name) }
    }
    return $dangling
}

# ---------- 定义侧：收集全部 .css 里的 .erp-* 选择器 ----------
$cssFiles = Get-ChildItem -LiteralPath $WebDir -Recurse -Filter '*.css' -File |
    Where-Object { $_.FullName -notmatch '[\\/]node_modules[\\/]' -and $_.FullName -notmatch '[\\/]dist[\\/]' }
$defined = New-Object System.Collections.Generic.HashSet[string]
foreach ($file in $cssFiles) {
    $css = Get-Content -LiteralPath $file.FullName -Raw -Encoding UTF8
    $css = [regex]::Replace($css, '/\*.*?\*/', '', [Text.RegularExpressions.RegexOptions]::Singleline)
    foreach ($match in [regex]::Matches($css, '\.(erp-[A-Za-z0-9_-]+)')) {
        [void]$defined.Add($match.Groups[1].Value)
    }
}

# ---------- 使用侧：收集 TS/TSX 里 className 的字面量类名 ----------
$sourceFiles = Get-ChildItem -LiteralPath (Join-Path $WebDir 'src') -Recurse -File |
    Where-Object { $_.Extension -in @('.ts', '.tsx') -and $_.FullName -notmatch '[\\/]node_modules[\\/]' }

# 每项：Key = 相对路径|类名（稳定，不含行号），Detail = 相对路径:行号  类名
$found = New-Object System.Collections.Generic.List[object]
$seenKeys = New-Object System.Collections.Generic.HashSet[string]
$usedCount = 0
foreach ($file in $sourceFiles) {
    $text = Get-Content -LiteralPath $file.FullName -Raw -Encoding UTF8
    $relative = $file.FullName.Substring($WebDir.Length + 1).Replace('\', '/')
    foreach ($chunk in (Get-ClassChunks -Text $text)) {
        $used = @([regex]::Matches($chunk.Text, $classTokenPattern) | ForEach-Object { $_.Value })
        if ($used.Count -eq 0) { continue }
        $usedCount += $used.Count
        $bad = Get-DanglingClass -Used $used -Defined $defined
        if ($bad.Count -eq 0) { continue }
        $line = ($text.Substring(0, $chunk.Index) -split "`n").Count
        foreach ($name in $bad) {
            $key = "$relative|$name"
            if (-not $seenKeys.Add($key)) { continue }
            $found.Add([pscustomobject]@{ Key = $key; Detail = ("{0}:{1}  {2}" -f $relative, $line, $name) })
        }
    }
}

if ($DumpBaseline) {
    Write-Output '    [string[]] $DanglingBaseline = @('
    $found | Sort-Object Key | ForEach-Object { Write-Output ("        '{0}'," -f $_.Key) }
    Write-Output '    )'
    exit 0
}

if ($SelfTest) {
    # 判别力自检：同一套解析 + 判定用在合成样本上，正例必须被抓住、反例不得误报。
    $sample = @'
export function Sample() {
  return <div className="erp-defined-a erp-missing-b">
    <span className={`erp-defined-a${flag ? ' erp-missing-c' : ''}`} />
    <span className="not-an-erp-class text-muted" />
    <span className={`erp-dynamic-${kind}`} />
    <span className={`erp-ghost-${kind}`} />
  </div>
}
'@
    $sampleDefined = New-Object System.Collections.Generic.HashSet[string]
    foreach ($name in @('erp-defined-a', 'erp-dynamic-x')) { [void]$sampleDefined.Add($name) }

    $sampleUsed = New-Object System.Collections.Generic.List[string]
    foreach ($chunk in (Get-ClassChunks -Text $sample)) {
        foreach ($match in [regex]::Matches($chunk.Text, $classTokenPattern)) { [void]$sampleUsed.Add($match.Value) }
    }
    $sampleBad = Get-DanglingClass -Used $sampleUsed.ToArray() -Defined $sampleDefined

    # 正例：erp-missing-b / erp-missing-c 应被抓；erp-ghost-*（动态前缀无同前缀定义）应被抓；
    #       erp-defined-a 已定义、erp-dynamic-* 有同前缀定义、not-an-erp-class 不是 erp-* 类名，三者都不得误报
    $expected = @('erp-missing-b', 'erp-missing-c', 'erp-ghost-*')
    $unexpected = $sampleBad | Where-Object { $expected -notcontains $_ }
    $missing = $expected | Where-Object { $sampleBad -notcontains $_ }
    if ($unexpected.Count -gt 0 -or $missing.Count -gt 0) {
        Write-Output ("FAIL -SelfTest 判别力不符：期望抓到 [{0}]，实得 [{1}]" -f ($expected -join ', '), (($sampleBad | Sort-Object) -join ', '))
        exit 3
    }

    # 反例：全部已定义（含动态前缀有同名定义）时不得报任何悬空类名
    $cleanDefined = New-Object System.Collections.Generic.HashSet[string]
    foreach ($name in @('erp-defined-a', 'erp-missing-b', 'erp-missing-c', 'erp-dynamic-x', 'erp-ghost-x')) { [void]$cleanDefined.Add($name) }
    $cleanBad = Get-DanglingClass -Used $sampleUsed.ToArray() -Defined $cleanDefined
    if ($cleanBad.Count -gt 0) {
        Write-Output ("FAIL -SelfTest 反例误报：全部已定义时仍报出 [{0}]" -f (($cleanBad | Sort-Object) -join ', '))
        exit 3
    }

    Write-Output ("PASS -SelfTest 判别力：正例 {0} 条被抓、反例 0 误报（解析覆盖引号字面量与模板字面量）" -f $sampleBad.Count)
}

$baselineSet = New-Object System.Collections.Generic.HashSet[string]
foreach ($entry in $DanglingBaseline) { [void]$baselineSet.Add($entry) }

$added = @($found | Where-Object { -not $baselineSet.Contains($_.Key) })
$resolved = @($DanglingBaseline | Where-Object { -not ($found.Key -contains $_) })

if ($added.Count -gt 0) {
    Write-Output ("FAIL 新增悬空类名 {0} 处（组件写了类名但样式表里没有定义，外观会退化成浏览器默认值）：" -f $added.Count)
    $added | Sort-Object Key | ForEach-Object { Write-Output "  $($_.Detail)" }
    Write-Output ("  定义来源：{0} 个 CSS 文件 / {1} 个 erp-* 类；使用侧共 {2} 处类名引用；" -f $cssFiles.Count, $defined.Count, $usedCount)
    Write-Output ("  已知存量 {0} 处（只许减不许增）。新类名请补进 CSS，或确认后登记进 -DanglingBaseline。" -f $DanglingBaseline.Count)
    exit 1
}

if ($resolved.Count -gt 0) {
    Write-Output ("PASS 前端样式类：无新增悬空类名（{0} 处引用全部有定义，已知存量 {1} 处）。" -f $usedCount, $DanglingBaseline.Count)
    Write-Output ("NOTE 基线已低于实际：以下 {0} 处悬空类名已被清理，请用 -DumpBaseline 下调 -DanglingBaseline：" -f $resolved.Count)
    $resolved | Sort-Object | ForEach-Object { Write-Output "  $_" }
    exit 0
}

Write-Output ("PASS 前端样式类：{0} 处 erp-* 类名引用全部有定义（{1} 个 CSS 文件 / {2} 个已定义类，已知悬空存量 {3} 处、无新增）。" -f $usedCount, $cssFiles.Count, $defined.Count, $DanglingBaseline.Count)
exit 0
