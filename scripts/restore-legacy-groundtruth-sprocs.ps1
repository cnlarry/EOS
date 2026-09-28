<#
.SYNOPSIS
    还原"旧过程基准"过程：CompareAfterSave.ps1 用来做特征化的遗留保存后钩子。

.DESCRIPTION
    背景：迁移 108 已把 MODULES.AFTERSAVE_SP 与其指向的 63 个过程从库里退役（现代保存链从不调用
    这些钩子）。但 EOS.API.Tests/CompareAfterSave.ps1 用其中 22 个过程作为"旧行为基准"——
    在事务内执行旧过程、取其结果与统一管线对照（真实证据，不是假断言）。过程一消失，这些用例
    便无从对照（脚本以断言失败收场）。

    快照项目（原 `EOS.Database/`）已整体退役，故基准过程改由仓库内的小目录
    `scripts/legacy-groundtruth/sprocs/` 提供——那 22 个文件是从归档 tag
    `archive/legacy-ssdt-full-snapshot` 逐字节取出的（见该目录 README）。本脚本把它们
    重新建回库里，使验收脚本保持可跑：

      - 幂等：已存在即跳过（-Force 才重建）；
      - 只还原基准名单，不碰 MODULES 钩子字段（字段退役状态不变，运行期不恢复遗留链）；
      - 列表内过程只服务于测试基准，属**开发期资产**；终局是把这些用例改成"冻结期望"，
        之后本脚本与这些过程一并删除（见 docs/plans/ADR-012两级覆盖率报告.md §八 批 3/4）。

.EXAMPLE
    pwsh scripts/restore-legacy-groundtruth-sprocs.ps1
    pwsh scripts/restore-legacy-groundtruth-sprocs.ps1 -Force
#>
[CmdletBinding()]
param(
    [string] $Root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path,
    [string[]] $Names = @(),
    [switch] $Force
)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8
. (Join-Path $Root 'scripts\dev\eos-sql.ps1')

# 默认名单 = 验收脚本 `EOS.API.Tests/CompareAfterSave.ps1` 里出现的过程名字面量
# （含以位置参数传给 Test-* 用例的名字，如 `Test-InvOccur 130102 '期初开帐单' 'P_INV_OCCUR_INIT_After_Save' ...`）。
# 不取"全仓引用扫描"的结果——那里还包含测试断言里的**有意退役**名字（如 `[InlineData("P_WF_PRODUCT", false)]`）
# 与根本不存在的哨兵名（P_UNKNOWN_SPROC），它们不应被还原。
$harnessScript = Join-Path $Root 'EOS.API.Tests\CompareAfterSave.ps1'
if ($Names.Count -eq 0 -and (Test-Path -LiteralPath $harnessScript)) {
    $harnessText = [IO.File]::ReadAllText($harnessScript)
    $Names = @([regex]::Matches($harnessText, "(?i)['""](P_[A-Za-z0-9_]{2,})['""]") |
        ForEach-Object { $_.Groups[1].Value } | Sort-Object -Unique)
    Write-Output ("按验收脚本求得基准过程 {0} 个" -f $Names.Count)
}
if ($Names.Count -eq 0 -and (Test-Path -LiteralPath (Join-Path $Root 'scripts\adr012-dead-sproc-report.ps1'))) {
    Write-Output '== 缺省名单为空，回退到全仓引用扫描的 MISSING-EXTERNAL =='
    $scan = & (Join-Path $Root 'scripts\adr012-dead-sproc-report.ps1') *>&1
    $Names = @($scan | Select-String -Pattern 'MISSING-EXTERNAL\s+(P_[A-Z0-9_]+)' | ForEach-Object { $_.Matches[0].Groups[1].Value } | Sort-Object -Unique)
    Write-Output ("扫描得到需还原过程 {0} 个" -f $Names.Count)
}

# CompareAfterSave.ps1 按名调用的旧过程 + 夹具（logs/adr012-acceptance/fixture）按名调用的 *_CHECK 过程。
# 名单与 scripts/adr012-dead-sproc-report.ps1 的 MISSING-EXTERNAL 输出保持一致（该脚本是权威扫描入口）。
$defaultNames = @(
    'P_BOM_STRU_After_Save', 'P_COP_BACK_After_Save', 'P_COP_FITIN_After_Save', 'P_COP_FITOUT_After_Save',
    'P_COP_RETURN_After_Save', 'P_CURR_After_Save', 'P_Employee_Card_After_Save', 'P_HR_WAGE_ITEM_After_Save',
    'P_INV_OCCUR_ADJUST_After_Save', 'P_INV_OCCUR_IN_After_Save', 'P_INV_OCCUR_INIT_After_Save',
    'P_INV_OCCUR_OUT_After_Save', 'P_INV_OCCUR_SCRAP_After_Save', 'P_INV_OCCUR_TRANSFER_After_Save',
    'P_MOC_GET_After_Save', 'P_MOC_PRODUCE_After_Save', 'P_MOC_PRODUCT_IN_After_Save',
    'P_PUR_APPLY_After_Save', 'P_PUR_CANCEL_After_Save', 'P_SFC_DAILY_After_Save',
    'P_SFC_PROCESS_After_Save', 'P_SYSDG_After_Save'
)
# 注：adr012-dead-sproc-report.ps1 的 MISSING-EXTERNAL 还会报出 P_INV_OCCUR_TRANSFER_CHECK，
# 那是历史遗留的**笔误引用**（只有 P_INV_OCCUR_TRANSFER_After_Save 的过程体里写着它，全量快照中并无该对象），
# 不属于本轮退役造成，也不需还原。
if ($Names.Count -eq 0) { $Names = $defaultNames }

$ssdtDir = Join-Path $Root 'scripts\legacy-groundtruth\sprocs'
$restored = 0; $skipped = 0; $failed = @()

foreach ($name in $Names) {
    $exists = (Invoke-EosSqlQuery -Query "SET NOCOUNT ON; SELECT CASE WHEN OBJECT_ID(N'dbo.$name', N'P') IS NULL THEN 0 ELSE 1 END;") -join ''
    if ($exists -eq '1' -and -not $Force) {
        Write-Output "SKIP  ${name}（已存在）"
        $skipped++
        continue
    }
    $file = Join-Path $ssdtDir "$name.sql"
    if (-not (Test-Path -LiteralPath $file)) {
        Write-Output "FAIL  ${name}：scripts/legacy-groundtruth/sprocs 缺少 $name.sql"
        $failed += $name
        continue
    }
    if ($exists -eq '1') {
        Invoke-EosSqlQuery -Query "SET NOCOUNT ON; DROP PROCEDURE dbo.$name;" | Out-Null
    }
    $result = Invoke-EosSqlFile -Path (Resolve-Path $file).Path
    if ($result.ExitCode -ne 0) {
        Write-Output "FAIL  ${name}：$($result.Output -join ' ')"
        $failed += $name
        continue
    }
    Write-Output "PASS  ${name}（自 scripts/legacy-groundtruth/sprocs 还原）"
    $restored++
}

Write-Output "== 汇总：还原=$restored 跳过=$skipped 失败=$($failed.Count) =="
if ($failed.Count -gt 0) { exit 1 }
