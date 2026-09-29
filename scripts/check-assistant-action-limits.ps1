<#
.SYNOPSIS
  助手动作阈值门禁：阈值只声明一次（业务代码里不得写死），红线开关恒为开且覆盖尝试启动即失败。

.DESCRIPTION
  三件事各断一处：

    1. **阈值单一事实源**：阈值只在 AssistantActionLimits.cs 里声明；
       动作层与配置写的实现文件里出现同值的数字字面量即 FAIL（那意味着某一处又埋了一个可调参数）；
    2. **红线恒为开**：预演 / 幂等 / 越权上限 / 批核族条目四条红线必须是固定取值，
       且可绑定选项类型上不得出现它们的可写属性（否则配置层就能把它们关掉）；
    3. **fail-fast 已接线**：Program.cs 必须注册阈值校验器并开启启动期校验——
       校验器没接线，等于红线"写了但没人管"。

  判别性：在业务代码里写死一个阈值，或在选项类型上加一个可写的红线开关，本脚本必须红。

.PARAMETER Root
  仓库根目录，缺省按脚本位置向上推导。

.PARAMETER SelfTest
  正反自检：用合成样本断言"该抓的抓得到、干净的不误报"，不读仓库文件。

.EXAMPLE
  pwsh scripts/check-assistant-action-limits.ps1
  pwsh scripts/check-assistant-action-limits.ps1 -SelfTest
#>
[CmdletBinding()]
param(
    [string] $Root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path,
    [switch] $SelfTest
)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8

$limitsPath = Join-Path $Root 'EOS.API/Features/Assistant/Governance/AssistantActionLimits.cs'
$programPath = Join-Path $Root 'EOS.API/Program.cs'

# 红线：名字 → 必须的取值（bool 用小写字面量，int 用字符串比较）
$redLines = [ordered]@{
    'DryRunRequired'           = 'true'
    'IdempotencyRequired'      = 'true'
    'MaxUnauthorizedActions'   = '0'
    'ApprovalFamilyActionCount' = '0'
}

# 阈值扫描范围：动作层与配置写的实现文件（登记在这里是无意埋第二个阈值的入口）
$scanRelativePaths = @(
    'EOS.API/Features/Assistant/Actions',
    'EOS.API/Features/Assistant/Governance',
    'EOS.API/Features/Assistant/Tools/RecordActionTools.cs',
    'EOS.API/Features/Assistant/Tools/ConfigWriteTools.cs',
    'EOS.API/Features/Assistant/Tools/ApprovalRequestTools.cs',
    'EOS.API/Features/Assistant/Config/ConfigClonePlanner.cs'
)

# 范围内与"动作阈值"无关的默认值：模型成本治理上限等，另有各自主张（不参与本门禁的取值比对）
$scanExclusions = @(
    'EOS.API/Features/Assistant/Governance/AssistantGovernance.cs'
)

function Remove-CodeNoise {
    param([Parameter(Mandatory)][string] $Text)

    # 先去掉字符串与字符字面量，再去注释：顺序反了会把注释里的引号当成字符串开头
    $clean = [regex]::Replace($Text, '@"([^"]|"")*"', '""')
    $clean = [regex]::Replace($clean, '"[^"\\\r\n]*(?:\\.[^"\\\r\n]*)*"', '""')
    $clean = [regex]::Replace($clean, "'(?:\\.[^'\r\n]|[^'\\\r\n])'", "''")
    $clean = [regex]::Replace($clean, '(?s)/\*.*?\*/', ' ')
    $clean = [regex]::Replace($clean, '//[^\r\n]*', ' ')
    return $clean
}

function Get-ThresholdValues {
    param([Parameter(Mandatory)][string] $Text)

    $values = New-Object System.Collections.Generic.List[int]
    foreach ($match in [regex]::Matches($Text, '(?m)^\s*public\s+const\s+int\s+(Max\w+)\s*=\s*(-?\d+)\s*;')) {
        $value = [int]$match.Groups[2].Value
        # 1 这种最小值不参与"写死的阈值"扫描：它在代码里到处都是，扫描只会制造噪音
        if ($value -ge 2) { $values.Add($value) }
    }
    return $values.ToArray()
}

