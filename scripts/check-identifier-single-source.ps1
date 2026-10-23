<#
.SYNOPSIS
    静态门禁：动态标识符白名单只有一处实现（ADR-036 §5 断言 C）。

.DESCRIPTION
    动态 SQL 的表名/列名白名单必须只有一处字面量实现——`EOS.API/Data/WorkbenchSql.cs` 的
    `Identifier`。同一语义散成多份时，宽松的那一份就是注入面：助手侧此前用的是
    `^[A-Za-z0-9_]+$`（允许数字开头、无长度上限），与工作台的 `^[A-Za-z_][A-Za-z0-9_]{0,127}$`
    并不等价，而两者拼的是同一批 SQL。

    判据是**正文字面量**匹配，零语义判断：扫描代码目录（EOS.API / EOS.API.Tests /
    EOS.Web/src / scripts / db）里该正则的字面量，命中处只允许 `EOS.API/Data/WorkbenchSql.cs`。
    `docs/` 不在扫描范围内——决策记录与手册本来就要写出这条规则本身。

.PARAMETER SelfTest
    自检：在临时目录里造"违规样本 → 必 FAIL"与"清样本 → 必 PASS"两例，不触碰仓库。
#>
param(
    [switch] $SelfTest
)

$ErrorActionPreference = 'Stop'

$literal = '^[A-Za-z_][A-Za-z0-9_]{0,127}$'
# 允许出现的位置：唯一实现，以及本门禁自己（它必须写出被搜的字面量才能搜它）。
$allowed = @('EOS.API/Data/WorkbenchSql.cs', 'scripts/check-identifier-single-source.ps1')
$codeDirs = @('EOS.API', 'EOS.API.Tests', 'EOS.Web/src', 'scripts', 'db')
$extensions = @('*.cs', '*.ts', '*.tsx', '*.ps1', '*.sql')

function Find-LiteralHits {
    param([string] $Root, [string[]] $Dirs)

    $hits = New-Object System.Collections.ArrayList
    foreach ($dir in $Dirs) {
        $full = Join-Path $Root $dir
        if (-not (Test-Path -LiteralPath $full)) { continue }
        $files = Get-ChildItem -LiteralPath $full -Recurse -File -Include $extensions -ErrorAction SilentlyContinue |
            Where-Object { $_.FullName -notmatch '[\\/](bin|obj|node_modules)[\\/]' }
        foreach ($file in $files) {
            if (-not (Select-String -LiteralPath $file.FullName -SimpleMatch -Pattern $literal -Quiet)) { continue }
            $relative = [IO.Path]::GetRelativePath($Root, $file.FullName).Replace('\', '/')
            [void]$hits.Add($relative)
        }
    }
    return $hits
}

try {
    [Console]::OutputEncoding = [Text.Encoding]::UTF8

    if ($SelfTest) {
        $temp = Join-Path ([IO.Path]::GetTempPath()) ("eos-ident-selftest-" + [Guid]::NewGuid().ToString('N'))
        try {
            # 正样本：允许的位置有字面量 + 一个违规文件 ⇒ 必须 FAIL
            New-Item -ItemType Directory -Force -Path (Join-Path $temp 'EOS.API/Data') | Out-Null
            Set-Content -LiteralPath (Join-Path $temp 'EOS.API/Data/WorkbenchSql.cs') -Value "var r = new Regex(`"$literal`");" -Encoding utf8
            Set-Content -LiteralPath (Join-Path $temp 'EOS.API/Offender.cs') -Value "var r = new Regex(`"$literal`");" -Encoding utf8
            $offending = @(Find-LiteralHits -Root $temp -Dirs @('EOS.API') | Where-Object { $allowed -notcontains $_ })
            if ($offending.Count -ne 1) {
                Write-Output "FAIL 自检：违规样本未被识别（期望 1 处，实际 $($offending.Count) 处）"
                exit 1
            }

            # 反样本：删掉违规文件 ⇒ 必须 PASS
            Remove-Item -LiteralPath (Join-Path $temp 'EOS.API/Offender.cs') -Force
            $clean = @(Find-LiteralHits -Root $temp -Dirs @('EOS.API') | Where-Object { $allowed -notcontains $_ })
            if ($clean.Count -ne 0) {
                Write-Output "FAIL 自检：清样本后仍报违规（$($clean.Count) 处）"
                exit 1
            }
        }
        finally {
            Remove-Item -LiteralPath $temp -Recurse -Force -ErrorAction SilentlyContinue
        }
        Write-Output 'PASS 自检：违规样本必命中、清样本必通过'
        exit 0
    }

    $root = Split-Path -Parent $PSScriptRoot
    $hits = @(Find-LiteralHits -Root $root -Dirs $codeDirs)
    $offenders = @($hits | Where-Object { $allowed -notcontains $_ })

    if ($offenders.Count -gt 0) {
        foreach ($file in $offenders) { Write-Output "  [FAIL] 标识符白名单字面量出现在 $file" }
        Write-Output "FAIL：动态标识符白名单必须只有一处实现（$($allowed -join '、')）；改引 WorkbenchSql.Identifier。"
        exit 1
    }

    if ($hits.Count -eq 0) {
        Write-Output "FAIL：未找到任何标识符白名单字面量——判据可能已失效（期望 $($allowed -join '、') 一处）。"
        exit 1
    }

    Write-Output "PASS 标识符白名单单入口：字面量只出现在 $($hits -join '、')。"
    exit 0
}
catch {
    Write-Output "FAIL：$($_.Exception.Message)"
    exit 1
}
