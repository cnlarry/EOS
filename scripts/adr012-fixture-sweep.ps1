<#
.SYNOPSIS
    ADR-012 合成夹具对拍跑批：把"账本无证据"的模块用夹具单据补齐 A/B 证据。

.DESCRIPTION
    开发库里有一批模块**没有可对拍单据**（主表为空或全部已批核），历史 A 类证据全部来自
    logs/adr012-acceptance/fixture/ 的合成单据；夹具拆除后这些模块在账本里就一直停在 C 类。

    本脚本把这些夹具重新跑成证据：

      1. Plan    读最近一次 EffectShadowSweep 汇总，取出 NO_SAMPLE 的模块/事件，
                 按"历史报告里用过的单据号出现在哪个夹具里"配对（夹具名以模块号开头优先，
                 声明式 DECLARE 命中加分；模块是纯主表形态时单据号可能是产品号，
                 故显式 -Overrides 可覆盖）
      2. Setup   逐个夹具造数（夹具统一 Mode=Status|Setup|Teardown；无 $(Mode) 的种子脚本只跑一次）
      3. 对拍    逐夹具一次 EffectShadowSweep（该夹具涉及的模块 × 批核/解批）
      4. Teardown 逐个夹具还原并复核零残留（默认执行，-NoTeardown 可留档调试）

    夹具与报告都在 logs/ 下，不入库；脚本只跑夹具与对拍，不启动服务、不改配置。

.EXAMPLE
    # 试点：只跑两个夹具
    pwsh scripts/adr012-fixture-sweep.ps1 -FixtureNames fitout-approve-fixture.sql

.EXAMPLE
    # 全量
    pwsh scripts/adr012-fixture-sweep.ps1
#>
[CmdletBinding()]
param(
    [string[]] $FixtureNames = @(),
    [int[]] $ModuleIds = @(),
    [string] $TestsDll = "$env:TEMP\adr012-tests\EOS.API.Tests.dll",
    [string] $FixtureDir,
    [switch] $SetupOnly,
    [switch] $NoTeardown,
    [hashtable] $Overrides = @{}
)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
if (-not $FixtureDir) { $FixtureDir = Join-Path $root 'logs\adr012-acceptance\fixture' }
. (Join-Path $root 'scripts\dev\eos-sql.ps1')

if (-not (Test-Path -LiteralPath $TestsDll)) { throw "测试程序集不存在：$TestsDll（先 dotnet build EOS.API.Tests -o 该目录）" }
$env:MSSQL_ERP_CONN = [Environment]::GetEnvironmentVariable('MSSQL_ERP_CONN', 'User')
if (-not $env:MSSQL_ERP_CONN) { throw '缺少 MSSQL_ERP_CONN（User 级环境变量）' }

function Invoke-FixtureFile {
    <# 运行夹具的一个 Mode；无 $(Mode) 的脚本按原样跑一次。返回 @{ Ok; Output } #>
    param([string] $Path, [string] $Mode, [string] $Scenario = 'A')

    $sql = [IO.File]::ReadAllText($Path)
    if ($Mode -and $sql.Contains('$(Mode)')) { $sql = $sql.Replace('$(Mode)', $Mode) }
    # 场景变量：夹具用它切换造数形态（2815 的 B = 已完工量等于制令量，默认 A = 未完工）
    if ($sql.Contains('$(Scenario)')) { $sql = $sql.Replace('$(Scenario)', $Scenario) }
    $tmp = Join-Path ([IO.Path]::GetTempPath()) ('eos-fix-{0}.sql' -f [guid]::NewGuid().ToString('N'))
    [IO.File]::WriteAllText($tmp, $sql, (New-Object System.Text.UTF8Encoding($false)))
    try {
        # 必须带 -b（Invoke-EosSqlFile 默认行为）：夹具内部用 TRY/CATCH + RAISERROR 报错，
        # 不带 -b 时 sqlcmd 仍返回 0，"Setup 失败"会被静默当成成功。
        $run = Invoke-EosSqlFile -Path $tmp
        $lines = @($run.Output | Where-Object { $_ -match '\S' })
        return @{ Ok = ($run.ExitCode -eq 0); Output = $lines }
    }
    finally { Remove-Item -LiteralPath $tmp -Force -ErrorAction SilentlyContinue }
}