function Get-RedLineFindings {
    param([Parameter(Mandatory)][string] $Text, [Parameter(Mandatory)][hashtable] $RedLines)

    $problems = New-Object System.Collections.Generic.List[string]
    foreach ($name in $RedLines.Keys) {
        $expected = $RedLines[$name]
        $match = [regex]::Match($Text, "(?m)^\s*public\s+const\s+(?:bool|int)\s+$name\s*=\s*([A-Za-z0-9]+)\s*;")
        if (-not $match.Success) {
            $problems.Add("红线 $name 没有以常量声明：它必须没有'配成关'的表达方式。")
            continue
        }
        if ($match.Groups[1].Value -cne $expected) {
            $problems.Add("红线 $name 的取值是 $($match.Groups[1].Value)，必须是 $expected。")
        }
    }
    return $problems.ToArray()
}

function Get-OptionsOverrideFindings {
    param([Parameter(Mandatory)][string] $Text)

    $problems = New-Object System.Collections.Generic.List[string]
    $pattern = 'public\s+(?:bool|int)\s+(?:DryRun|Idempotency|MaxUnauthorized|UnauthorizedAction|ApprovalFamily)\w*\s*\{\s*get;\s*set;'
    foreach ($match in [regex]::Matches($Text, $pattern)) {
        $problems.Add("可绑定选项类型上出现了可写的红线属性（$($match.Value)）：配置层因此能把它关掉。")
    }
    return $problems.ToArray()
}

function Get-MagicNumberFindings {
    param(
        [Parameter(Mandatory)][string] $Text,
        [Parameter(Mandatory)][int[]] $Values,
        [Parameter(Mandatory)][string] $FileName
    )

    $problems = New-Object System.Collections.Generic.List[string]
    $code = Remove-CodeNoise -Text $Text
    foreach ($value in $Values) {
        $pattern = '(?<![\w.])' + $value + '(?![\w.])'
        foreach ($match in [regex]::Matches($code, $pattern)) {
            $line = ($code.Substring(0, $match.Index) -split "`n").Count
            $problems.Add("$FileName 第 $line 行写死了阈值 $value：阈值只能来自 AssistantActionLimits。")
        }
    }
    return $problems.ToArray()
}

if ($SelfTest) {
    $limitsSample = @'
        public const int MaxRowsPerAction = 50;
        public const int MaxAuditResourceKeys = 20;
        public const bool DryRunRequired = true;
        public const bool IdempotencyRequired = true;
        public const int MaxUnauthorizedActions = 0;
        public const int ApprovalFamilyActionCount = 0;
'@
    $brokenRedLine = $limitsSample.Replace('public const bool DryRunRequired = true;', 'public const bool DryRunRequired = false;')
    $values = @(Get-ThresholdValues -Text $limitsSample)

    $magicSample = @'
        // 注释里的 50 不算：覆盖率 100% 这种说明文字不该被当成阈值
        var rows = request.Rows.Take(50);
        var snapshot = AssistantActionLimits.MaxAuditResourceKeys;
'@
    $cleanSample = @'
        var rows = request.Rows.Take(AssistantActionLimits.MaxRowsPerAction);
        var keys = row.Keys.Take(AssistantActionLimits.MaxAuditResourceKeys);
'@
    $optionsDirty = @'
        public sealed class AssistantActionLimitsOptions
        {
            public bool DryRunEnabled { get; set; }
        }
'@
    $optionsClean = @'
        public sealed class AssistantActionLimitsOptions
        {
            public int MaxRowsPerAction { get; set; } = AssistantActionLimits.MaxRowsPerAction;
        }
'@

    $cases = @(
        [pscustomobject]@{ Name = '阈值声明齐全'; Expect = 0; Actual = @(Get-RedLineFindings -Text $limitsSample -RedLines $redLines).Count },
        [pscustomobject]@{ Name = '红线被改成关'; Expect = 1; Actual = @(Get-RedLineFindings -Text $brokenRedLine -RedLines $redLines).Count },
        [pscustomobject]@{ Name = '业务代码写死阈值'; Expect = 1; Actual = @(Get-MagicNumberFindings -Text $magicSample -Values $values -FileName 'sample.cs').Count },
        [pscustomobject]@{ Name = '业务代码引用常量'; Expect = 0; Actual = @(Get-MagicNumberFindings -Text $cleanSample -Values $values -FileName 'sample.cs').Count },
        [pscustomobject]@{ Name = '选项类型带可写红线'; Expect = 1; Actual = @(Get-OptionsOverrideFindings -Text $optionsDirty).Count },
        [pscustomobject]@{ Name = '选项类型只放阈值'; Expect = 0; Actual = @(Get-OptionsOverrideFindings -Text $optionsClean).Count }
    )

    $failed = 0
    foreach ($case in $cases) {
        if ($case.Actual -ne $case.Expect) {
            $failed++
            Write-Output ("FAIL 自检[{0}] 期望 {1} 条问题，实得 {2} 条" -f $case.Name, $case.Expect, $case.Actual)
        }
        else {
            Write-Output ("PASS 自检[{0}] 实得 {1} 条问题（期望 {2}）" -f $case.Name, $case.Actual, $case.Expect)
        }
    }

    if ($values.Count -lt 2) { Write-Output 'FAIL 自检：样本里没解析出阈值，扫描形同虚设。'; exit 1 }
    if ($failed -gt 0) { Write-Output ("FAIL 自检：{0} 例不符预期。" -f $failed); exit 1 }
    Write-Output '-- SELFTEST OK'
    exit 0
}

