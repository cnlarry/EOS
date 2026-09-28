<#
.SYNOPSIS
    快照落后常态检查：已发布快照是否落后于"按当前配置重建的定义"，尤其是**没有任何标记提示**的那种。

.DESCRIPTION
    运行期读的是已发布快照，而快照会**静默落后于库内配置**：配置改了、却没人重发布，模块也
    没有脏标记 ⇒ 没有任何信号，运行期一直按旧定义跑。

    成因有两类，第二类是本检查存在的理由：
      · **有人编辑过**：元数据写入路径会标脏（`WORKBENCH_MODULE_DIRTY`），有信号，属已知的
        待发布积压；
      · **迁移/直写落库**：既不会标脏，也可能**不动 `LAST_UPDATE_DATE`**（字段顺序、可见性、
        列宽这类改动常常只改值），于是脏标记与 `SOURCE_METADATA_VERSION` 双双漏掉——只能靠
        "把定义重建一遍再逐字比对"发现。

    判据与发布时的"内容未变即复用版本"**同一口径**（同一个校验器产出的 `DEFINITION_JSON`），
    不另立标准。判定：
      · `stale 且无脏标记` ⇒ **FAIL**（静默落后：配置变了，没有任何标记提示）；
      · `stale 且有脏标记` ⇒ 仅提示（已知的待发布积压，数量本身不是异常）；
      · 内容一致 ⇒ PASS。

    代价：需要重建每个模块的定义，实测 262 个模块约 48 秒。需 API 在线且账号有系统管理 Setup 权限。

.PARAMETER ApiUrl
    运行中的 API 地址。

.PARAMETER IncludeFlagged
    连"有脏标记的落后"一起算失败（默认只提示）。放量清理待发布积压时可打开。

.EXAMPLE
    pwsh scripts/check-snapshot-staleness.ps1
    pwsh scripts/check-snapshot-staleness.ps1 -IncludeFlagged
#>
[CmdletBinding()]
param(
    [string] $ApiUrl = 'http://localhost:5261',
    [switch] $IncludeFlagged,
    [int] $TimeoutSec = 900
)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8

try {
    . (Join-Path $PSScriptRoot '..\EOS.API.Tests\AcceptanceCommon.ps1')
    $session = New-EosSession -ApiUrl $ApiUrl
    $response = Invoke-EosApi -Method 'GET' -Path '/api/v1/workbench-definitions/staleness' -Session $session -ApiUrl $ApiUrl
}
catch {
    Write-Output "FAIL snapshot staleness probe error: $($_.Exception.Message)"
    exit 2
}

if ($response.Status -ne 200) {
    Write-Output "FAIL snapshot staleness endpoint returned HTTP $($response.Status)（需 API 在线且账号有系统管理 Setup 权限）"
    exit 2
}

$rows = @($response.Content)
$stale = @($rows | Where-Object { $_.stale })
$silent = @($stale | Where-Object { -not $_.dirty })
$flagged = @($stale | Where-Object { $_.dirty })

Write-Output "checked=$($rows.Count) fresh=$($rows.Count - $stale.Count) stale=$($stale.Count) silent=$($silent.Count) flagged=$($flagged.Count)"

if ($flagged.Count -gt 0) {
    Write-Output "NOTE stale-but-flagged (known pending republish backlog; not a failure):"
    $flagged | Sort-Object moduleId | ForEach-Object {
        Write-Output ("  module {0} {1} v{2}" -f $_.moduleId, $_.title, $_.version)
    }
}

if ($silent.Count -gt 0) {
    Write-Output "FAIL snapshot is stale with NO dirty flag (config changed without any republish signal):"
    $silent | Sort-Object moduleId | ForEach-Object {
        Write-Output ("  module {0} {1} v{2} (len {3} -> {4})" -f $_.moduleId, $_.title, $_.version, $_.storedLength, $_.rebuiltLength)
        if ($_.firstDifference) { Write-Output ("    {0}" -f $_.firstDifference) }
    }
    Write-Output "  remedy: republish these modules (POST /api/v1/workbench-definitions/publish), or mark them dirty so the backlog is visible."
    exit 1
}

if ($IncludeFlagged -and $flagged.Count -gt 0) {
    Write-Output 'FAIL -IncludeFlagged: stale-but-flagged modules are treated as a failure.'
    exit 1
}

Write-Output "PASS published snapshots match the definitions rebuilt from current configuration ($($flagged.Count) flagged backlog notice(s))."
exit 0
