<#
.SYNOPSIS
    全量白名单模块重发布 + 快照差异清单（发布即重派生的验收工具）。

.DESCRIPTION
    发布链路改为「一律从当前代码+元数据重建模块级字段」后，已发布快照可能与本应重建的
    内容存在漂移（例如删掉一条 C# 领域规则后，旧快照仍指向已删族名）。本脚本对**统一表单
    写名单与库内所有带当前快照的模块**各发布一次，并对比发布前后 DEFINITION_JSON，产出
    「漂移模块差异清单」。

    覆盖面说明：只读名单模块同样有当前快照、同样要与配置同步，而它们不在写名单里；只按
    写名单枚举会让这些模块的快照永远重发布不到，而 check-snapshot-staleness.ps1 是按
    「有当前快照的模块」检查的，于是落后检测会一直为它们报失败。故默认枚举取两者并集。

    差异只比较模块级字段（发布会重派生的部分）；运行时按用户实时计算的字段
    （MasterFields/DetailFields/FilterFieldKeys/UserId/ExecTag/CanDelete/GroupExpressions/
    DefinitionVersion）与版本号不计入差异，避免把每模块都判成"有差异"。

    需要 API 以本次改动构建重启后运行；使用开发账号 admin/admin。

    用法（仓库根执行）：
      pwsh scripts/republish-all-whitelist.ps1                  # 发布写名单 + 所有带当前快照的模块
      pwsh scripts/republish-all-whitelist.ps1 -ModuleIds '110103,180102'
      pwsh scripts/republish-all-whitelist.ps1 -SkipPublish     # 只对比当前快照与元数据重建（不写库）

    产出：
      logs/definition-republish/diff-<timestamp>.json     逐模块差异明细（漂移清单）
      logs/definition-republish/summary-<timestamp>.md    汇总（发布数/漂移数/无漂移数/失败数）

    验收口径：漂移模块即「快照与元数据漂移」清单（本次一次性暴露，属预期）；重发布后
    必须重跑 scripts/check-domain-rule-registry.ps1（快照族名受支持）与账本
    scripts/adr012-acceptance-ledger.ps1。发布服务对**内容未变**的模块复用当前版本
    （不递增版本，决策 #109），故重发布不会再让既有对拍报告降为 C 类；只有确有内容变更、
    产生新版本的模块才需要重取证据。
#>

[CmdletBinding()]
param(
    [string]$ModuleIds,
    [string]$ApiUrl = 'http://localhost:5261',
    [string]$UserId = 'admin',
    [string]$Password = 'admin',
    [switch]$SkipPublish
)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8

$root = Split-Path -Parent $PSScriptRoot
$outDir = Join-Path $root 'logs\definition-republish'
if (-not (Test-Path $outDir)) { New-Item -ItemType Directory -Path $outDir | Out-Null }
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$diffPath = Join-Path $outDir "diff-$stamp.json"
$summaryPath = Join-Path $outDir "summary-$stamp.md"

. (Join-Path $PSScriptRoot 'dev\eos-sql.ps1')

# 读取当前快照的完整 DEFINITION_JSON（sqlcmd 默认 -W 会把超长 JSON 按 256 字符截断，
# 必须 -y 0 防截断且不带 -W；与 scripts/export-business-actions.ps1 的经验一致。
# -h/-W/-y 0 互斥，用 -y 0 时表头会输出，按行过滤掉表头与分隔线）。
function Read-CurrentSnapshotJson([int]$moduleId) {
    $target = Get-EosSqlTarget
    $sql = "SET NOCOUNT ON; SELECT CONVERT(nvarchar(max), DEFINITION_JSON) FROM dbo.WORKBENCH_DEFINITION_SNAPSHOT WITH (NOLOCK) WHERE M_IDX=$moduleId AND IS_CURRENT=1;"
    $raw = & $script:EosSqlCmd @($target.Args) -C -f 65001 -y 0 -Q $sql 2>&1
    if ($LASTEXITCODE -ne 0) { throw "sqlcmd 查询失败 rc=$LASTEXITCODE" }
    $lines = @($raw -split "`r?`n" |
        Where-Object { $_.Trim() -ne '' -and $_ -notlike '(* rows affected)' -and $_ -notmatch '^DEFINITION_JSON' -and $_ -notmatch '^-+$' })
    return ($lines -join '')
}

