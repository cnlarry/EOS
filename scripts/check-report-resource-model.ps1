<#
.SYNOPSIS
    报表资源模型门禁：汇总报表的聚合列必须逐列声明字段级权限位。

.DESCRIPTION
    汇总报表（ReportAggregateRegistry）的列是 SQL 算出来的派生列，不属于任何物理表，
    因此没有 FIELDS 行可供反查成本位/保密位/拒绝名单——权限只能在注册表里逐列声明。
    运行期的口径是"未声明即丢弃"（fail-closed），但那样只会表现为**静默少列**：
    客户端不报错、接口不 403，谁也不会发现某张汇总报表悄悄丢了成本列。

    所以这条规则必须在构建期拦下：注册表里每一次 ReportColumn 构造都要显式写出
    IsCost 与 IsSecrecy 两个命名实参，取值为 true/false 字面量。

    静态扫描，不连库、不连 API。

.PARAMETER SelfTest
    判别力自检：对合成源码样本跑同一套分析——合规样本必须通过，
    缺 IsSecrecy、缺 IsCost、用位置实参冒充、把权限位写成非字面量四类必须逐条命中。
    样本里含"括号出现在字符串字面量内"的用例，用于验证扫描按字符串感知配对括号。

.EXAMPLE
    pwsh scripts/check-report-resource-model.ps1
    pwsh scripts/check-report-resource-model.ps1 -SelfTest
#>
[CmdletBinding()]
param(
    [switch] $SelfTest
)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8

$script:RegistryPath = Join-Path (Split-Path -Parent $PSScriptRoot) 'EOS.API\Data\ReportAggregateRegistry.cs'

function Get-EosReportColumnArguments {
    <#
     从源码文本里抽出每一次 `new ReportColumn(` 的实参文本。
     按字符串感知的方式配对括号：`"占比(%)"`、`"a)b"` 这类字面量里的括号不算嵌套层级——
     否则一个标签写成"单价(元/件"就会把后续整段源码吞进"实参"里，门禁从此静默失明。
    #>
    param([Parameter(Mandatory)][string] $Text)

    $results = @()
    $marker = 'new ReportColumn('
    $searchFrom = 0
    while ($true) {
        $start = $Text.IndexOf($marker, $searchFrom, [StringComparison]::Ordinal)
        if ($start -lt 0) { break }
        $cursor = $start + $marker.Length
        $depth = 1
        $inString = $false
        while ($cursor -lt $Text.Length -and $depth -gt 0) {
            $ch = $Text[$cursor]
            if ($inString) {
                if ($ch -eq '"') {
                    # 连续两个引号是转义引号，不结束字符串
                    if ($cursor + 1 -lt $Text.Length -and $Text[$cursor + 1] -eq '"') { $cursor += 2; continue }
                    $inString = $false
                }
                $cursor++
                continue
            }
            if ($ch -eq '"') { $inString = $true; $cursor++; continue }
            if ($ch -eq '(') { $depth++ }
            elseif ($ch -eq ')') { $depth-- }
            $cursor++
        }
        if ($depth -ne 0) {
            # 括号不配平：把剩余全部当作一个待检实参，交由调用方判失败（宁可报错，不可静默）
            $results += , @{ Index = $start; Arguments = $Text.Substring($start) }
            break
        }
        $results += , @{ Index = $start; Arguments = $Text.Substring($start + $marker.Length, $cursor - $start - $marker.Length - 1) }
        $searchFrom = $cursor
    }
    return $results
}

function Test-EosColumnPrivilegeDeclared {
    <# 单条实参文本是否显式声明了 IsCost 与 IsSecrecy 两个布尔字面量。 #>
    param([Parameter(Mandatory)][string] $Arguments)

    $missing = @()
    foreach ($flag in @('IsCost', 'IsSecrecy')) {
        if ($Arguments -notmatch "(?<![A-Za-z0-9_])$flag\s*:\s*(true|false)\b") { $missing += $flag }
    }
    return $missing
}

function Get-EosReportColumnViolations {
    <# 返回违规清单（每条含行号与说明）。 #>
    param([Parameter(Mandatory)][string] $Text)

    $violations = @()
    foreach ($call in (Get-EosReportColumnArguments -Text $Text)) {
        $missing = Test-EosColumnPrivilegeDeclared -Arguments $call.Arguments
        if (-not $missing) { continue }
        # 行号按 marker 位置折算：源码扫描只用于定位，不要求精确到列
        $line = ($Text.Substring(0, $call.Index) -split "`n").Count
        $trimmed = ($call.Arguments -replace '\s+', ' ').Trim()
        if ($trimmed.Length -gt 90) { $trimmed = $trimmed.Substring(0, 90) + '…' }
        $violations += [pscustomobject]@{
            Line    = $line
            Missing = ($missing -join '/')
            Snippet = $trimmed
        }
    }
    return $violations
}

