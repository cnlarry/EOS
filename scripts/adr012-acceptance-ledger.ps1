<#
.SYNOPSIS
    Recomputes the ADR-012 effect-engine acceptance ledger from logs/shadow reports.

.DESCRIPTION
    Classifies every module/event pair into one of four classes:

      A  dual-path valid    - report definitionVersion >= the currently published snapshot,
                              oldPath ran (ok) and the snapshot contains side-effect tables
      B  engine-only        - legacy procedure retired so dual-path is impossible; the engine
                              ran cleanly at the current version (proves "no engine error",
                              NOT equivalence with the legacy implementation)
      C  stale dual-path    - a dual-path report exists but predates the current snapshot
      D  no evidence        - no usable report at all

    Only class A carries equivalence evidence. Never merge B into A when reporting progress.
    The script is read-only: it never writes the database and never starts services.

.PARAMETER Root
    Repository root. Defaults to the parent of this script.

.PARAMETER Denominator
    'actions' (default): modules that carry business actions with EFFECT_ENGINE_TAG=1.
    'all': every module that has a current published snapshot.

.PARAMETER Detail
    Emit a per-module markdown table in addition to the summary counts.

.PARAMETER OutFile
    Optional path; when set the report is written there (UTF-8 without BOM) as well.

.EXAMPLE
    pwsh scripts/adr012-acceptance-ledger.ps1 -Detail
#>
[CmdletBinding()]
param(
    [string] $Root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path,
    [ValidateSet('actions', 'all')]
    [string] $Denominator = 'actions',
    [switch] $Detail,
    [string] $OutFile
)

$ErrorActionPreference = 'Stop'

function Invoke-RowQuery {
    param([string] $Sql)

    # 取数走仓库统一的 SQL 入口：它按连接串决定用 SQL 认证还是集成认证、必要时回退托管客户端，
    # 本脚本不再自己拼 sqlcmd 参数——写死 -E 在拿不到系统加密凭证的环境里连不上，会让账本整程跑不起来。
    # 代价：不再传 -s "|"，所以**多列查询一律先 CONCAT 成单列**（本仓惯例，见 eos-sql.ps1 的 NOTES），
    # 下游仍按 '|' 切分，判据与解析口径不变。
    $helper = Join-Path $PSScriptRoot 'dev/eos-sql.ps1'
    if (-not (Get-Command Invoke-EosSqlQuery -ErrorAction SilentlyContinue)) {
        . $helper
    }
    return @(Invoke-EosSqlQuery -Query $Sql |
        ForEach-Object { $_.Trim() } |
        Where-Object { $_ -ne '' -and $_ -notlike '(* rows affected)' })
}

function Get-ReportVersion {
    param([string] $DefinitionVersion)

    if ($DefinitionVersion -match 'v(\d+)$') { return [int] $Matches[1] }
    return -1
}