# 待发布模块：显式 -ModuleIds 优先；否则取「统一表单写名单」∪「库内所有带当前快照的模块」。
# 两个覆盖面并不相等——只读名单模块有快照却不在写名单里，只按写名单枚举会漏掉它们。
$ids = @()
if ($ModuleIds) {
    $ids = $ModuleIds.Split(',', [StringSplitOptions]::RemoveEmptyEntries) | ForEach-Object { [int]$_.Trim() }
} else {
    $appSettings = Get-Content (Join-Path $root 'EOS.API\appsettings.json') -Raw | ConvertFrom-Json
    $ids = @($appSettings.UnifiedFormEditor.EnabledModuleIds | ForEach-Object { [int]$_ })
    $snapshotIds = @(Invoke-EosSqlQuery 'SET NOCOUNT ON; SELECT M_IDX FROM dbo.WORKBENCH_DEFINITION_SNAPSHOT WHERE IS_CURRENT=1' |
        ForEach-Object { [int]($_ -replace '\D', '') })
    $ids = @($ids + $snapshotIds | Sort-Object -Unique)
}
if ($ids.Count -eq 0) { throw '未取得待发布模块。' }
Write-Output "待发布模块数：$($ids.Count)"

# 读取某模块当前快照的模块级字段（JSON 提取，剔除运行时按用户计算的字段与版本）
function Get-ModuleLevelFieldsJson([string]$json) {
    $obj = $json | ConvertFrom-Json
    # 删除运行时按用户计算/版本字段与序列化冗余的计算属性，只留发布会重派生的模块级字段
    foreach ($drop in @('MasterFields', 'DetailFields', 'FilterFieldKeys', 'UserId', 'ExecTag', 'CanDelete', 'GroupExpressions', 'DefinitionVersion', 'EffectEngineEnabled')) {
        if ($obj.PSObject.Properties.Name -contains $drop) { $obj.PSObject.Properties.Remove($drop) }
    }
    return ($obj | ConvertTo-Json -Depth 100 -Compress)
}

# 发布（或仅预检）前的快照字段快照
$before = @{}
foreach ($id in $ids) {
    $snapshotJson = Read-CurrentSnapshotJson $id
    if ($snapshotJson) { $before[$id] = Get-ModuleLevelFieldsJson $snapshotJson }
}

$session = New-Object Microsoft.PowerShell.Commands.WebRequestSession
Invoke-RestMethod -Uri "$ApiUrl/api/v1/auth/login" -Method Post -ContentType 'application/json' `
    -Body (@{ userId = $UserId; password = $Password; rememberMe = $false } | ConvertTo-Json) `
    -WebSession $session | Out-Null

$drift = @()
$published = 0
$reused = 0
$failed = 0
$noDrift = 0