# ---------------------------------------------------------------- 形态门禁：不得新增 /reports 型模块
function Get-EosModuleInsertStatements {
    <#
      抽出所有 `INSERT INTO ... MODULES ...` 语句文本（到分号或 GO 行为止）。
      只按语句范围判断，不做全文关键词匹配：迁移里合法出现 `'/reports'` 的地方
      （清理脚本的 WHERE、注释、留底说明）不是"新增模块"，混在一起报就是误报，误报多了门禁就没人看。
    #>
    param([Parameter(Mandatory)][string] $Text)

    $statements = @()
    $pattern = [regex]'(?is)INSERT\s+INTO\s+[\[\]\w\.]*MODULES\b'
    foreach ($match in $pattern.Matches($Text)) {
        $rest = $Text.Substring($match.Index)
        $end = [regex]::Match($rest, '(?is);|^\s*GO\s*$')
        $body = if ($end.Success) { $rest.Substring(0, $end.Index) } else { $rest }
        $statements += , $body
    }
    return $statements
}

function Get-EosCarrierModuleInsertViolations {
    <# 新增 MODULES 行时把地址写成 /reports ⇒ 报表又被当模块建了出来（这正是本次要根除的形态）。 #>
    param(
        [Parameter(Mandatory)][string] $Text,
        [Parameter(Mandatory)][string] $Path
    )

    $violations = @()
    foreach ($body in (Get-EosModuleInsertStatements -Text $Text)) {
        if ($body -match "'/reports'") {
            $line = ($Text.Substring(0, $Text.IndexOf($body, [StringComparison]::Ordinal)) -split "`n").Count
            $snippet = ($body -replace '\s+', ' ').Trim()
            if ($snippet.Length -gt 110) { $snippet = $snippet.Substring(0, 110) + '…' }
            $violations += [pscustomobject]@{ Path = $Path; Line = $line; Snippet = $snippet }
        }
    }
    return $violations
}

function Get-EosCarrierModuleInsertViolationsInTree {
    <# 扫迁移与引导数据：这两处是模块行的唯一来源。 #>
    param([Parameter(Mandatory)][string[]] $Roots)

    $violations = @()
    foreach ($root in $Roots) {
        if (-not (Test-Path -LiteralPath $root)) { continue }
        foreach ($file in (Get-ChildItem -LiteralPath $root -Recurse -Filter *.sql -File)) {
            $text = Get-Content -Raw -LiteralPath $file.FullName
            $violations += Get-EosCarrierModuleInsertViolations -Text $text -Path $file.FullName
        }
    }
    return $violations
}