try {
    $versionRows = Invoke-RowQuery 'SELECT CONCAT(M_IDX, char(124), VERSION) FROM dbo.WORKBENCH_DEFINITION_SNAPSHOT WHERE IS_CURRENT = 1 ORDER BY M_IDX;'
    $currentVersion = @{}
    foreach ($row in $versionRows) {
        $parts = $row -split '\|'
        if ($parts.Count -ge 2 -and $parts[0].Trim() -match '^\d+$') {
            $currentVersion[[int] $parts[0].Trim()] = [int] $parts[1].Trim()
        }
    }

    if ($Denominator -eq 'actions') {
        $scopeRows = Invoke-RowQuery 'SELECT DISTINCT A.M_IDX FROM dbo.MODULE_BUSINESS_ACTION A JOIN dbo.MODULES M ON M.M_IDX = A.M_IDX WHERE ISNULL(M.EFFECT_ENGINE_TAG,0) = 1 ORDER BY A.M_IDX;'
    } else {
        $scopeRows = Invoke-RowQuery 'SELECT M_IDX FROM dbo.WORKBENCH_DEFINITION_SNAPSHOT WHERE IS_CURRENT = 1 ORDER BY M_IDX;'
    }
    $scope = @($scopeRows | Where-Object { $_ -match '^\d+$' } | ForEach-Object { [int] $_ })

    $nameRows = Invoke-RowQuery 'SELECT CONCAT(M_IDX, char(124), M_DESC) FROM dbo.MODULES;'
    $names = @{}
    foreach ($row in $nameRows) {
        $parts = $row -split '\|'
        if ($parts.Count -ge 1 -and $parts[0].Trim() -match '^\d+$') {
            $names[[int] $parts[0].Trim()] = $parts[1].Trim()
        }
    }

    $reports = New-Object System.Collections.Generic.List[object]
    foreach ($file in @(Get-ChildItem -Path (Join-Path $Root 'logs\shadow\*.json') -File -ErrorAction SilentlyContinue)) {
        try {
            $json = ([IO.File]::ReadAllText($file.FullName)) | ConvertFrom-Json
            $reports.Add([pscustomobject]@{
                File   = $file.Name
                Module = [int] $json.moduleId
                Event  = [string] $json.event
                Verdict = if ($json.summary -and $json.summary.verdict) { [string] $json.summary.verdict } else { '' }
                Old      = if ($json.oldPath -and $json.oldPath.status) { [string] $json.oldPath.status } else { '' }
                OldError = if ($json.oldPath -and $json.oldPath.error) { (($json.oldPath.error -replace "`r?`n", ' ').Trim()) } else { '' }
                New      = if ($json.newPath -and $json.newPath.status) { [string] $json.newPath.status } else { '' }
                NewError = if ($json.newPath -and $json.newPath.error) { (($json.newPath.error -replace "`r?`n", ' ').Trim()) } else { '' }
                Version = if ($json.definitionVersion) { [string] $json.definitionVersion } else { '' }
                Tables = @($json.tables).Count
                Failure = [bool] $json.failure
                Time   = $file.LastWriteTime
            })
        } catch {
            Write-Warning "PARSE_FAIL $($file.Name)"
        }
    }

    $lines = New-Object System.Collections.Generic.List[string]
    $lines.Add('===== ADR-012 acceptance ledger =====')
    $lines.Add("generated = $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')")
    $lines.Add("denominator = $($scope.Count) ($Denominator)   reports = $($reports.Count)")

    $detailRows = @{}
    foreach ($event in @('APPROVE_EFFECT', 'DEAPPROVE')) {
        # Modules whose complete dual footprint is inherently tiny (master-only
        # documents with no detail table): 3307's snapshot is the master row plus the
        # single stamped PRODUCT row; 2401's is the sample master row, plus the
        # same-number product row only when one exists (the edition stamp itself
        # touches nothing else). The tables>2 gate exists to exclude master+detail-only
        # basic specs, which these are not. Minimum tables per module are explicit.
        $smallFootprint = @{ '3307' = 2; '2401' = 1 }
        $dual = @($reports | Where-Object { $_.Event -eq $event -and $_.Old -eq 'ok' -and ($_.Tables -gt 2 -or ($smallFootprint.ContainsKey("$($_.Module)") -and $_.Tables -ge $smallFootprint["$($_.Module)"])) })
        $only = @($reports | Where-Object { $_.Event -eq $event -and $_.Old -eq 'skipped' -and $_.New -eq 'ok' })

        $pass = 0; $fail = 0; $qualified = 0; $stale = 0; $none = 0
        $classA = @(); $classB = @(); $classC = @(); $classD = @()

        foreach ($module in $scope) {
            $latestDual = @($dual | Where-Object { $_.Module -eq $module } | Sort-Object Time | Select-Object -Last 1)
            $latestOnly = @($only | Where-Object { $_.Module -eq $module } | Sort-Object Time | Select-Object -Last 1)
            $dualVersion = if ($latestDual.Count) { Get-ReportVersion $latestDual[0].Version } else { -2 }
            $onlyVersion = if ($latestOnly.Count) { Get-ReportVersion $latestOnly[0].Version } else { -2 }
            $target = if ($currentVersion.ContainsKey($module)) { $currentVersion[$module] } else { -1 }

            if ($dualVersion -ge 0 -and $target -ge 0 -and $dualVersion -ge $target) {
                if ($latestDual[0].Verdict -eq 'PASS') { $pass++; $cell = "A PASS (v$dualVersion)" } else { $fail++; $cell = "A FAIL (v$dualVersion)" }
                $classA += $module
            } elseif ($onlyVersion -ge 0 -and $target -ge 0 -and $onlyVersion -ge $target -and (-not $latestDual.Count -or $latestOnly[0].Time -gt $latestDual[0].Time)) {
                $qualified++; $cell = "B engine-only (v$onlyVersion)"
                $classB += $module
            } elseif ($dualVersion -ge 0) {
                $stale++; $cell = "C stale (v$dualVersion -> v$target)"
                $classC += $module
            } else {
                $none++; $cell = 'D none'
                $classD += $module
            }
            $detailRows["$module|$event"] = $cell
        }

        $lines.Add('')
        $lines.Add("### $event")
        $lines.Add("  A dual-path valid   PASS = $pass   FAIL = $fail")
        $lines.Add("  B engine-only qualified = $qualified   [ $($classB -join ', ') ]")
        $lines.Add("  C stale dual-path       = $stale   [ $($classC -join ', ') ]")
        $lines.Add("  D no evidence           = $none   [ $($classD -join ', ') ]")
    }

    # Failure branch (ADR 6 third acceptance state): the engine must block before writing
    # anything. Dual = the legacy path blocked as well; engine-only = its procedure is retired.
    #
    # A module/event whose legacy procedure is retired can never produce dual failure
    # evidence again; when no valid engine-side failure sample exists either, the combo is
    # registered as "unobtainable" and kept out of the block counts (its absence is not a
    # failure, it is a physical limit of the evidence). 1423 and 2816 deapprove are such
    # cases: their deapprove-side legacy procedures P_WF_COP_BACK / P_WF_MOC_PRODUCT_IN
    # were retired, so the dual side cannot be materialised.
    $unobtainable = @('1423|DEAPPROVE', '2816|DEAPPROVE')
    $lines.Add('')
    $lines.Add('### failure branch (engine blocked, zero residue expected)')
    $blockedByModule = @{}
    $latestByModule = @{}
    foreach ($report in ($reports | Sort-Object Time)) { $latestByModule["$($report.Module)|$($report.Event)"] = $report }
    # 失败分支只取"失败分支运行"（EOS_SHADOW_FAILURE=1 写的报告）：同一模块/事件的成功路径对拍
    # 会写更新的报告，若按"最新一条"取，失败证据会被挤掉。同时忽略"单据不存在"这类夹具键失效的伪阻断。
    foreach ($report in ($reports |
        Where-Object { $_.Failure -and $_.New -eq 'blocked' -and $_.OldError -notmatch '单据不存在或已批核|单据不存在或未批核' } |
        Sort-Object Time)) {
        # Only a block observed at the currently published version means anything: an older
        # snapshot may have been fixed since.
        $reportVersion = if ($report.Version -match 'v(\d+)$') { [int] $Matches[1] } else { -1 }
        $current = if ($currentVersion.ContainsKey($report.Module)) { $currentVersion[$report.Module] } else { -1 }
        if ($reportVersion -lt 0 -or $current -lt 0 -or $reportVersion -lt $current) { continue }
        $blockedByModule["$($report.Module)|$($report.Event)"] = $report
    }
    $dual = 0; $only = 0; $artifact = 0; $unobtained = @()
    foreach ($key in ($blockedByModule.Keys | Sort-Object)) {
        if ($unobtainable -contains $key) { $unobtained += $key; continue }
        $report = $blockedByModule[$key]
        # A legacy "block" caused by a missing stored procedure is a harness artifact, not
        # evidence that the legacy implementation blocked the document for the same reason.
        $legacyMissing = $report.Old -eq 'blocked' -and $report.OldError -match '找不到存储过程|Could not find stored procedure'
        $kind = if ($legacyMissing) { 'LEGACY_MISSING'; $artifact++ }
                elseif ($report.Old -eq 'blocked') { 'DUAL'; $dual++ }
                else { 'ENGINE_ONLY'; $only++ }
        $lines.Add(("  {0,-24} {1,-9} {2,-15} tables={3} new_err={4}" -f $key, $report.Verdict, $kind, $report.Tables, ($(if ($report.NewError) { $report.NewError } else { '' }))))
    }
    if ($unobtained.Count) {
        $lines.Add("  unobtainable (legacy side retired, no dual evidence possible) = $($unobtained.Count)   [ $($unobtained -join ', ') ]")
    }
    $lines.Add("  dual blocked = $dual   engine-only blocked = $only   legacy-missing artifact = $artifact")

    if ($Detail) {
        $lines.Add('')
        $lines.Add('### per-module detail')
        $lines.Add('| module | name | approve | deapprove |')
        $lines.Add('|---|---|---|---|')
        foreach ($module in $scope) {
            $name = if ($names.ContainsKey($module)) { $names[$module] } else { '' }
            $approve = if ($detailRows.ContainsKey("$module|APPROVE_EFFECT")) { $detailRows["$module|APPROVE_EFFECT"] } else { 'D none' }
            $deapprove = if ($detailRows.ContainsKey("$module|DEAPPROVE")) { $detailRows["$module|DEAPPROVE"] } else { 'D none' }
            $lines.Add("| $module | $name | $approve | $deapprove |")
        }
    }

    $text = $lines -join "`n"
    if ($OutFile) {
        [IO.File]::WriteAllText($OutFile, $text, (New-Object System.Text.UTF8Encoding($false)))
    }
    Write-Output $text
    exit 0
} catch {
    Write-Output "LEDGER_FAIL: $($_.Exception.Message)"
    exit 2
}