function Get-LatestSweepSummary {
    param([switch] $Baseline)
    $files = @(Get-ChildItem (Join-Path $root 'logs\shadow\sweep-*.json') -ErrorAction SilentlyContinue)
    if ($files.Count -eq 0) { throw '找不到 EffectShadowSweep 汇总（先跑一次 EOS_SHADOW_SWEEP=1）' }
    $summaries = foreach ($file in $files) {
        try { [pscustomobject]@{ File = $file; Json = (Get-Content $file.FullName -Raw -Encoding UTF8 | ConvertFrom-Json) } }
        catch { continue }
    }
    if ($Baseline) {
        # 全量基线 = 覆盖模块/事件最多的那份汇总；按夹具分批跑出来的小汇总不能当基线。
        return ($summaries | Sort-Object -Property @{ Expression = { $_.Json.runs } ; Descending = $true },
            @{ Expression = { $_.File.LastWriteTime } ; Descending = $true } | Select-Object -First 1).Json
    }
    return ($summaries | Sort-Object -Property @{ Expression = { $_.File.LastWriteTime } ; Descending = $true } | Select-Object -First 1).Json
}

function Get-CurrentSnapshotVersions {
    <# @{ moduleId = version }：当前已发布快照版本，用于跳过"已有新鲜证据"的配对 #>
    $versions = @{}
    foreach ($line in (Invoke-EosSqlQuery -Query 'SELECT CONCAT(M_IDX, char(124), VERSION) FROM dbo.WORKBENCH_DEFINITION_SNAPSHOT WHERE IS_CURRENT = 1;')) {
        $parts = ($line -join '') -split '\|'
        if ($parts.Count -ge 2 -and $parts[0] -match '^\d+$') { $versions[[int]$parts[0]] = [int]$parts[1] }
    }
    return $versions
}

function Get-FixturePlan {
    <# 返回 @( @{ Module; Event; Keys; Fixture } )：账本无证据且能对到夹具的模块/事件 #>
    $summary = Get-LatestSweepSummary -Baseline
    # 需要重取证据的两类：无可用样本（NO_SAMPLE）与仍判红（FAIL——对拍器口径变过之后要复评）
    $need = @($summary.results | Where-Object { $_.verdict -eq 'NO_SAMPLE' -or $_.verdict -eq 'FAIL' })

    # 历史报告里各模块/事件用过的单据号（夹具拆除后最新报告仍是夹具那一条），
    # 同时记录最新报告，用于跳过已经有新鲜证据的配对。
    $history = @{}
    $latest = @{}
    foreach ($file in (Get-ChildItem (Join-Path $root 'logs\shadow\shadow-*.json'))) {
        try { $json = Get-Content $file.FullName -Raw -Encoding UTF8 | ConvertFrom-Json } catch { continue }
        $key = "$($json.moduleId)|$($json.event)"
        if (-not $history.ContainsKey($key) -or $history[$key].Time -lt $file.LastWriteTime) {
            $history[$key] = [pscustomobject]@{ Time = $file.LastWriteTime; Keys = @($json.recordKeys) }
        }
        if (-not $latest.ContainsKey($key) -or $latest[$key].Time -lt $file.LastWriteTime) {
            $latest[$key] = [pscustomobject]@{ Time = $file.LastWriteTime; Version = $json.definitionVersion; Verdict = $json.summary.verdict }
        }
    }
    $current = Get-CurrentSnapshotVersions

    $fixtures = @(Get-ChildItem (Join-Path $FixtureDir '*.sql'))
    $text = @{}
    foreach ($fixture in $fixtures) { $text[$fixture.Name] = [IO.File]::ReadAllText($fixture.FullName) }

    $plan = New-Object System.Collections.Generic.List[object]
    foreach ($item in $need) {
        $key = "$($item.moduleId)|$($item.event)"
        # 已有当前版本的新鲜 PASS 证据：不必再造数（分批重跑时脚本可幂等复用）
        $newest = $latest[$key]
        if ($newest -and $current.ContainsKey([int]$item.moduleId)) {
            $expected = "module-$($item.moduleId)-v$($current[[int]$item.moduleId])"
            if ($newest.Version -eq $expected -and $newest.Verdict -eq 'PASS') { continue }
        }
        if ($Overrides.ContainsKey($key)) {
            $plan.Add([pscustomobject]@{ Module = $item.moduleId; Event = $item.event; Keys = ''; Fixture = $Overrides[$key] })
            continue
        }
        $remembered = $history[$key]
        if (-not $remembered -or $remembered.Keys.Count -lt 2) { continue }
        $document = $remembered.Keys[1]
        $candidates = @($fixtures | Where-Object { $text[$_.Name].Contains("'$document'") })
        if ($candidates.Count -eq 0) { continue }
        $scored = @($candidates | ForEach-Object {
            $score = 0
            if ($_.Name.StartsWith("$($item.moduleId)")) { $score += 4 }
            if ($text[$_.Name] -match ("DECLARE\s+@\w+\s+\w+[^=]*=\s*N?'" + [regex]::Escape($document) + "'")) { $score += 2 }
            if ($_.Name -match '^bridge-takeover') { $score += 1 }
            [pscustomobject]@{ Name = $_.Name; Score = $score }
        } | Sort-Object -Property @{ Expression = 'Score'; Descending = $true }, Name)
        $plan.Add([pscustomobject]@{ Module = $item.moduleId; Event = $item.event; Keys = ($remembered.Keys -join '|'); Fixture = $scored[0].Name })
    }
    return $plan
}