# ---------------------------------------------------------------- 判别力自检
function Test-EosReportModelDiscrimination {
    $compliant = @'
private static readonly ReportAggregate Sample = new(
    "R_1", "SELECT A", "A", [],
    [
        new ReportColumn("A", "单价(元/件", "float", null, IsCost: false, IsSecrecy: false),
        new ReportColumn("B", "含)括(号", "nvarchar", null, IsCost: true, IsSecrecy: false),
    ]);
'@
    $missingSecrecy = @'
new ReportColumn("A", "单价", "float", null, IsCost: true),
'@
    $missingCost = @'
new ReportColumn("A", "单价", "float", null, IsSecrecy: false),
'@
    $positional = @'
new ReportColumn("A", "单价", "float", null, true, false),
'@
    $nonLiteral = @'
new ReportColumn("A", "单价", "float", null, IsCost: IsCostOn, IsSecrecy: false),
'@

    $cases = [ordered]@{
        'COMPLIANT'       = @{ Text = $compliant;       Expect = 0 }
        'MISSING_SECRECY' = @{ Text = $missingSecrecy;  Expect = 1 }
        'MISSING_COST'    = @{ Text = $missingCost;     Expect = 1 }
        'POSITIONAL'      = @{ Text = $positional;      Expect = 1 }
        'NON_LITERAL'     = @{ Text = $nonLiteral;      Expect = 1 }
    }

    $bad = @()
    foreach ($name in $cases.Keys) {
        $case = $cases[$name]
        $actual = @(Get-EosReportColumnViolations -Text $case.Text).Count
        if ($actual -ne $case.Expect) { $bad += "$name(期望 $($case.Expect) 条违规，实得 $actual)" }
    }

    # 形态门禁样本：只抓"新增模块时把地址写成 /reports"，不抓清理脚本里合法出现的同一字面量
    $carrierInsert = @'
INSERT INTO dbo.MODULES (M_IDX, M_DESC, M_URL, M_TAG)
VALUES (140199, N'销售订单明细表', '/reports', 1);
'@
    $cleanInsert = @'
INSERT INTO dbo.MODULES (M_IDX, M_DESC, M_URL, M_TAG)
VALUES (150201, N'制令单', '/workbench', 1);
'@
    $cleanupOnly = @'
DELETE m FROM dbo.MODULES m WHERE LTRIM(RTRIM(ISNULL(m.M_URL, ''))) = '/reports';
PRINT N'承载页 "/reports" 已收敛';
'@

    $carrierCases = [ordered]@{
        'CARRIER_INSERT' = @{ Text = $carrierInsert; Expect = 1 }
        'CLEAN_INSERT'   = @{ Text = $cleanInsert;   Expect = 0 }
        'CLEANUP_ONLY'   = @{ Text = $cleanupOnly;   Expect = 0 }
    }
    foreach ($name in $carrierCases.Keys) {
        $case = $carrierCases[$name]
        # 只在字符类里报单引号不安全，这里用逐字符判断：/reports 必须被单引号包住才算"写进地址"
        $actual = @(Get-EosCarrierModuleInsertViolations -Text $case.Text -Path '<selftest>').Count
        if ($actual -ne $case.Expect) { $bad += "$name(期望 $($case.Expect) 条形态违规，实得 $actual)" }
    }

    if ($bad.Count -gt 0) {
        $script:SelfTestMessage = "-SelfTest 判别力不符：$($bad -join '；')"
        return $false
    }
    $script:SelfTestMessage = '-SelfTest 判别力：合规样本（含字符串内括号）判通过；缺 IsSecrecy、缺 IsCost、位置实参、非字面量取值四类逐条命中；新增 /reports 型模块被点名、而清理脚本里的同一字面量不误报。'
    return $true
}

if ($SelfTest) {
    if (Test-EosReportModelDiscrimination) {
        Write-Output "PASS $script:SelfTestMessage"
        exit 0
    }
    Write-Output "FAIL $script:SelfTestMessage"
    exit 3
}

# ---------------------------------------------------------------- 正检
if (-not (Test-Path -LiteralPath $script:RegistryPath)) {
    Write-Output "FAIL 找不到聚合数据源注册表：$script:RegistryPath"
    exit 2
}

$source = Get-Content -Raw -LiteralPath $script:RegistryPath
$calls = @(Get-EosReportColumnArguments -Text $source)
if ($calls.Count -eq 0) {
    Write-Output "FAIL 注册表中未扫描到任何 ReportColumn 声明：$script:RegistryPath（扫描逻辑或文件结构已变）"
    exit 2
}

$violations = @(Get-EosReportColumnViolations -Text $source)
if ($violations.Count -gt 0) {
    foreach ($item in $violations) {
        Write-Output ("FAIL 第 {0} 行聚合列未声明权限位（缺 {1}）：{2}" -f $item.Line, $item.Missing, $item.Snippet)
    }
    Write-Output ("FAIL 聚合列必须显式声明 IsCost 与 IsSecrecy（未声明即 fail-closed，但不得靠运行期静默丢弃兜底）：{0} 处违规。" -f $violations.Count)
    exit 1
}

Write-Output ("PASS 聚合列 {0} 处均显式声明 IsCost / IsSecrecy。" -f $calls.Count)

# ---------------------------------------------------------------- 形态门禁
$repoRoot = Split-Path -Parent $PSScriptRoot
$shapeRoots = @(
    (Join-Path $repoRoot 'EOS.API\Data\Migrations'),
    (Join-Path $repoRoot 'db\bootstrap')
)
$shapeViolations = @(Get-EosCarrierModuleInsertViolationsInTree -Roots $shapeRoots)
if ($shapeViolations.Count -gt 0) {
    foreach ($item in $shapeViolations) {
        Write-Output ("FAIL 新增 MODULES 行把地址写成 /reports（报表不得再被当模块建出来）：{0}:{1} {2}" -f $item.Path, $item.Line, $item.Snippet)
    }
    Write-Output ("FAIL 报表不进菜单是硬约束；承载页形态一旦重现，归属表就会长出第二套锚点。违规 {0} 处。" -f $shapeViolations.Count)
    exit 1
}
Write-Output ("PASS 迁移与引导数据中无新增 /reports 型模块（扫描 {0} 个目录）。" -f $shapeRoots.Count)

exit 0