foreach ($path in @($limitsPath, $programPath)) {
    if (-not (Test-Path -LiteralPath $path)) {
        Write-Output "FAIL 找不到文件：$path"
        exit 1
    }
}

$problems = New-Object System.Collections.Generic.List[string]
$limitsText = Get-Content -LiteralPath $limitsPath -Raw -Encoding utf8
foreach ($line in @(Get-RedLineFindings -Text $limitsText -RedLines $redLines)) { $problems.Add($line) }
foreach ($line in @(Get-OptionsOverrideFindings -Text $limitsText)) { $problems.Add($line) }

$values = @(Get-ThresholdValues -Text $limitsText)
if ($values.Count -eq 0) {
    $problems.Add("阈值文件里没有解析出任何阈值：单一事实源可能是空的。")
}

$scanned = 0
foreach ($relative in $scanRelativePaths) {
    $target = Join-Path $Root $relative
    if (-not (Test-Path -LiteralPath $target)) {
        $problems.Add("扫描范围里登记的路径不存在：$relative")
        continue
    }
    $files = if ((Get-Item -LiteralPath $target).PSIsContainer) {
        @(Get-ChildItem -LiteralPath $target -Recurse -File -Filter '*.cs')
    }
    else {
        @(Get-Item -LiteralPath $target)
    }
    foreach ($file in $files) {
        if ($file.FullName -eq $limitsPath) { continue }
        $fileRelative = $file.FullName.Substring($Root.Length).TrimStart('\', '/').Replace('\', '/')
        if ($scanExclusions -contains $fileRelative) { continue }
        $scanned++
        $code = Get-Content -LiteralPath $file.FullName -Raw -Encoding utf8
        $relativeName = $file.FullName.Substring($Root.Length).TrimStart('\', '/')
        foreach ($line in @(Get-MagicNumberFindings -Text $code -Values $values -FileName $relativeName)) { $problems.Add($line) }
    }
}

# fail-fast 必须真的接线：校验器没注册，等于"试图覆盖也没人拦"
$programText = Get-Content -LiteralPath $programPath -Raw -Encoding utf8
if ($programText.IndexOf('AssistantActionLimitsValidator', [StringComparison]::Ordinal) -lt 0) {
    $problems.Add("Program.cs 未注册 AssistantActionLimitsValidator：红线覆盖尝试不会在启动期失败。")
}
if ($programText.IndexOf('ValidateOnStart', [StringComparison]::Ordinal) -lt 0) {
    $problems.Add("Program.cs 未开启 ValidateOnStart：阈值校验不会在启动期执行。")
}

if ($problems.Count -gt 0) {
    Write-Output '== 助手动作阈值门禁 =='
    foreach ($line in $problems) { Write-Output "  [FAIL] $line" }
    Write-Output "-- FAIL（扫描 $scanned 个文件，阈值 $($values -join '/')）"
    exit 1
}

Write-Output "PASS 助手动作阈值：阈值 $($values -join '/') 只来自单一事实源，红线恒为开，覆盖尝试启动即失败（扫描 $scanned 个文件）。"
exit 0
