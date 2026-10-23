<#
.SYNOPSIS
    静态门禁：控制器不得直接做模块权限决策（ADR-036 §5 断言 B）。

.DESCRIPTION
    `IPermissionService` 的注释声明自己是"EOS.API 内唯一模块权限判断入口：控制器与仓储不再
    直接调用 `ModuleRightsRepository`"，而实测仍有 21 个控制器直接向它取权限。**声明与事实
    不一致比重复本身更危险**——下一位维护者会照注释去错误的位置找真相。

    判据是字面量匹配 `ModuleRightsRepository`，零语义判断；存量按**具名冻结**处理（只减不增）：
    清单内的文件是待收敛项，清单外任何控制器一旦出现即 FAIL。收敛一处就删一行——清单变短是
    进展，变长是退化。清单里已不再命中的条目由本脚本以 INFO 提示移除。

    **注释同样命中**：本门禁不做 C# 语法分析，`//` 与 XML 文档注释里点到该类名都算命中
    （同类坑见 `check-retired-db-objects.ps1` 里对「清单条目与邻近注释不得点名对象」的说明）。
    只是"指向该类"的注释请改写措辞（指向 `IPermissionService` 或方法名），不要为它加白名单。

.PARAMETER SelfTest
    自检：合成目录里造"清单外控制器引用该类 → 必 FAIL""清单内文件 → 放行"两支，清样本后必 PASS。
#>
param(
    [switch] $SelfTest
)

$ErrorActionPreference = 'Stop'

$pattern = 'ModuleRightsRepository'

# 具名冻结：批 2 第 6 项（权限门收敛）逐条清空。新增条目等于把新写的越界当成既有事实，不允许。
$allowed = @(
    'EOS.API/Controllers/AssistantController.cs'
    'EOS.API/Controllers/BusinessFlowController.cs'
    'EOS.API/Controllers/CarSummaryController.cs'
    'EOS.API/Controllers/ChooserController.cs'
    'EOS.API/Controllers/DepotStockPolicyController.cs'
    'EOS.API/Controllers/FieldAdminController.cs'
    'EOS.API/Controllers/FlowDefinitionController.cs'
    'EOS.API/Controllers/ImportController.cs'
    'EOS.API/Controllers/JobsController.cs'
    'EOS.API/Controllers/KbController.cs'
    'EOS.API/Controllers/MenuAdminController.cs'
    'EOS.API/Controllers/ModuleBusinessConfigController.cs'
    'EOS.API/Controllers/ModuleButtonAdminController.cs'
    'EOS.API/Controllers/ModuleGroupAdminController.cs'
    'EOS.API/Controllers/NavigationGroupsController.cs'
    'EOS.API/Controllers/ReportAdminController.cs'
    'EOS.API/Controllers/RightsAdminController.cs'
    'EOS.API/Controllers/SettingsController.cs'
    'EOS.API/Controllers/TableDataController.cs'
    'EOS.API/Controllers/UserAdminController.cs'
    'EOS.API/Controllers/WorkflowController.cs'
)

function Find-Hits {
    param([string] $Root)

    $controllers = Join-Path $Root 'EOS.API/Controllers'
    if (-not (Test-Path -LiteralPath $controllers)) { return @() }
    return @(Get-ChildItem -LiteralPath $controllers -Recurse -File -Filter *.cs |
        Where-Object { Select-String -LiteralPath $_.FullName -SimpleMatch -Pattern $pattern -Quiet } |
        ForEach-Object { [IO.Path]::GetRelativePath($Root, $_.FullName).Replace('\', '/') })
}

function Get-Offenders {
    param([string[]] $Hits)
    return @($Hits | Where-Object { $allowed -notcontains $_ })
}

try {
    [Console]::OutputEncoding = [Text.Encoding]::UTF8

    if ($SelfTest) {
        $temp = Join-Path ([IO.Path]::GetTempPath()) ("eos-ctrl-perm-" + [Guid]::NewGuid().ToString('N'))
        try {
            $controllersDir = Join-Path $temp 'EOS.API/Controllers'
            New-Item -ItemType Directory -Force -Path $controllersDir | Out-Null
            $offender = Join-Path $controllersDir 'OffenderController.cs'
            # 清单内文件 ⇒ 放行（棘轮语义：存量冻结仍可运行）；取清单第一条，删清单时自检跟着走
            $frozen = Join-Path $controllersDir (Split-Path -Leaf $allowed[0])
            $sample = '    ModuleRightsRepository rightsRepository,'
            Set-Content -LiteralPath $offender -Value $sample -Encoding utf8

            # 清单外控制器引用 ⇒ 必须命中
            $hits = @(Find-Hits -Root $temp)
            $offenders = @(Get-Offenders -Hits $hits)
            if ($offenders.Count -ne 1 -or $offenders[0] -ne 'EOS.API/Controllers/OffenderController.cs') {
                Write-Output "FAIL 自检：清单外控制器未被识别（命中 $($hits.Count) 处，违规 $($offenders.Count) 处）"
                exit 1
            }

            # 清单内文件 ⇒ 放行（棘轮语义：存量冻结仍可运行）
            Set-Content -LiteralPath $frozen -Value $sample -Encoding utf8
            $hits = @(Find-Hits -Root $temp)
            $offenders = @(Get-Offenders -Hits $hits)
            if ($hits.Count -ne 2 -or $offenders.Count -ne 1) {
                Write-Output "FAIL 自检：冻结清单未生效（命中 $($hits.Count) 处，违规 $($offenders.Count) 处，期望 2/1）"
                exit 1
            }

            # 清掉违规样本 ⇒ 必须 PASS
            Remove-Item -LiteralPath $offender -Force
            $offenders = @(Get-Offenders -Hits @(Find-Hits -Root $temp))
            if ($offenders.Count -ne 0) {
                Write-Output "FAIL 自检：清样本后仍报违规（$($offenders.Count) 处）"
                exit 1
            }
        }
        finally {
            Remove-Item -LiteralPath $temp -Recurse -Force -ErrorAction SilentlyContinue
        }
        Write-Output 'PASS 自检：清单外必命中、清单内放行、清样本必通过'
        exit 0
    }

    $root = Split-Path -Parent $PSScriptRoot
    $hits = @(Find-Hits -Root $root)
    $offenders = @(Get-Offenders -Hits $hits)

    foreach ($stale in @($allowed | Where-Object { $hits -notcontains $_ } | Sort-Object)) {
        Write-Output "  [INFO] 冻结清单里的 $stale 已不再引用该仓储，可从脚本的 `$allowed 移除"
    }

    if ($offenders.Count -gt 0) {
        foreach ($file in $offenders) { Write-Output "  [FAIL] 控制器直接取模块权限：$file" }
        Write-Output 'FAIL：控制器的模块级权限决策必须经 IPermissionService（ADR-036 批 2 第 6 项）。'
        exit 1
    }

    Write-Output "PASS 控制器权限边界：Controllers/** 无清单外的权限仓储引用（冻结 $($allowed.Count) 个，待收敛）。"
    exit 0
}
catch {
    Write-Output "FAIL：$($_.Exception.Message)"
    exit 1
}