function Get-EngineOnlyModules {
    <# 旧批核过程已退役（或快照未登记）的模块：这些模块的解批对拍是"引擎单跑"，不需要旧过程 #>
    param([int[]] $Modules)
    if ($Modules.Count -eq 0) { return @() }
    $rows = Invoke-EosSqlQuery -Query @"
SELECT CONCAT(s.M_IDX, char(124),
  CASE WHEN OBJECT_ID(JSON_VALUE(s.DEFINITION_JSON, '`$.BusinessRule.WorkflowSproc'), 'P') IS NULL THEN '1' ELSE '0' END)
FROM dbo.WORKBENCH_DEFINITION_SNAPSHOT s
WHERE s.IS_CURRENT = 1 AND s.M_IDX IN ($($Modules -join ','));
"@
    $engineOnly = @()
    foreach ($row in $rows) {
        $parts = ($row -join '') -split '\|'
        if ($parts.Count -ge 2 -and $parts[1] -eq '1') { $engineOnly += [int]$parts[0] }
    }
    return $engineOnly
}

function Invoke-ForceConfirm {
    <#
        引擎单跑模块的解批前置：夹具的 Confirm 依赖旧批核过程物化副作用，过程退役即 REFUSE；
        而引擎单跑只需要单据处于"已批核"前置状态，故直接翻转 CONFIRM_TAG。
        只动夹具自己造的单据（键来自夹具对拍记录），Teardown 会连单据一起删除。
    #>
    param([object[]] $Items)

    $done = 0
    foreach ($item in $Items) {
        if ($item.Event -ne 'DEAPPROVE' -or -not $item.Keys) { continue }
        $keys = $item.Keys -split '\|'
        if ($keys.Count -lt 2) { continue }
        $meta = Invoke-EosSqlQuery -Query @"
SELECT CONCAT(m.MASTER_TABLE, char(124), c1.name, char(124), ISNULL(c2.name, ''))
FROM dbo.MODULES m
LEFT JOIN sys.indexes i ON i.object_id = OBJECT_ID('dbo.' + m.MASTER_TABLE) AND i.is_primary_key = 1
LEFT JOIN sys.index_columns ic1 ON ic1.object_id = i.object_id AND ic1.index_id = i.index_id AND ic1.key_ordinal = 1
LEFT JOIN sys.columns c1 ON c1.object_id = ic1.object_id AND c1.column_id = ic1.column_id
LEFT JOIN sys.index_columns ic2 ON ic2.object_id = i.object_id AND ic2.index_id = i.index_id AND ic2.key_ordinal = 2
LEFT JOIN sys.columns c2 ON c2.object_id = ic2.object_id AND c2.column_id = ic2.column_id
WHERE m.M_IDX = $($item.Module);
"@
        $parts = (($meta | Select-Object -First 1) -join '') -split '\|'
        if ($parts.Count -lt 3 -or -not $parts[0] -or -not $parts[1]) { continue }
        $master = $parts[0]; $key1 = $parts[1]; $key2 = $parts[2]
        $filter = "[$key1] = @k1"
        if ($key2) { $filter += " AND [$key2] = @k2" }
        $sql = @"
SET NOCOUNT ON;
DECLARE @k1 NVARCHAR(50) = N'$($keys[0].Replace("'", "''"))';
DECLARE @k2 NVARCHAR(50) = N'$($keys[1].Replace("'", "''"))';
UPDATE dbo.[$master] SET CONFIRM_TAG = 1, CONFIRM_PERSON = N'shadow', CONFIRM_DATE = SYSDATETIME()
WHERE $filter AND ISNULL(CONFIRM_TAG, 0) = 0;
"@
        $tmp = Join-Path ([IO.Path]::GetTempPath()) ('eos-cf-{0}.sql' -f [guid]::NewGuid().ToString('N'))
        [IO.File]::WriteAllText($tmp, $sql, (New-Object System.Text.UTF8Encoding($false)))
        try { $null = Invoke-EosSqlFile -Path $tmp } finally { Remove-Item $tmp -Force -ErrorAction SilentlyContinue }
        $done++
    }
    return $done
}

