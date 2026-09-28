<#
.SYNOPSIS
    配置面说明巡检：每个效果键与每种反向 kind 都必须有人话说明，库内在用的效果键必须都在目录内。

.DESCRIPTION
    配置面把实现层的键直接摆给配置者看。目录只回答"叫什么"（BusinessActionLabels.EffectKeys
    的一行短语），回答不了"这个键在单据上做什么""解批到底怎么反悔"——而这两件事正是配置者
    敢不敢下手的分水岭。本次把说明补成目录的一部分（EffectKeyDescriptions /
    ReverseKindDescriptions），本脚本把它固化为常态巡检，避免新增键时说明再次缺席。

    三条判据：

    ① 目录里的每个效果键都有**非空**说明（空串等于没有——界面上会露出英文码）。
    ② 库内**在用的**效果键必须都在目录里（MANUAL 行的键是自定义按钮，归操作注册表管，不在本题内）。
       反向不要求：目录里的保留键（meta-link / flow-trigger / job-enqueue）本来就允许没有实例。
    ③ EffectStructSchemas.AllReverseKinds() 的每个取值都有非空说明。

    -SelfTest 做判别力自检：把同一套提取与判定跑在合成样本上，缺失与空说明必须被抓住，
    齐全时不得误报。只读源码与库，不改任何数据。

.EXAMPLE
    pwsh scripts/check-effect-descriptions.ps1            # exit 0 = 说明齐全且无目录外在用键
    pwsh scripts/check-effect-descriptions.ps1 -SelfTest   # 额外做正反自检
    pwsh scripts/check-effect-descriptions.ps1 -SkipDatabase
#>
[CmdletBinding()]
param(
    [string] $Root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path,
    [string] $ConnectionString,
    [switch] $SelfTest,
    [switch] $SkipDatabase
)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8

function Get-CSharpBlock {
    param([string] $Source, [string] $Block)
    $match = [regex]::Match($Source, '(?s)' + $Block + '\s*=\s*new\s*[^{]*\{([^}]*)\}')
    if (-not $match.Success) { throw "C# block not found: $Block" }
    return $match.Groups[1].Value
}

function Get-BlockKeys {
    param([string] $Source, [string] $Block)
    @([regex]::Matches((Get-CSharpBlock -Source $Source -Block $Block), '"([a-z][a-z0-9-]*)"') |
        ForEach-Object { $_.Groups[1].Value } | Sort-Object -Unique)
}

function Get-MissingDescriptions {
    param([string] $LabelsSource, [string] $Block, [string[]] $Keys)
    $block = Get-CSharpBlock -Source $LabelsSource -Block $Block
    $missing = @()
    $blank = @()
    foreach ($key in $Keys) {
        $entry = [regex]::Match($block, '\["' + [regex]::Escape($key) + '"\]\s*=\s*"([^"]*)"')
        if (-not $entry.Success) {
            $missing += $key
            continue
        }
        if ($entry.Groups[1].Value.Trim() -eq '') {
            $blank += $key
        }
    }
    return @{ Missing = $missing; Blank = $blank }
}

function Test-Self {
    $fakeCatalog = @'
public static readonly IReadOnlySet<string> EffectKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
{
    "alpha",
    "beta",
};
'@
    $fakeComplete = @'
public static readonly IReadOnlyDictionary<string, string> EffectKeyDescriptions =
    new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["alpha"] = "甲说明",
        ["beta"] = "乙说明",
    };
'@
    $fakeBroken = @'
public static readonly IReadOnlyDictionary<string, string> EffectKeyDescriptions =
    new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["alpha"] = "",
        ["beta"] = "乙说明",
    };
'@
    $keys = Get-BlockKeys -Source $fakeCatalog -Block 'EffectKeys'
    if ($keys.Count -ne 2) {
        return "SelfTest 提取失败：期望 2 个键，实得 $($keys.Count)"
    }
    $ok = Get-MissingDescriptions -LabelsSource $fakeComplete -Block 'EffectKeyDescriptions' -Keys $keys
    if ($ok.Missing.Count -ne 0 -or $ok.Blank.Count -ne 0) {
        return "SelfTest 误报：说明齐全时期望 0 命中，实得 missing=$($ok.Missing.Count) blank=$($ok.Blank.Count)"
    }
    $bad = Get-MissingDescriptions -LabelsSource $fakeBroken -Block 'EffectKeyDescriptions' -Keys $keys
    if ($bad.Blank.Count -ne 1 -or $bad.Blank[0] -ne 'alpha') {
        return "SelfTest 漏报：空说明必须被抓住，实得 blank=$($bad.Blank -join ',')"
    }
    $absent = Get-MissingDescriptions -LabelsSource $fakeComplete -Block 'EffectKeyDescriptions' -Keys @('alpha', 'gamma')
    if ($absent.Missing.Count -ne 1 -or $absent.Missing[0] -ne 'gamma') {
        return "SelfTest 漏报：缺说明的键必须被抓住，实得 missing=$($absent.Missing -join ',')"
    }
    return $null
}

