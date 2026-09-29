<#
.SYNOPSIS
    统一工作台策略层单入口门禁：授权/名单/幂等判定不得回到控制器，且策略服务必须是共享入口。

.DESCRIPTION
    统一工作台（单据录入与浏览界面）的授权判定原先分散在控制器的若干私有方法里，
    同一个"能不能以某模式访问该模块"的判定被写了两遍，连失败响应形态都不一致。
    为将来把同一套判定复用于助手侧的"哪些单可做"预判，判定必须收敛到**唯一入口**
    （`WorkbenchAccessPolicy`），控制器只消费判定结果。

    本门禁把这件事固定成可机械检查的事实，而不是约定：

      ① **控制器不持有判定记号**——`rights.CanAddNew` / `FormWritable(` / `HasWritablePage(` /
         `IsUnifiedFormRoute(` / `CanSetup` 这类决策记号只允许出现在策略服务；控制器里再出现
         任一记号即 FAIL 并点名（行号 + 记号）。白名单为空且是显式的：确需豁免要在这里写明理由。
      ② **策略服务是共享入口**——除控制器之外，必须另有至少一个消费方（测试或助手侧入口）
         引用它；只有控制器引用时它不过是"改了名字的私有方法"，判定仍会再度分叉。
      ③ **策略服务已在 Program.cs 注册**——漏注册只在运行时炸（端点全 500），编译与单测都发现不了。

    只做静态文本检查，不连库、不改任何文件。-SelfTest 用合成探针做正反自检。

.EXAMPLE
    pwsh scripts/check-workbench-policy-single-entry.ps1            # exit 0 = clean
    pwsh scripts/check-workbench-policy-single-entry.ps1 -SelfTest  # 正反自检：埋一个判定记号必须被判 FAIL
#>
[CmdletBinding()]
param(
    [string] $Root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path,
    [switch] $SelfTest
)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8

# 判定记号：这些字符串代表"谁能在什么条件下做什么"的决策本身，只允许出现在策略服务里。
$decisionTokens = @(
    'rights.CanBrowse',
    'rights.CanAddNew',
    'rights.CanEdit',
    'rights.CanDelete',
    'rights.CanSetup',
    'FormWritable(',
    'FormReadOnly(',
    'HasWritablePage(',
    'IsUnifiedFormRoute(',
    'WithoutWriteActions(',
    'FormAccess(',
    'ActionAccess(',
    'IdempotencyProblem(',
    'SetupDefinition(',
    'AuthorizedDefinition(',
    'EffectiveDataFilter(',
    'ModuleRouteValidator.'
)

# 显式白名单：确需留在控制器的判定记号（当前为空——判定已全部下沉，控制器只剩对策略入口的调用）。
# 加入任何一项都要在这里附一句理由，否则等于门禁失效。
$controllerExemptions = @()

$policyType = 'WorkbenchAccessPolicy'
$policyRelative = 'EOS.API/Data/Workbench/WorkbenchAccessPolicy.cs'
$controllerRelative = 'EOS.API/Controllers/DocumentWorkbenchController.cs'
$programRelative = 'EOS.API/Program.cs'

function Get-DecisionViolations {
    param(
        [Parameter(Mandatory)] [string] $Path,
        [Parameter(Mandatory)] [string[]] $Tokens,
        [string[]] $Exemptions = @()
    )
    $violations = New-Object System.Collections.Generic.List[string]
    if (-not (Test-Path -LiteralPath $Path)) { return $violations }
    $text = Get-Content -Raw -Encoding UTF8 $Path
    $name = [System.IO.Path]::GetFileName($Path)
    foreach ($token in $Tokens) {
        if ($Exemptions -contains $token) { continue }
        # 前置词边界：`MyFormWritable(` 不是 `FormWritable(`，不误报。
        $pattern = '(?<![A-Za-z0-9_])' + [regex]::Escape($token)
        foreach ($match in [regex]::Matches($text, $pattern)) {
            $line = ($text.Substring(0, $match.Index) -split "`n").Count
            $violations.Add("$name" + ":$line 命中判定记号 $token")
        }
    }
    return $violations
}