# 逐模块：发布（默认）或 dry-run 重建（-SkipPublish）后回读并对比
foreach ($id in $ids) {
    $afterJsonRaw = $null
    if ($SkipPublish) {
        # 不写库：调 validate 拿「元数据重建」的定义 JSON，与当前快照对比
        $resp = Invoke-RestMethod -Uri "$ApiUrl/api/v1/workbench-definitions/validate" -Method Post `
            -ContentType 'application/json' -Body (@{ moduleId = $id } | ConvertTo-Json -Compress) `
            -WebSession $session
        $afterJsonRaw = $resp.definitionJson
        if (-not $afterJsonRaw) {
            $failed++
            Write-Output "  FAIL  module=$id validate 未返回重建定义（校验未过）"
            continue
        }
    } else {
        $resp = Invoke-WebRequest -Uri "$ApiUrl/api/v1/workbench-definitions/publish" -Method Post `
            -ContentType 'application/json' -Body (@{ moduleIds = @($id) } | ConvertTo-Json -Compress) `
            -WebSession $session -SkipHttpErrorCheck
        $results = @($resp.Content | ConvertFrom-Json)
        $result = $results[0]
        if ($resp.StatusCode -ne 200 -or -not $result.passed) {
            $failed++
            Write-Output "  FAIL  module=$id 发布被校验拦截"
            continue
        }
        # 内容未变时发布服务复用当前版本（不递增）：写库动作没发生，但配置已确认与快照一致。
        if (-not $result.published) {
            $reused++
            Write-Output "  REUSE module=$id 内容未变，复用 v$($result.version)"
        } else {
            $published++
        }
        $snapshotJson = Read-CurrentSnapshotJson $id
        if (-not $snapshotJson) {
            $failed++
            Write-Output "  WARN  module=$id 发布后无当前快照"
            continue
        }
        $afterJsonRaw = $snapshotJson
    }

    $afterJson = Get-ModuleLevelFieldsJson $afterJsonRaw
    $beforeJson = $before[$id]
    if ($null -eq $beforeJson) {
        $noDrift++
        continue
    }
    # 排序后的字段级比较：JSON 对象键顺序不稳定，先反序列化再按字段比较
    $beforeObj = $beforeJson | ConvertFrom-Json
    $afterObj = $afterJson | ConvertFrom-Json
    $diffFields = @()
    $allKeys = @($beforeObj.PSObject.Properties.Name + $afterObj.PSObject.Properties.Name | Sort-Object -Unique)
    foreach ($key in $allKeys) {
        $bv = if ($beforeObj.PSObject.Properties.Name -contains $key) { $beforeObj.$key } else { $null }
        $av = if ($afterObj.PSObject.Properties.Name -contains $key) { $afterObj.$key } else { $null }
        $bStr = if ($null -eq $bv) { '<null>' } else { ($bv | ConvertTo-Json -Depth 100 -Compress) }
        $aStr = if ($null -eq $av) { '<null>' } else { ($av | ConvertTo-Json -Depth 100 -Compress) }
        if ($bStr -ne $aStr) { $diffFields += [pscustomobject]@{ Field = $key; Before = $bStr; After = $aStr } }
    }
    if ($diffFields.Count -gt 0) {
        $drift += [pscustomobject]@{ ModuleId = $id; DiffFields = $diffFields }
        Write-Output "  DRIFT module=$id 字段: $(($diffFields.Field) -join ',')"
    } else {
        $noDrift++
        Write-Output "  OK    module=$id 无漂移"
    }
}

$diffPathReal = if ($SkipPublish) { (Join-Path $outDir "precheck-$stamp.json") } else { $diffPath }
$drift | ConvertTo-Json -Depth 10 | Set-Content -Encoding utf8 $diffPathReal

$summary = @"
# 全量白名单重发布差异清单（$stamp）

- 模式：$(if ($SkipPublish) { '仅对比（未写库）' } else { '已发布' })
- 白名单模块：$($ids.Count)
- 发布成功（内容有变，写入新版本）：$published
- 复用当前版本（内容未变，不递增版本）：$reused
- 漂移（快照与元数据不一致）：$($drift.Count)
- 无漂移：$noDrift
- 失败/未发布：$failed

## 漂移模块

$((($drift | ForEach-Object { "- module=$($_.ModuleId)：$((($_.DiffFields | ForEach-Object { $_.Field }) -join '、'))" }) -join "`n"))

## 验收口径

1. 重发布后运行 `scripts/check-domain-rule-registry.ps1`（快照族名必须受支持，PASS）。
2. 内容未变的模块复用当前版本，**既有对拍报告不再因重发布而降为 C 类**（只有真正改过配置、
   产生新版本的模块才需要重取证据）；跑 `scripts/adr012-acceptance-ledger.ps1` 复核 C 类清单
   应当只包含"确有内容变更"的模块。
3. 漂移字段中的 `BusinessRule.DomainRule` 指向已删族名的，重发布后应消失。
"@
$summary | Set-Content -Encoding utf8 $summaryPath
Write-Output "差异清单：$diffPathReal"
Write-Output "汇总：$summaryPath"
Write-Output "漂移模块数：$($drift.Count) / 无漂移：$noDrift / 新版本：$published / 复用版本：$reused / 失败：$failed"
exit 0