if ($SelfTest) {
    $failure = Test-Self
    if ($failure) {
        Write-Output "FAIL -SelfTest 判别力不符：$failure"
        exit 3
    }
    Write-Output 'PASS -SelfTest 判别力：缺说明与空说明都被抓住，说明齐全时不误报。'
}

$catalogSource = Get-Content (Join-Path $Root 'EOS.API\Data\BusinessActionCatalog.cs') -Raw
$labelsSource = Get-Content (Join-Path $Root 'EOS.API\Data\BusinessActionLabels.cs') -Raw
$schemasSource = Get-Content (Join-Path $Root 'EOS.API\Data\EffectStructSchemas.cs') -Raw

$catalogEffects = Get-BlockKeys -Source $catalogSource -Block 'EffectKeys'
$reverseKinds = Get-BlockKeys -Source $schemasSource -Block 'ReverseKinds'

$effects = Get-MissingDescriptions -LabelsSource $labelsSource -Block 'EffectKeyDescriptions' -Keys $catalogEffects
$kinds = Get-MissingDescriptions -LabelsSource $labelsSource -Block 'ReverseKindDescriptions' -Keys $reverseKinds

$failures = @()

if ($effects.Missing.Count -gt 0) {
    $failures += "效果键缺说明（界面只能露出英文码）：$($effects.Missing -join ', ')"
}
if ($effects.Blank.Count -gt 0) {
    $failures += "效果键说明为空串（等于没有说明）：$($effects.Blank -join ', ')"
}
if ($kinds.Missing.Count -gt 0) {
    $failures += "反向 kind 缺说明（配置者无从知道解批会发生什么）：$($kinds.Missing -join ', ')"
}
if ($kinds.Blank.Count -gt 0) {
    $failures += "反向 kind 说明为空串：$($kinds.Blank -join ', ')"
}

if (-not $SkipDatabase) {
    try {
        . (Join-Path $Root 'scripts\dev\eos-sql.ps1')
        # MANUAL 行的键是自定义按钮，归操作注册表（DocumentActionRegistry）管，不在效果目录内。
        $rows = @(Invoke-EosSqlQuery -ConnectionString $ConnectionString -Query @"
SELECT DISTINCT LTRIM(RTRIM(a.EFFECT_KEY)) FROM dbo.MODULE_BUSINESS_ACTION a
WHERE a.EFFECT_KEY IS NOT NULL AND LTRIM(RTRIM(a.EFFECT_KEY)) <> ''
  AND LTRIM(RTRIM(ISNULL(a.EVENT_CODE,''))) <> 'MANUAL';
"@ | Where-Object { $_ -and $_.Trim() -ne '' } | ForEach-Object { $_.Trim() })
        $unknown = @($rows | Where-Object { $_ -notin $catalogEffects })
        if ($unknown.Count -gt 0) {
            $failures += "库内配置的效果键不在目录内（界面无从渲染其名称与说明）：$($unknown -join ', ')"
        }
        Write-Output "INFO 库内在用效果键 $($rows.Count) 个，目录内效果键 $($catalogEffects.Count) 个，反向 kind $($reverseKinds.Count) 个。"
    }
    catch {
        Write-Output "FAIL 库内在用效果键巡检失败：$($_.Exception.Message)"
        exit 2
    }
}

if ($failures.Count -gt 0) {
    Write-Output 'FAIL 配置面说明不齐全：'
    $failures | ForEach-Object { Write-Output "  $_" }
    exit 1
}

Write-Output "PASS 效果键说明齐全（$($catalogEffects.Count) 个）、反向 kind 说明齐全（$($reverseKinds.Count) 个），库内在用键均在目录内。"
exit 0