function Get-PolicyConsumers {
    param(
        [Parameter(Mandatory)] [string] $RootPath,
        [Parameter(Mandatory)] [string] $TypeName,
        [Parameter(Mandatory)] [string[]] $ExcludedRelative
    )
    $consumers = New-Object System.Collections.Generic.List[string]
    foreach ($scanRoot in @('EOS.API', 'EOS.API.Tests')) {
        $directory = Join-Path $RootPath $scanRoot
        if (-not (Test-Path -LiteralPath $directory)) { continue }
        foreach ($file in Get-ChildItem -LiteralPath $directory -Recurse -File -Filter '*.cs') {
            if ($file.FullName -match '[\\/](bin|obj)[\\/]') { continue }
            $relative = [System.IO.Path]::GetRelativePath($RootPath, $file.FullName).Replace('\', '/')
            if ($ExcludedRelative -contains $relative) { continue }
            if ((Get-Content -Raw -Encoding UTF8 $file.FullName) -match "(?<![A-Za-z0-9_])$([regex]::Escape($TypeName))(?![A-Za-z0-9_])") {
                $consumers.Add($relative)
            }
        }
    }
    return $consumers
}

$controllerPath = Join-Path $Root $controllerRelative
$policyPath = Join-Path $Root $policyRelative
$programPath = Join-Path $Root $programRelative

if (-not (Test-Path -LiteralPath $policyPath)) {
    Write-Host "FAIL 未找到策略服务：$policyRelative"
    exit 1
}

if ($SelfTest) {
    $probeDir = Join-Path ([System.IO.Path]::GetTempPath()) "eos-policy-gate-selftest-$([Guid]::NewGuid().ToString('N'))"
    $null = New-Item -ItemType Directory -Path $probeDir
    try {
        # 反向自检：控制器里出现判定记号，必须被抓住并点名。
        $dirty = Join-Path $probeDir 'DirtyController.cs'
        Set-Content -LiteralPath $dirty -Encoding UTF8 -Value @'
public sealed class DocumentWorkbenchController
{
    private bool Gate(int moduleId) => FormWritable(moduleId) || rights.CanAddNew;
}
'@
        $found = @(Get-DecisionViolations -Path $dirty -Tokens $decisionTokens -Exemptions $controllerExemptions)
        if ($found.Count -eq 0) { throw '自检失败：埋入的判定记号没有被门禁发现（门禁形同虚设）。' }
        if (-not (($found -join ' ') -match 'FormWritable\(')) { throw '自检失败：门禁报了问题，但没有点名 FormWritable(' }
        if (-not (($found -join ' ') -match 'rights\.CanAddNew')) { throw '自检失败：门禁报了问题，但没有点名 rights.CanAddNew' }
        if (-not (($found -join ' ') -match 'DirtyController\.cs:3')) { throw "自检失败：命中行号不对 ⇒ $($found -join ' | ')" }
        Write-Host '  [PASS] 反向自检：控制器里的判定记号被判 FAIL 并点名行号'

        # 正向自检：控制器只调用策略入口（含同名后缀的其它标识符），不得误报。
        $clean = Join-Path $probeDir 'CleanController.cs'
        Set-Content -LiteralPath $clean -Encoding UTF8 -Value @'
public sealed class DocumentWorkbenchController
{
    private Task<object> Call(int moduleId) => policy.AuthorizeDefinitionAsync(UserId, moduleId, token);
    private bool MyFormWritableHelper(int moduleId) => true;
    private bool HasCanEditFlag => true;
}
'@
        $cleanFound = @(Get-DecisionViolations -Path $clean -Tokens $decisionTokens -Exemptions $controllerExemptions)
        if ($cleanFound.Count -gt 0) { throw "自检失败：薄委托文件被误报 ⇒ $($cleanFound -join ' | ')" }
        Write-Host '  [PASS] 正向自检：只调策略入口的文件零命中（同名后缀标识符不误报）'
        Write-Host '-- SELFTEST OK'
    }
    finally {
        Remove-Item -LiteralPath $probeDir -Recurse -Force -ErrorAction SilentlyContinue
    }
    exit 0
}

$problems = New-Object System.Collections.Generic.List[string]

# ① 控制器不持有判定记号
foreach ($violation in (Get-DecisionViolations -Path $controllerPath -Tokens $decisionTokens -Exemptions $controllerExemptions)) {
    $problems.Add("$controllerRelative：$violation（判定必须落在策略服务 $policyRelative，控制器只消费判定结果）")
}

# ② 策略服务是共享入口：控制器 + 至少一个其它消费方
$consumers = @(Get-PolicyConsumers -RootPath $Root -TypeName $policyType -ExcludedRelative @($policyRelative, $programRelative))
if (-not ($consumers -contains $controllerRelative)) {
    $problems.Add("$controllerRelative 未引用 $policyType：控制器与策略入口断链（判定又会分叉）")
}
$otherConsumers = @($consumers | Where-Object { $_ -ne $controllerRelative })
if ($otherConsumers.Count -eq 0) {
    $problems.Add("$policyType 只有控制器一个消费方：它必须同时被测试或助手侧入口引用，" +
                  "否则无法证明是共享入口，而不是改了名字的私有方法")
}

# ③ DI 注册（允许命名空间限定：AddScoped<Data.Workbench.WorkbenchAccessPolicy>()）
$program = Get-Content -Raw -Encoding UTF8 $programPath
if ($program -notmatch "Add(Scoped|Singleton|Transient)<[^<>]*$([regex]::Escape($policyType))>") {
    $problems.Add("$programRelative 未注册 $policyType：漏注册只在运行时炸（相关端点全 500），编译与单测都发现不了")
}

if ($problems.Count -gt 0) {
    Write-Host '== 统一工作台策略层单入口门禁 =='
    foreach ($line in $problems) { Write-Host "  [FAIL] $line" }
    Write-Host "-- FAIL（判定记号 $($decisionTokens.Count) 个，显式豁免 $($controllerExemptions.Count) 个，消费方 $($consumers.Count) 个）"
    exit 1
}

Write-Host "PASS 统一工作台策略层单入口：控制器无判定记号（豁免 $($controllerExemptions.Count) 个），$policyType 消费方 $($consumers.Count) 个（$($consumers -join ', ')），已注册 DI。"
exit 0