function Invoke-SweepForFixture {
    <# 对该夹具涉及的模块跑一次批量对拍，返回最近一次汇总里的结果行 #>
    param([int[]] $Modules, [string] $Events)

    $env:EOS_SHADOW_SWEEP = '1'
    $env:EOS_SHADOW_MODULES = ($Modules -join ',')
    $env:EOS_SHADOW_EVENTS = $Events
    try {
        $output = & dotnet vstest $TestsDll --TestCaseFilter:"FullyQualifiedName~EffectShadowSweep" 2>&1
        if ($LASTEXITCODE -ne 0) { Write-Output ("    vstest rc=$LASTEXITCODE"); $output | Select-Object -Last 3 | ForEach-Object { Write-Output "    $_" } }
    }
    finally {
        $env:EOS_SHADOW_SWEEP = $null; $env:EOS_SHADOW_MODULES = $null; $env:EOS_SHADOW_EVENTS = $null
    }
    $summary = Get-LatestSweepSummary
    return @($summary.results | Where-Object { $Modules -contains [int]$_.moduleId })
}

# ------------------------------------------------------------------ 计划 ---
$plan = Get-FixturePlan
if ($FixtureNames.Count -gt 0) { $plan = @($plan | Where-Object { $FixtureNames -contains $_.Fixture }) }
if ($ModuleIds.Count -gt 0) { $plan = @($plan | Where-Object { $ModuleIds -contains $_.Module }) }
if ($plan.Count -eq 0) { Write-Output 'SKIP 没有可跑的配对'; exit 0 }

$groups = @($plan | Group-Object Fixture | Sort-Object Name)
Write-Output ("== 计划：{0} 个模块/事件，{1} 个夹具" -f $plan.Count, $groups.Count)

