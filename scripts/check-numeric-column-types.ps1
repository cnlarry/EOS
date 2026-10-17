<#
.SYNOPSIS
    静态门禁：迁移脚本不得新增 float 列。

.DESCRIPTION
    依据 docs/decisions/ADR-038-数值列的语义标准化.md（决定二、决定六、决定七）。
    只读文件、不连库，可在 CI 与提交前运行。

    刻意不做的事：不判断"某列该属于哪个语义族"（那需要业务语义，会误报）；
    语义登记由 FIELDS.NUMERIC_ROLE 承载，其一致性见 check-numeric-role.ps1。

.PARAMETER Path
    待检查目录，默认 EOS.API/Data/Migrations。

.PARAMETER SelfTest
    判别性注入自检：合规样本必须放行、违规样本必须拦下。
#>
param(
    [string] $Path,
    [switch] $SelfTest
)

$ErrorActionPreference = 'Stop'

# 判据：ALTER TABLE ... ADD 后紧跟的列类型是 float
$script:FloatAddPattern = '(?i)\bADD\s+\[?([A-Za-z0-9_]+)\]?\s+\[?float\]?'

function Find-FloatAdditions([string] $Dir) {
    $found = New-Object System.Collections.ArrayList
    foreach ($f in (Get-ChildItem $Dir -Filter *.sql -Recurse)) {
        $lines = Get-Content $f.FullName -Encoding utf8
        for ($i = 0; $i -lt $lines.Count; $i++) {
            if ($lines[$i] -match $script:FloatAddPattern) {
                [void]$found.Add("$($f.Name):$($i + 1) 新增 float 列 [$($Matches[1])]")
            }
        }
    }
    return $found
}

try {
    [Console]::OutputEncoding = [Text.Encoding]::UTF8
    $root = Split-Path -Parent $PSScriptRoot

    if ($SelfTest) {
        # 判别性注入：同一条判据必须放行合规样本、拦下违规样本
        $cases = @(
            @{ Text = 'ALTER TABLE [dbo].[T] ADD [AMOUNT] [decimal](18,4) NULL;'; Expect = $false },
            @{ Text = 'ALTER TABLE [dbo].[T] ADD [QTY] [float] NULL;'; Expect = $true },
            @{ Text = 'ALTER TABLE [dbo].[T] ADD [QTY] float NULL;'; Expect = $true }
        )
        foreach ($case in $cases) {
            $hit = [bool]($case.Text -match $script:FloatAddPattern)
            if ($hit -ne $case.Expect) {
                throw "自检失败：判据对样本 [$($case.Text)] 的结论为 $hit，期望 $($case.Expect)"
            }
        }
        Write-Output 'PASS 自检：合规样本放行、违规样本拦下'
        exit 0
    }

    $dir = if ($Path) { $Path } else { Join-Path $root 'EOS.API/Data/Migrations' }
    if (-not (Test-Path $dir)) { Write-Output "SKIP 迁移目录不存在：$dir"; exit 0 }

    $violations = @(Find-FloatAdditions $dir)
    if ($violations.Count -gt 0) {
        Write-Output 'FAIL 迁移脚本中出现 float 列（应使用 ADR-038 的标准类型）：'
        $violations | ForEach-Object { Write-Output "  $_" }
        exit 1
    }

    Write-Output 'PASS 迁移脚本未新增 float 列'
    exit 0
}
catch {
    Write-Error $_.Exception.Message
    exit 1
}
