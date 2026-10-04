<#
.SYNOPSIS
    写名单模块的模块过滤条件（`MODULES.FILTER`）必须能被新记录满足。

.DESCRIPTION
    背景（2026-10-04 实测）：`14997 应收未收(明细)` 在统一表单**写名单**里，而它的模块 FILTER 是
    `CONFIRM_TAG=1 AND FINISHED_TAG=0 AND SUM_AMOUNT-RECEIVE_AMOUNT>0`——新记录的 `CONFIRM_TAG`
    恒为 0、未收金额恒为 0，三个谓词一个都满足不了，于是「新增」入口必然报
    `RECORD_OUT_OF_MODULE_FILTER`。写名单里的模块却用不了新增，是用户可见的故障。

    判据（**只守写名单**：只有写名单才承诺"可新增"；只读名单与未入名单的模块不在此题内）：

      1) 批核/结案状态位（`CONFIRM_TAG` / `FINISHED_TAG`）出现在 FILTER 里 ⇒ 一律失败。
         这两个列由引擎在批核/结案时写，用户填不了、新记录恒为 0；"只看已批核记录"的模块
         不可能同时承诺可新增。
      2) 其余列：主表真实列中出现在 FILTER 文本里的每一个，必须有**至少一个**新增时能产生值的来源：
         ① 用户可填：`FIELDS` 行存在且 `IS_VISIBLE=1`、`IS_READONLY=0`、`IS_VIRTUAL=0`；
         ② 字段元数据默认值：`DFT_VALUE` 或 `FORM_OPTIONS` 非空；
         ③ 页面代码默认值规则：`EOS.API/Data/FormDefaultRules.cs` 里该模块登记了该列；
         ④ 物理列默认值：`sys.default_constraints` 有约束（INSERT 省略该列时库侧补齐）。

    为什么用"从主表真实列出发去 FILTER 文本里找"、而不是在 PowerShell 里对 FILTER 分词：
    FILTER 是自由文本，分词要先剥字符串字面量（`'CLQG'` 这类取值会被当成列名）、函数名与 SQL 关键字，
    规则一多就会误判；反过来列名是已知的，不需要猜。文本匹配用 SQL 粗筛（`CHARINDEX`）+
    PowerShell 词边界精筛（`(?<![A-Za-z0-9_])列名(?![A-Za-z0-9_])`），后者可被自检覆盖。

    来源不能只看 FIELDS：`MOC_PRODUCE_M.REWORK_TAG` 之类是**只读可见**列，值由页面代码默认值规则
    （`FormDefaultRules` 的 `[1512]/[2803]` 等）或**物理列默认值**（`((0))`）产生——漏掉这两条来源
    会把正常模块误判为不可满足（首版判据实测误报 6 个模块）。

    `-SelfTest` 做判别力自检：合成样本里"该抓的"（无任何来源的列、批核状态位）必须命中，
    "不该抓的"（五种来源各自成立、以及 `REWORK_TAG` 里不应误抓 `TAG`）必须不命中。
    只跑 SELECT，不改任何数据。

.EXAMPLE
    pwsh scripts/check-module-filter-columns.ps1             # exit 0 = 写名单模块的过滤条件均可满足
    pwsh scripts/check-module-filter-columns.ps1 -SelfTest   # 额外做正反自检
#>
[CmdletBinding()]
param(
    [string] $ConnectionString,
    [switch] $SelfTest
)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$appSettingsPath = Join-Path $repoRoot 'EOS.API/appsettings.json'
$defaultRulesPath = Join-Path $repoRoot 'EOS.API/Data/FormDefaultRules.cs'

$stateBits = @('CONFIRM_TAG', 'FINISHED_TAG')

function Get-WriteList {
    param([string] $Path)
    $app = Get-Content -Raw -LiteralPath $Path -Encoding utf8 | ConvertFrom-Json
    return @($app.UnifiedFormEditor.EnabledModuleIds | ForEach-Object { [string]$_ })
}