$results = New-Object System.Collections.Generic.List[object]
$fixtureFailure = 0
foreach ($group in $groups) {
    $fixturePath = Join-Path $FixtureDir $group.Name
    $fixtureSql = [IO.File]::ReadAllText($fixturePath)
    $modes = $fixtureSql.Contains('$(Mode)')
    $modules = @($group.Group | ForEach-Object { [int]$_.Module } | Sort-Object -Unique)
    Write-Output ("-- {0}  modules={1}" -f $group.Name, ($modules -join ','))

    $setup = $null
    $statusBefore = $null
    if ($modes) {
        if ($fixtureSql -match 'Status') {
            $statusBefore = Invoke-FixtureFile -Path $fixturePath -Mode 'Status'
            if (-not $statusBefore.Ok) { Write-Output '    WARN Status 非零退出（继续）' }
        }
        $setup = Invoke-FixtureFile -Path $fixturePath -Mode 'Setup'
    }
    else {
        # 种子类脚本没有 Mode：只应用一次，不做还原
        $setup = Invoke-FixtureFile -Path $fixturePath -Mode ''
    }
    if (-not $setup.Ok) {
        Write-Output '    FAIL Setup 失败'
        $setup.Output | Select-Object -Last 5 | ForEach-Object { Write-Output "      $_" }
        $fixtureFailure++
        foreach ($item in $group.Group) {
            $results.Add([pscustomobject]@{ Module = $item.Module; Event = $item.Event; Fixture = $group.Name; Verdict = 'SETUP_FAIL' })
        }
        # Setup 可能已提交了半成品：仍然尝试还原，避免把残留留给下一次运行。
        if ($modes -and -not $NoTeardown) {
            $cleanup = Invoke-FixtureFile -Path $fixturePath -Mode 'Teardown'
            if (-not $cleanup.Ok) { Write-Output '    还原也失败：请人工核对残留' }
        }
        continue
    }

    if (-not $SetupOnly) {
        # 批核证据必须在"未批核"前置上取；解批证据需要"已批核"前置，
        # 故分两段：批核对拍 → 物化批核前置（夹具 Confirm / 引擎单跑直接翻转）→ 解批对拍。
        foreach ($row in (Invoke-SweepForFixture -Modules $modules -Events 'APPROVE_EFFECT')) {
            $results.Add([pscustomobject]@{ Module = [int]$row.moduleId; Event = $row.event; Fixture = $group.Name; Verdict = $row.verdict })
        }

        $deapproveItems = @($group.Group | Where-Object { $_.Event -eq 'DEAPPROVE' })
        if ($deapproveItems.Count -gt 0) {
            if ($modes -and $fixtureSql -match "N'Confirm'") {
                $confirm = Invoke-FixtureFile -Path $fixturePath -Mode 'Confirm'
                if (-not $confirm.Ok) {
                    # 旧过程退役的模块 Confirm 会 REFUSE：这就是下面直接翻转 CONFIRM_TAG 的场景
                    Write-Output '    WARN Confirm 未生效（旧过程退役时属预期），改用直接置位'
                }
            }
            $forced = Invoke-ForceConfirm -Items $deapproveItems
            if ($forced -gt 0) { Write-Output ("    解批前置：直接置 CONFIRM_TAG=1 共 {0} 单" -f $forced) }
            foreach ($row in (Invoke-SweepForFixture -Modules $modules -Events 'DEAPPROVE')) {
                $results.Add([pscustomobject]@{ Module = [int]$row.moduleId; Event = $row.event; Fixture = $group.Name; Verdict = $row.verdict })
            }
        }
    }

    if ($modes -and -not $NoTeardown) {
        $teardown = Invoke-FixtureFile -Path $fixturePath -Mode 'Teardown'
        if (-not $teardown.Ok) {
            Write-Output '    FAIL Teardown 失败（报错见上）'
            $fixtureFailure++
        }
        if ($statusBefore -and $statusBefore.Ok -and $fixtureSql -match 'Status') {
            # 造数前 / 还原后的 Status 必须一致：夹具的 Setup/Teardown 是对称的，
            # 任何差异都意味着留下了残留（或擦掉了既有数据）。
            $post = Invoke-FixtureFile -Path $fixturePath -Mode 'Status'
            $beforeText = ($statusBefore.Output -join "`n")
            $afterText = (($post.Output | Where-Object { $_ -match '\S' }) -join "`n")
            if ($afterText -ne $beforeText) {
                Write-Output '    WARN 后置 Status 与造数前不一致：'
                foreach ($line in (Compare-Object -ReferenceObject ($statusBefore.Output | Where-Object { $_ -match '\S' }) `
                            -DifferenceObject ($post.Output | Where-Object { $_ -match '\S' }))) {
                    Write-Output ("      {0} {1}" -f $line.SideIndicator, $line.InputObject)
                }
            }
        }
    }
}

Write-Output ''
Write-Output '== 结果（账本证据口径）'
foreach ($item in ($plan | Sort-Object Module, Event)) {
    $got = @($results | Where-Object { $_.Module -eq $item.Module -and $_.Event -eq $item.Event } | Select-Object -Last 1)
    $verdict = if ($got.Count) { $got[0].Verdict } else { 'NO_RUN' }
    Write-Output ("   {0,-8} {1,-15} {2,-10} {3}" -f $item.Module, $item.Event, $verdict, $item.Fixture)
}
$pass = @($results | Where-Object { $_.Verdict -eq 'PASS' }).Count
$fail = @($results | Where-Object { $_.Verdict -eq 'FAIL' }).Count
$skip = @($results | Where-Object { $_.Verdict -eq 'NO_SAMPLE' }).Count
Write-Output ("== 汇总：PASS={0} FAIL={1} NO_SAMPLE={2} 夹具失败={3}" -f $pass, $fail, $skip, $fixtureFailure)
if ($fixtureFailure -gt 0) { exit 1 }
exit 0
