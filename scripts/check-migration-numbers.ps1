<#
.SYNOPSIS
    迁移编号唯一性门禁：同一个编号只能对应一个迁移脚本，且文件名必须符合 `NNN_描述.sql`。

.DESCRIPTION
    编号是**排序键**，文件名才是身份（DbUp 把资源名登记进 `dbo.ERP_SCHEMA_JOURNAL`，只据此判重）。
    于是"两个文件同号"不会让迁移漏跑，却会留下两处说不清的东西：编号不再能指向唯一脚本，
    台账、发布说明与后来者都得额外解释一遍。成因几乎总是并行开发时两个人同时取「当前最大 + 1」。

    本门禁只做两件确定性检查（只读文件、不连库，可在 CI 跑）：

      A. 编号唯一：`NNN_` 前缀相同的文件超过一个即失败，打印该编号下的全部文件名；
      B. 命名格式：文件名必须是 `NNN_小写描述.sql`（三位数字 + 下划线 + 小写字母/数字/下划线）。

    **撞号之后不要只改文件名**：改名的脚本在新台账里不存在，会被当作新脚本**再跑一次**；
    若同时把台账里旧名那一行删掉，改名后必须保证脚本本身幂等，并在脚本内完成这次清理
    （范例：`445_chooser_self_return_targets.sql`——改名 + 末尾按旧资源名 DELETE 台账行）。

.EXAMPLE
    pwsh scripts/check-migration-numbers.ps1             # exit 0 = 编号唯一、命名合规
    pwsh scripts/check-migration-numbers.ps1 -SelfTest   # 正反样本自检门禁本身有判别力
#>
[CmdletBinding()]
param(
    [string] $MigrationsDir = (Join-Path (Split-Path -Parent $PSScriptRoot) 'EOS.API/Data/Migrations'),
    [switch] $SelfTest
)

$ErrorActionPreference = 'Stop'

$namePattern = '^(?<number>\d{3})_[a-z0-9_]+\.sql$'

function Test-MigrationNames {
    <# 对一组文件名做两项检查，返回违规清单（空 = 通过）。 #>
    param([string[]] $Names)

    $problems = @()

    foreach ($name in $Names) {
        if ($name -cnotmatch $namePattern) {
            $problems += [pscustomobject]@{
                Kind = '命名格式'
                Detail = "$name —— 期望「三位编号_小写描述.sql」"
            }
        }
    }

    foreach ($group in ($Names | Where-Object { $_ -cmatch $namePattern } |
            Group-Object { [regex]::Match($_, $namePattern).Groups['number'].Value } |
            Where-Object { $_.Count -gt 1 })) {
        $problems += [pscustomobject]@{
            Kind = '编号重复'
            Detail = "编号 $($group.Name) 有 $($group.Count) 个脚本：$($group.Group -join '、')"
        }
    }

    return $problems
}

if ($SelfTest) {
    $failures = 0

    $clean = @('001_alpha.sql', '002_beta_gamma.sql', '430_retire_attendance_jobs_menu.sql')
    if ((Test-MigrationNames -Names $clean).Count -ne 0) {
        Write-Host 'FAIL 合规样本被误判' -ForegroundColor Red
        $failures++
    }

    $duplicated = @(
        '430_chooser_self_return_targets.sql'
        '430_retire_attendance_jobs_menu.sql'
    )
    $hits = Test-MigrationNames -Names $duplicated
    if (-not ($hits | Where-Object { $_.Kind -eq '编号重复' })) {
        Write-Host 'FAIL 重号样本未被拦下' -ForegroundColor Red
        $failures++
    }

    $badName = @('430b_chooser.sql', '12_short.sql', '430_Chooser.SQL')
    foreach ($sample in $badName) {
        if (-not ((Test-MigrationNames -Names @($sample)) | Where-Object { $_.Kind -eq '命名格式' })) {
            Write-Host "FAIL 非法命名未被拦下：$sample" -ForegroundColor Red
            $failures++
        }
    }

    if ($failures -gt 0) { exit 1 }
    Write-Host 'PASS 自检：合规样本放行，重号与非法命名均被拦下' -ForegroundColor Green
    exit 0
}

if (-not (Test-Path -LiteralPath $MigrationsDir)) {
    Write-Host "FAIL 迁移目录不存在：$MigrationsDir" -ForegroundColor Red
    exit 1
}

$names = @(Get-ChildItem -LiteralPath $MigrationsDir -Filter '*.sql' -File |
    Sort-Object Name | Select-Object -ExpandProperty Name)

$problems = @(Test-MigrationNames -Names $names)
if ($problems.Count -gt 0) {
    foreach ($problem in $problems) {
        Write-Host "FAIL [$($problem.Kind)] $($problem.Detail)" -ForegroundColor Red
    }
    Write-Host ''
    Write-Host '处理口径：新迁移一律取当前最大编号 +1；已落库的同号脚本改名时必须保证幂等，' -ForegroundColor Yellow
    Write-Host '并在脚本内删除旧资源名的台账行（范例 445_chooser_self_return_targets.sql）。' -ForegroundColor Yellow
    exit 1
}

Write-Host "PASS 迁移编号唯一、命名合规（$($names.Count) 个脚本）" -ForegroundColor Green
exit 0
