<#
.SYNOPSIS
    静态门禁：控制器不得持有数据访问（ADR-036 §5 断言 A）。

.DESCRIPTION
    约束的是"控制器只做契约与授权"这条分工：`Controllers/**` 出现 `new SqlCommand` 意味着
    控制器自己拼 SQL、自己开连接，于是同一条读写在仓储与控制器各有一份，改一处不等于改齐
    （批 1 第 5 项把 7 个控制器的内联 SQL 全部下沉，存量已清零）。

    判据是字面量匹配 `new SqlCommand`，零语义判断。存量清单为空：新增即 FAIL。
    需要临时豁免的（例如某个端点确实只能直连）必须写进 `$allowed` 并写明理由与清除条件。

.PARAMETER SelfTest
    自检：造一个含 `new SqlCommand` 的控制器样本必 FAIL，删除样本后必 PASS。
#>
param(
    [switch] $SelfTest
)

$ErrorActionPreference = 'Stop'

# 存量清零：新增控制器内联 SQL 直接判失败。条目必须写清理由与清除条件。
$allowed = @()
$pattern = 'new SqlCommand'

function Find-Offenders {
    param([string] $Root)

    $controllers = Join-Path $Root 'EOS.API/Controllers'
    if (-not (Test-Path -LiteralPath $controllers)) { return @() }
    $hits = Get-ChildItem -LiteralPath $controllers -Recurse -File -Filter *.cs |
        Where-Object { Select-String -LiteralPath $_.FullName -SimpleMatch -Pattern $pattern -Quiet } |
        ForEach-Object { [IO.Path]::GetRelativePath($Root, $_.FullName).Replace('\', '/') }
    return @($hits | Where-Object { $allowed -notcontains $_ })
}

try {
    [Console]::OutputEncoding = [Text.Encoding]::UTF8

    if ($SelfTest) {
        $temp = Join-Path ([IO.Path]::GetTempPath()) ("eos-ctrl-sql-" + [Guid]::NewGuid().ToString('N'))
        try {
            New-Item -ItemType Directory -Force -Path (Join-Path $temp 'EOS.API/Controllers') | Out-Null
            Set-Content -LiteralPath (Join-Path $temp 'EOS.API/Controllers/OffenderController.cs') `
                -Value 'var command = new SqlCommand("SELECT 1");' -Encoding utf8
            $offenders = @(Find-Offenders -Root $temp)
            if ($offenders.Count -ne 1) { Write-Output "FAIL 自检：违规样本未被识别（$($offenders.Count) 处）"; exit 1 }

            Remove-Item -LiteralPath (Join-Path $temp 'EOS.API/Controllers/OffenderController.cs') -Force
            if (@(Find-Offenders -Root $temp).Count -ne 0) { Write-Output 'FAIL 自检：清样本后仍报违规'; exit 1 }
        }
        finally {
            Remove-Item -LiteralPath $temp -Recurse -Force -ErrorAction SilentlyContinue
        }
        Write-Output 'PASS 自检：控制器内联 SQL 必命中、清样本必通过'
        exit 0
    }

    $root = Split-Path -Parent $PSScriptRoot
    $offenders = @(Find-Offenders -Root $root)
    if ($offenders.Count -gt 0) {
        foreach ($file in $offenders) { Write-Output "  [FAIL] 控制器持有 SQL：$file" }
        Write-Output 'FAIL：控制器的数据访问必须落在 Data/ 下的仓储（ADR-036 批 1 第 5 项）。'
        exit 1
    }
    Write-Output 'PASS 控制器边界：Controllers/** 无内联 SQL（数据访问全部在 Data/ 仓储）。'
    exit 0
}
catch {
    Write-Output "FAIL：$($_.Exception.Message)"
    exit 1
}
