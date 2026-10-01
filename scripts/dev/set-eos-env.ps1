#Requires -Version 7.0
<#
.SYNOPSIS
    把应用需要的凭据设成 User 级环境变量（配置里 ${VAR} 引用的真值来源）。

.DESCRIPTION
    仓库只保留一份入库的 EOS.API/appsettings.json，敏感项写成 ${MSSQL_ERP_CONN} 这类引用，
    真值只存在于环境变量。

    注意：**工作助手的密钥不由本脚本设置**（ADR-030 §3/§8）。它的变量名是每个供应商一行数据，
    由「工作助手管理 → 模型与用量」在界面上填、并写进环境变量；库里只存变量名。

    两种用法：
    1) 迁移（默认）：从既有的 EOS.API/appsettings.Development.json 取值写入 User 级环境变量，
       迁移后即可删除该文件；
    2) 手工设值：直接给 -ConnectionString / -ApiKey，不依赖旧文件。

    **默认不覆盖已有值**：User 级的 MSSQL_ERP_CONN 往往由 scripts/dev/set-mssql-mcp-env.ps1
    维护（本机连库脚本与真库测试都依赖它），文件里的值未必更好——不同就跳过并提示，确需覆盖加 -Force。

    值一律**不打印**（只报是否写入与长度）。写入后需**重开终端**（或重启承载进程）才会被继承。

.EXAMPLE
    pwsh scripts/dev/set-eos-env.ps1            # 迁移（不覆盖既有值）
    pwsh scripts/dev/set-eos-env.ps1 -WhatIf    # 只看会做什么
#>
[CmdletBinding()]
param(
    [string] $ConnectionString,
    [string] $ApiKey,
    [switch] $Force,
    [switch] $WhatIf
)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8

function Set-EosUserVariable {
    param([string] $Name, [string] $Value, [string] $Note, [switch] $Overwrite)

    if ([string]::IsNullOrWhiteSpace($Value)) {
        Write-Host "SKIP  $Name —— 无可用值（$Note）"
        return
    }

    $existing = [Environment]::GetEnvironmentVariable($Name, 'User')
    if ($existing) {
        if ($existing -eq $Value) {
            Write-Host "PASS  $Name —— User 级已是该值，无需改动"
            return
        }
        if (-not $Overwrite) {
            Write-Host "SKIP  $Name —— User 级已有不同的值（长度 $($existing.Length)），未覆盖；确需覆盖加 -Force"
            return
        }
        Write-Host "WARN  $Name —— 覆盖 User 级既有值（-Force）"
    }

    if ($WhatIf) {
        Write-Host "DRY   $Name —— 将写入 User 级（长度 $($Value.Length)，值不打印）"
        return
    }

    [Environment]::SetEnvironmentVariable($Name, $Value, 'User')
    Write-Host "PASS  $Name —— 已写入 User 级（长度 $($Value.Length)，值不打印）"
}

$fromFile = $false
$conn = $ConnectionString
$key = $ApiKey

if (-not $conn -or -not $key) {
    $settingsPath = Join-Path $PSScriptRoot '..\..\EOS.API\appsettings.Development.json'
    if (Test-Path -LiteralPath $settingsPath) {
        $cfg = Get-Content -Raw -LiteralPath $settingsPath -Encoding utf8 | ConvertFrom-Json
        if (-not $conn) { $conn = $cfg.ConnectionStrings.ErpDatabase }
        if (-not $key) { $key = $cfg.Assistant.ApiKey }
        $fromFile = $true
        Write-Host "来源：$settingsPath"
    }
    else {
        Write-Host "来源：命令行（未找到 appsettings.Development.json，已跳过文件迁移）"
    }
}

Set-EosUserVariable -Name 'MSSQL_ERP_CONN' -Value $conn -Note '业务库连接串（必填；为空则 /health/ready 报 Unhealthy）' -Overwrite:$Force

# 工作助手的密钥**不在这里设**：变量名是每个供应商一行数据（在「工作助手管理 → 模型与用量」里填），
# 值由那个界面写进环境变量，库里只存变量名。这里只提示一次，免得有人以为"设个固定的
# EOS_ASSISTANT_API_KEY 助手就能用"——那个名字已经不是约定了。
if ($key) {
    Write-Warning '已忽略 -ApiKey / 旧的 Assistant:ApiKey：工作助手的密钥现在由「工作助手管理 → 模型与用量」写入环境变量，变量名以那边的配置为准（ADR-030 §8）。'
}

if ($fromFile) {
    Write-Host ''
    Write-Host '迁移完成后即可删除 EOS.API/appsettings.Development.json（它只承载连接串）。'
}

Write-Host ''
Write-Host '环境变量在启动进程时继承：请**重开终端**后再启动 / 重启 EOS.API。'
