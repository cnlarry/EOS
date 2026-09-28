<#
.SYNOPSIS
    开工前环境预检：库可达性、API 端口与构建一致性、bin 产物是否落后于 HEAD。

.DESCRIPTION
    把"每次新会话都要重新踩一遍"的三类摩擦一次报清：

      1. DB：用 SQL 认证（MSSQL_ERP_CONN）连 EOS.ERP；沙箱/受限环境下集成认证会被 SSPI 拒。
      2. API：探测候选端口的 /health/live、/health/version、/health/ready；
         version 端点给出 commit 与进程启动时间，据此判定"运行中的进程是不是当前构建"。
      3. 工作区：HEAD、是否有未提交改动、EOS.API/bin 产物时间是否落后于 HEAD 提交时间。

    只读：不写库、不启停任何服务。

.EXAMPLE
    pwsh scripts/probe-env.ps1
    pwsh scripts/probe-env.ps1 -ApiPorts 5261,5299 -SkipDb
#>
[CmdletBinding()]
param(
    [int[]] $ApiPorts = @(5261),
    [switch] $SkipDb,
    [switch] $SkipApi
)

$ErrorActionPreference = 'Continue'
[Console]::OutputEncoding = [Text.Encoding]::UTF8
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$results = New-Object System.Collections.Generic.List[object]

function Add-Result {
    param([string]$Item, [string]$Status, [string]$Detail)
    $results.Add([pscustomobject]@{ Item = $Item; Status = $Status; Detail = $Detail })
}

# ---------- 1. 仓库 / 工作区 ----------
try {
    $head = (& git -C $root rev-parse --short HEAD 2>$null)
    $headTime = (& git -C $root log -1 --format=%cI 2>$null)
    $dirty = @(& git -C $root status --porcelain 2>$null | Where-Object { $_ -match '\S' })
    Add-Result 'git HEAD' 'INFO' "$head ($headTime)"
    if ($dirty.Count -gt 0) {
        Add-Result '工作区' 'WARN' "$($dirty.Count) 项未提交改动（只提交自己的改动；他人改动勿动）"
    }
    else {
        Add-Result '工作区' 'PASS' '干净'
    }
}
catch {
    Add-Result 'git HEAD' 'WARN' "读取失败：$($_.Exception.Message)"
}

# ---------- 2. 数据库 ----------
if (-not $SkipDb) {
    try {
        . (Join-Path $PSScriptRoot 'dev\eos-sql.ps1')
        $target = Get-EosSqlTarget
        $row = Invoke-EosSqlQuery -Query 'SELECT DB_NAME() + '' | '' + SUSER_SNAME();'
        Add-Result 'EOS.ERP 连接' 'PASS' "$($target.Server)/$($target.Database) auth=$($target.Auth) → $($row -join ',')"
        if ($target.Auth -eq 'integrated') {
            Add-Result 'SQL 认证' 'WARN' 'MSSQL_ERP_CONN 未配置，当前走集成认证（受限环境会被 SSPI 拒）。用 scripts/dev/set-mssql-mcp-env.ps1 配置'
        }
        else {
            Add-Result 'SQL 认证' 'PASS' 'MSSQL_ERP_CONN 可用（沙箱/受限环境下可直连）'
        }
    }
    catch {
        $msg = ($_.Exception.Message -replace "`r?`n", ' ').Trim()
        Add-Result 'EOS.ERP 连接' 'FAIL' $msg.Substring(0, [Math]::Min(180, $msg.Length))
    }
}

# ---------- 3. API ----------
if (-not $SkipApi) {
    foreach ($port in $ApiPorts) {
        $base = "http://localhost:$port"
        try {
            $live = Invoke-WebRequest -Uri "$base/health/live" -TimeoutSec 5 -UseBasicParsing
            Add-Result "API $port /health/live" 'PASS' ([int]$live.StatusCode).ToString()
        }
        catch {
            Add-Result "API $port /health/live" 'INFO' "未运行（$($_.Exception.Message.Split([char]10)[0])）"
            continue
        }
        try {
            $ver = Invoke-WebRequest -Uri "$base/health/version" -TimeoutSec 5 -UseBasicParsing
            $v = $ver.Content | ConvertFrom-Json
            $staleLabel = if ($v.stale) { 'FAIL' } else { 'PASS' }
            $detail = "commit=$($v.commit) built=$($v.buildTimeUtc) processStart=$($v.processStartTimeUtc) stale=$($v.stale)"
            Add-Result "API $port /health/version" $staleLabel $detail
            if ($v.stale) {
                Add-Result "API $port 构建一致性" 'FAIL' '运行中的进程早于二进制构建时间：当前进程不是最新构建，请重启 EOS.API'
            }
        }
        catch {
            Add-Result "API $port /health/version" 'WARN' "端点不可用（旧构建或未开发模式）：$($_.Exception.Message.Split([char]10)[0])"
        }
        try {
            $ready = Invoke-WebRequest -Uri "$base/health/ready" -TimeoutSec 15 -UseBasicParsing
            $r = $ready.Content | ConvertFrom-Json
            $bad = @($r.entries.PSObject.Properties | Where-Object { $_.Value.status -ne 'Healthy' })
            if ($bad.Count -eq 0) { Add-Result "API $port /health/ready" 'PASS' $r.status }
            else { Add-Result "API $port /health/ready" 'WARN' ("$($r.status): " + (($bad | ForEach-Object { $_.Name }) -join ',')) }
        }
        catch {
            Add-Result "API $port /health/ready" 'WARN' $_.Exception.Message.Split([char]10)[0]
        }
    }
}

# ---------- 4. 构建产物 vs HEAD ----------
try {
    $bin = Get-ChildItem (Join-Path $root 'EOS.API\bin') -Recurse -Filter 'EOS.API.dll' -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if (-not $bin) {
        Add-Result 'EOS.API/bin' 'INFO' '未找到构建产物（用 -o $env:TEMP 构建时属正常）'
    }
    else {
        $binTime = $bin.LastWriteTime
        $headDate = [datetime]::Parse($headTime)
        if ($binTime -lt $headDate) {
            Add-Result 'EOS.API/bin' 'WARN' "产物 $($binTime.ToString('yyyy-MM-dd HH:mm')) 早于 HEAD（$($headDate.ToString('yyyy-MM-dd HH:mm'))）：需重新构建/重启"
        }
        else {
            Add-Result 'EOS.API/bin' 'PASS' "产物 $($binTime.ToString('yyyy-MM-dd HH:mm')) 不早于 HEAD"
        }
    }
}
catch {
    Add-Result 'EOS.API/bin' 'INFO' "跳过：$($_.Exception.Message.Split([char]10)[0])"
}

# ---------- 汇总 ----------
Write-Output '== EOS 环境预检 =='
$results | ForEach-Object { Write-Output ("  [{0,-4}] {1,-24} {2}" -f $_.Status, $_.Item, $_.Detail) }
$failed = @($results | Where-Object Status -eq 'FAIL').Count
$warned = @($results | Where-Object Status -eq 'WARN').Count
Write-Output ("-- FAIL={0} WARN={1}" -f $failed, $warned)
exit ([int]($failed -gt 0))
