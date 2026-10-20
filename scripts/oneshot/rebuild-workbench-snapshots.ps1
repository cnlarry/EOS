# 一次性｜锚点：迁移 436（框架表改名）
<#
.SYNOPSIS
    重建全部工作台快照：备份当前快照 → 清空版本历史 → 全量重新发布。

.DESCRIPTION
    应用场景：元数据发生系统级变更（表改名、字段改名）后，已发布快照里仍是旧标识符，
    模块表现为空数据或打不开。逐模块手工重发布容易漏，这里一次性重建。

    行为：
      1. 把 IS_CURRENT=1 的快照导出到 $HOME/.eos/backup/（回滚依据）；
      2. 校验令牌可用（调 staleness 端点），令牌无效则不动库；
      3. 清空 WORKBENCH_DEFINITION_SNAPSHOT 与 WORKBENCH_MODULE_DIRTY，重置 identity；
      4. 调 POST /api/v1/workbench-definitions/backfill 全量发布；
      5. 核对：版本号从 1 起、每个模块 IS_CURRENT=1 各一条。

    清空到发布完成之间运行期回落实时装配，不会出现"打不开"的窗口；但基线缓存只在发布
    成功后才逐模块刷新，因此令牌预检必须先过，不要在令牌无效时先清库。

.PARAMETER BaseUrl
    EOS.API 地址，默认 http://localhost:5261。

.PARAMETER UserId
    管理员账号（需 2306 CanSetup），默认 admin。

.PARAMETER Password
    管理员口令；不传则交互式输入（不会写进命令历史）。

.PARAMETER WhatIf
    只做备份与影响面盘点，不清库、不发布。

.EXAMPLE
    pwsh scripts/oneshot/rebuild-workbench-snapshots.ps1 -WhatIf
    pwsh scripts/oneshot/rebuild-workbench-snapshots.ps1
#>
[CmdletBinding()]
param(
    [string] $BaseUrl = 'http://localhost:5261',
    [string] $UserId = 'admin',
    [string] $Password,
    [switch] $WhatIf
)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
. (Join-Path $repoRoot 'scripts\dev\eos-sql.ps1')

$target = Get-EosSqlTarget
$connection = New-Object System.Data.SqlClient.SqlConnection $target.AdoConnectionString