# 页面代码默认值规则：模块 → 列集合。唯一真源是 FormDefaultRules.cs，这里只解析、不复写映射。
function Get-CodeDefaults {
    param([string] $Path)
    $text = Get-Content -Raw -LiteralPath $Path -Encoding utf8
    $map = @{}
    foreach ($entry in [regex]::Matches($text, '\[(\d+)\]\s*=\s*new Dictionary<string,\s*string>\s*\{([^}]*)\}')) {
        $mid = $entry.Groups[1].Value
        $columns = @([regex]::Matches($entry.Groups[2].Value, '\["([A-Za-z_][A-Za-z0-9_]*)"\]') |
            ForEach-Object { $_.Groups[1].Value.ToUpperInvariant() })
        if ($columns.Count -gt 0) { $map[$mid] = $columns }
    }
    return $map
}

function Test-WordBoundary {
    param([string] $Filter, [string] $Column)
    return [regex]::IsMatch($Filter, '(?<![A-Za-z0-9_])' + [regex]::Escape($Column) + '(?![A-Za-z0-9_])',
        [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)
}

<#
    判定核心（纯函数，便于自检）：对候选（模块 × 主表列）逐条给出"不可满足"的原因。
    参数：
      Candidates   : @{ ModuleId; Desc; Table; AutoApprove; Filter; Column }
      Fields       : "表|列" → @{ Visible; Readonly; Virtual; HasDefault }
      CodeDefaults : 模块号 → 列名数组
      ColumnDefaults: "表|列" → $true/$false（物理列是否有默认值约束）
#>
function Get-FilterViolations {
    param($Candidates, $Fields, $CodeDefaults, $ColumnDefaults)
    $violations = [System.Collections.Generic.List[object]]::new()
    foreach ($item in $Candidates) {
        $column = $item.Column.ToUpperInvariant()
        if (-not (Test-WordBoundary -Filter $item.Filter -Column $column)) { continue }
        if ($column -in $stateBits) {
            $violations.Add([pscustomobject]@{
                ModuleId = $item.ModuleId; Desc = $item.Desc; Column = $column
                Reason = '批核/结案状态位：由引擎在批核/结案时写，新记录恒为 0，过滤条件不可能满足'
            })
            continue
        }
        if ($CodeDefaults.ContainsKey($item.ModuleId) -and $CodeDefaults[$item.ModuleId] -contains $column) { continue }
        $fieldKey = "$($item.Table.ToUpperInvariant())|$column"
        if ($Fields.ContainsKey($fieldKey)) {
            $f = $Fields[$fieldKey]
            if ($f.Visible -eq '1' -and $f.Readonly -eq '0' -and $f.Virtual -eq '0') { continue }
            if ($f.HasDefault) { continue }
        }
        if ($ColumnDefaults.ContainsKey($fieldKey) -and $ColumnDefaults[$fieldKey]) { continue }
        $describe = if ($Fields.ContainsKey($fieldKey)) {
            $f = $Fields[$fieldKey]
            "vis=$($f.Visible) ro=$($f.Readonly) virt=$($f.Virtual) 元数据默认值=无"
        }
        else { '无 FIELDS 元数据' }
        $violations.Add([pscustomobject]@{
            ModuleId = $item.ModuleId; Desc = $item.Desc; Column = $column
            Reason = "既非用户可填、也无元数据/代码/物理默认值（$describe）"
        })
    }
    return $violations
}

try {
    . (Join-Path $PSScriptRoot 'dev/eos-sql.ps1')

    $writeList = Get-WriteList -Path $appSettingsPath
    $codeDefaults = Get-CodeDefaults -Path $defaultRulesPath
    # 解析守卫：源文件格式变了会让规则静默失效、门禁假绿——必须显式失败
    if ($codeDefaults.Count -eq 0 -or ($codeDefaults['1407'] -notcontains 'SEND_TAG')) {
        Write-Host "ERROR FormDefaultRules 解析结果为空或缺已知条目（1407 -> SEND_TAG）：源文件格式可能已变，判定不可信。"
        exit 1
    }

    $writeIds = "(" + ($writeList -join ',') + ")"
    # 候选：写名单里 Filter 非空的模块 × 主表真实列，粗筛"列名出现在 FILTER 文本里"
    $candidateSql = @"
SELECT CONCAT(CAST(m.M_IDX AS varchar(20)), '~', ISNULL(m.M_DESC,''), '~', LTRIM(RTRIM(m.MASTER_TABLE)), '~',
       ISNULL(CAST(m.AUTO_APPROVE AS varchar(2)),'0'), '~', c.name, '~', ISNULL(m.FILTER,''))
FROM dbo.MODULES m
JOIN sys.columns c ON c.object_id = OBJECT_ID(N'dbo.' + LTRIM(RTRIM(m.MASTER_TABLE)))
WHERE CAST(m.M_IDX AS varchar(20)) IN $writeIds
  AND m.FILTER IS NOT NULL AND LTRIM(RTRIM(m.FILTER)) <> ''
  AND CHARINDEX(c.name, m.FILTER) > 0;
"@
    $candidates = @()
    $tables = @()
    foreach ($line in (Invoke-EosSqlQuery -ConnectionString $ConnectionString -Query $candidateSql)) {
        $c = $line -split '~', 6
        if ($c.Count -lt 6) { continue }
        $candidates += [pscustomobject]@{
            ModuleId = $c[0].Trim(); Desc = $c[1].Trim(); Table = $c[2].Trim()
            AutoApprove = $c[3].Trim(); Column = $c[4].Trim(); Filter = $c[5].Trim()
        }
        $tables += $c[2].Trim()
    }
    $tables = @($tables | Sort-Object -Unique)

    $fields = @{}
    $columnDefaults = @{}
    if ($tables.Count -gt 0) {
        $tblList = "('" + ($tables -join "','") + "')"
        foreach ($line in (Invoke-EosSqlQuery -ConnectionString $ConnectionString -Query @"
SELECT CONCAT(LTRIM(RTRIM(f.T_ID)), '~', LTRIM(RTRIM(f.F_ID)), '~',
       ISNULL(CAST(f.IS_VISIBLE AS varchar(2)),'-'), '~', ISNULL(CAST(f.IS_READONLY AS varchar(2)),'-'), '~',
       ISNULL(CAST(f.IS_VIRTUAL AS varchar(2)),'-'), '~',
       CASE WHEN ISNULL(LTRIM(RTRIM(f.DFT_VALUE)),'') <> '' OR ISNULL(LTRIM(RTRIM(f.FORM_OPTIONS)),'') <> ''
            THEN '1' ELSE '0' END)
FROM dbo.FIELDS f WHERE LTRIM(RTRIM(f.T_ID)) IN $tblList;
"@)) {
            $c = $line -split '~', 6
            if ($c.Count -lt 6) { continue }
            $fields["$($c[0].ToUpperInvariant())|$($c[1].ToUpperInvariant())"] = [pscustomobject]@{
                Visible = $c[2]; Readonly = $c[3]; Virtual = $c[4]; HasDefault = ($c[5] -eq '1')
            }
        }
        foreach ($line in (Invoke-EosSqlQuery -ConnectionString $ConnectionString -Query @"
SELECT DISTINCT CONCAT(o.name, '~', c.name)
FROM sys.columns c
JOIN sys.objects o ON o.object_id = c.object_id AND o.type = 'U'
WHERE o.name IN $tblList AND c.default_object_id <> 0;
"@)) {
            $c = $line -split '~', 2
            if ($c.Count -lt 2) { continue }
            $columnDefaults["$($c[0].ToUpperInvariant())|$($c[1].ToUpperInvariant())"] = $true
        }
    }

    if ($SelfTest) {
        Write-Host '== 判别力自检 =='
        $sampleFields = @{
            'T_SF|FILLABLE' = [pscustomobject]@{ Visible = '1'; Readonly = '0'; Virtual = '0'; HasDefault = $false }
            'T_SF|DFTED'    = [pscustomobject]@{ Visible = '0'; Readonly = '1'; Virtual = '0'; HasDefault = $true }
            'T_SF|READONLYONE' = [pscustomobject]@{ Visible = '1'; Readonly = '1'; Virtual = '0'; HasDefault = $false }
            'T_SF|NOTFILLABLE' = [pscustomobject]@{ Visible = '1'; Readonly = '1'; Virtual = '0'; HasDefault = $false }
        }
        $sampleCode = @{ '9005' = @('CODED') }
        $sampleColDefaults = @{ 'T_SF|COLDEFED' = $true }
        $sampleCandidates = @(
            [pscustomobject]@{ ModuleId = '9001'; Desc = '合成-无任何来源'; Table = 'T_SF'; Column = 'NOTFILLABLE'; Filter = 'NOTFILLABLE=1' }
            [pscustomobject]@{ ModuleId = '9002'; Desc = '合成-状态位'; Table = 'T_SF'; Column = 'CONFIRM_TAG'; Filter = 'CONFIRM_TAG=1' }
            [pscustomobject]@{ ModuleId = '9003'; Desc = '合成-用户可填'; Table = 'T_SF'; Column = 'FILLABLE'; Filter = 'FILLABLE=1' }
            [pscustomobject]@{ ModuleId = '9004'; Desc = '合成-元数据默认值'; Table = 'T_SF'; Column = 'DFTED'; Filter = 'DFTED=1' }
            [pscustomobject]@{ ModuleId = '9005'; Desc = '合成-代码默认值'; Table = 'T_SF'; Column = 'CODED'; Filter = 'CODED=1' }
            [pscustomobject]@{ ModuleId = '9006'; Desc = '合成-物理默认值'; Table = 'T_SF'; Column = 'COLDEFED'; Filter = 'COLDEFED=1' }
            # 词边界：FILTER 里只有 REWORK_TAG，列 TAG 不得被误抓
            [pscustomobject]@{ ModuleId = '9007'; Desc = '合成-词边界'; Table = 'T_SF'; Column = 'TAG'; Filter = 'REWORK_TAG=0' }
        )
        $sampleHits = @(Get-FilterViolations -Candidates $sampleCandidates -Fields $sampleFields `
            -CodeDefaults $sampleCode -ColumnDefaults $sampleColDefaults)
        $hitIds = @($sampleHits | ForEach-Object { $_.ModuleId } | Sort-Object)
        $expectIds = @('9001', '9002')
        if (($hitIds -join ',') -ne ($expectIds -join ',')) {
            Write-Host "  [FAIL] 合成样本命中面不符预期：实际 $(($hitIds -join ',')) / 期望 $(($expectIds -join ','))"
            Write-Host 'SELFTEST FAIL 过滤条件可满足性判据失去判别力。'
            exit 1
        }
        Write-Host '  [PASS] 只命中「无任何来源」与「批核状态位」；用户可填 / 元数据默认值 / 代码默认值 / 物理默认值 / 词边界 均未误报'
    }

    $violations = @(Get-FilterViolations -Candidates $candidates -Fields $fields `
        -CodeDefaults $codeDefaults -ColumnDefaults $columnDefaults)
    if ($violations.Count -gt 0) {
        Write-Host '== 写名单模块的过滤条件（应为 0 处不可满足）=='
        foreach ($v in $violations) {
            Write-Host "  [$($v.ModuleId)] $($v.Desc) | 列 $($v.Column)：$($v.Reason)"
        }
        Write-Host "FAIL 有 $($violations.Count) 处：这些模块在写名单里（承诺可新增），但新记录满足不了自己的模块过滤条件，新增入口必然失败。"
        exit 1
    }

    Write-Host "PASS 写名单 $($writeList.Count) 个模块中，带 FILTER 的模块其过滤列均有新增时可产生的来源（候选列 $($candidates.Count) 处）。"
    exit 0
}
catch {
    Write-Host "ERROR 模块过滤条件巡检执行失败：$($_.Exception.Message)"
    exit 1
}
