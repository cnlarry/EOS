param(
    [string]$ApiUrl = 'http://localhost:5261',
    [switch]$SkipCompare,
    [switch]$SkipLedger
)

<#
    ADR-012 D9 落地后的验证串（需 EOS.API 以当前构建重启后执行）：

      1. probe-env           —— 库与 API 可达性、bin 是否落后于 HEAD；
      2. 重发布 17 个模块     —— 让工作区里的校验配置进入运行时定义（含 1606 的引用校验范围化）；
      3. CompareAfterSave    —— 保存侧"旧 SP ↔ 新规则"对拍整跑；
      4. 账本刷新            —— A/B/C/D 四类与失败分支。

    用法：pwsh scripts/adr012-b1-verify.ps1
#>

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8

# 配置已落库、需要重发布才能生效的模块：
#   B1c（迁移 079）HR 主从跨单唯一 8 模块 + 离职工资表 2 模块（迁移 082 停用其目录实例）
#   B1a（迁移 077）mou-assess / hr-employee 4 模块
#   （上一批已重发布过的 1606、170103、170203 不在此列——重复发布会无谓地让既有报告降为 C 类）
$moduleIds = @(2914, 180102, 180105, 180110, 180111,
               180205, 180206, 180211, 180651, 180309, 1803091, 180504, 180310, 1803101)

$failures = 0

Write-Host '== 1. 环境预检 ==' -ForegroundColor Cyan
& (Join-Path $PSScriptRoot 'probe-env.ps1') | ForEach-Object { Write-Host "  $_" }

Write-Host '== 2. 重新发布校验配置所在模块 ==' -ForegroundColor Cyan
$republish = & (Join-Path $PSScriptRoot 'adr012-republish-modules.ps1') -ModuleIds $moduleIds -ApiUrl $ApiUrl *>&1
$republish | ForEach-Object { Write-Host "  $_" }
if ($LASTEXITCODE -ne 0 -or ($republish -join "`n") -match 'FAIL ') { $failures++ }

Write-Host '== 2b. 领域规则登记一致性（快照族名受支持 + 目录承接模块均有启用规则）==' -ForegroundColor Cyan
# 该门禁在重发布前必然失败（删码后快照仍指向旧族名），故放在重发布之后作为切换闸。
$registry = & (Join-Path $PSScriptRoot 'check-domain-rule-registry.ps1') *>&1
$registry | ForEach-Object { Write-Host "  $_" }
if ($LASTEXITCODE -ne 0) { $failures++ }

if (-not $SkipCompare) {
    Write-Host '== 3. 保存侧对拍（旧 SP ↔ 新规则）==' -ForegroundColor Cyan
    # 对拍脚本以断言异常表达失败、成功时不显式设置退出码，故按输出标记判定。
    $compare = & (Join-Path $PSScriptRoot '..\EOS.API.Tests\CompareAfterSave.ps1') -ApiUrl $ApiUrl *>&1
    $compare | Select-Object -Last 12 | ForEach-Object { Write-Host "  $_" }
    $compareText = $compare -join "`n"
    if ($compareText -notmatch 'CompareAfterSave 全部通过' -or $compareText -match '断言失败') {
        Write-Host '  FAIL 对拍未全部通过'
        $failures++
    }
}

if (-not $SkipLedger) {
    Write-Host '== 4. 验收账本刷新 ==' -ForegroundColor Cyan
    $ledger = & (Join-Path $PSScriptRoot 'adr012-acceptance-ledger.ps1') *>&1
    $ledgerText = $ledger -join "`n"
    # 只打印分支小结与 C 类清单，完整报告看产物文件
    ($ledger | Select-String -Pattern '###|dual-path valid|engine-only|stale|no evidence|failure branch|dual blocked|unobtainable' |
        Select-Object -First 24) | ForEach-Object { Write-Host "  $($_.Line.Trim())" }
    if ($LASTEXITCODE -ne 0) { $failures++ }
}

if ($failures -gt 0) {
    Write-Host "FAIL 验证串有 $failures 步未通过"
    exit 1
}
Write-Host 'PASS D9 落地验证串全部通过'
exit 0