try {
    $connection.Open()

    # ---------------------------------------------------------------- 1. 备份
    $backupDir = Join-Path $HOME '.eos/backup'
    New-Item -ItemType Directory -Path $backupDir -Force | Out-Null
    $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    $backupFile = Join-Path $backupDir "workbench-snapshots-$stamp.json"

    $rows = New-Object System.Collections.Generic.List[object]
    $read = $connection.CreateCommand()
    $read.CommandText = @'
SELECT SNAPSHOT_ID, M_IDX, VERSION, DEFINITION_JSON, SOURCE_METADATA_VERSION,
       VALIDATION_STATUS, VALIDATION_REPORT_JSON, PUBLISHED_BY, PUBLISHED_AT
  FROM dbo.WORKBENCH_DEFINITION_SNAPSHOT
 WHERE IS_CURRENT = 1
 ORDER BY M_IDX;
'@
    $reader = $read.ExecuteReader()
    while ($reader.Read()) {
        $rows.Add([pscustomobject]@{
            snapshotId  = $reader.GetInt64(0)
            moduleId    = $reader.GetInt32(1)
            version     = $reader.GetInt32(2)
            definition  = $reader.GetString(3)
            metadataVer = if ($reader.IsDBNull(4)) { $null } else { $reader.GetString(4) }
            status      = $reader.GetString(5)
            report      = if ($reader.IsDBNull(6)) { $null } else { $reader.GetString(6) }
            publishedBy = if ($reader.IsDBNull(7)) { $null } else { $reader.GetString(7) }
            publishedAt = $reader.GetDateTime(8).ToString('o')
        })
    }
    $reader.Close(); $read.Dispose()

    $rows | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $backupFile -Encoding utf8
    Write-Host "BACKUP current=$($rows.Count) -> $backupFile"

    # ------------------------------------------------------------ 2. 影响面
    $stat = $connection.CreateCommand()
    $stat.CommandText = 'SELECT COUNT(*) FROM dbo.WORKBENCH_DEFINITION_SNAPSHOT;'
    $allRows = [int]$stat.ExecuteScalar()
    $stat.CommandText = 'SELECT COUNT(*) FROM dbo.MODULES WHERE ISNULL(M_TAG,0)=1;'
    $enabled = [int]$stat.ExecuteScalar()
    Write-Host "PLAN rows=$allRows current=$($rows.Count) enabled_modules=$enabled"

    if ($WhatIf) {
        Write-Host 'WHATIF 未清库、未发布。'
        exit 0
    }

    # -------------------------------------------------------- 3. 登录预检
    # 认证是 Cookie 方案（SignInAsync + CookieAuthenticationDefaults），没有 Bearer 令牌。
    if ([string]::IsNullOrWhiteSpace($Password)) {
        $secure = Read-Host -Prompt "请输入 $UserId 的口令" -AsSecureString
        $Password = [Runtime.InteropServices.Marshal]::PtrToStringAuto(
            [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure))
    }

    $session = New-Object Microsoft.PowerShell.Commands.WebRequestSession
    try {
        Invoke-RestMethod -Uri "$BaseUrl/api/v1/auth/login" -Method Post -WebSession $session `
            -ContentType 'application/json' `
            -Body (@{ userId = $UserId; password = $Password; rememberMe = $true } | ConvertTo-Json) | Out-Null
        Invoke-RestMethod -Uri "$BaseUrl/api/v1/workbench-definitions/staleness" -WebSession $session -Method Get -TimeoutSec 30 | Out-Null
    }
    catch {
        Write-Host "FAIL 登录或权限预检（未清库）：$($_.Exception.Message)"
        exit 3
    }
    Write-Host "PRECHECK login ok user=$UserId"

    # ------------------------------------------------------------ 4. 清空
    $clear = $connection.CreateCommand()
    $clear.CommandText = @'
SET NOCOUNT ON;
DELETE FROM dbo.WORKBENCH_DEFINITION_SNAPSHOT;
DELETE FROM dbo.WORKBENCH_MODULE_DIRTY;
DBCC CHECKIDENT ('dbo.WORKBENCH_DEFINITION_SNAPSHOT', RESEED, 0) WITH NO_INFOMSGS;
'@
    $clear.CommandTimeout = 120
    [void]$clear.ExecuteNonQuery()
    Write-Host 'CLEARED snapshots and dirty marks'

    # ------------------------------------------------------------ 5. 发布
    $response = Invoke-RestMethod -Uri "$BaseUrl/api/v1/workbench-definitions/backfill" `
        -WebSession $session -Method Post -Body '{}' -ContentType 'application/json' -TimeoutSec 900
    $results = @($response)
    $published = @($results | Where-Object { $_.published })
    $failed = @($results | Where-Object { -not $_.published })
    Write-Host "PUBLISH attempted=$($results.Count) published=$($published.Count) failed=$($failed.Count)"

    foreach ($item in $failed) {
        $reason = if ($item.error) { $item.error } else { '' }
        Write-Host ("  SKIP module={0} title={1} {2}" -f $item.moduleId, $item.title, $reason)
    }

    # ------------------------------------------------------------ 6. 核对
    $verify = $connection.CreateCommand()
    $verify.CommandText = @'
SET NOCOUNT ON;
SELECT COUNT(*) AS current_rows, COUNT(DISTINCT M_IDX) AS modules,
       SUM(CASE WHEN VERSION = 1 THEN 1 ELSE 0 END) AS version_one,
       MAX(VERSION) AS max_version
  FROM dbo.WORKBENCH_DEFINITION_SNAPSHOT;
'@
    $vr = $verify.ExecuteReader()
    [void]$vr.Read()
    $currentRows = $vr.GetInt32(0)
    $moduleCount = $vr.GetInt32(1)
    $versionOne = if ($vr.IsDBNull(2)) { 0 } else { $vr.GetInt32(2) }
    $maxVersion = if ($vr.IsDBNull(3)) { 0 } else { $vr.GetInt32(3) }
    $vr.Close()

    Write-Host "VERIFY current_rows=$currentRows modules=$moduleCount version_one=$versionOne max_version=$maxVersion"

    if ($maxVersion -gt 1) {
        Write-Host 'WARN 仍有模块版本号大于 1，说明该模块发布时复用了旧版本号。'
    }
    if ($currentRows -ne $moduleCount) {
        Write-Host 'WARN 当前行数与模块数不一致（同一模块出现多条 IS_CURRENT=1）。'
    }

    exit 0
}
catch {
    Write-Host "ERROR $($_.Exception.Message)"
    exit 1
}
finally {
    if ($connection) { $connection.Dispose() }
}
