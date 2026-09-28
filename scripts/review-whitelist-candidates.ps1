#Requires -Version 7.0
<#
.SYNOPSIS
统一表单白名单放量候选预筛：合并 候选 CSV + 实时 MODULES 元数据 + SP 移植台账 +
当前 EnabledModuleIds，逐模块给出 建议（可放量 / 需确认 / 阻塞 / 已启用）。
.DESCRIPTION
启用判据参考 docs/plans/archive/统一表单编辑器待办.md §A（已归档）：
MODI_URL 非空、默认值/单号已登记、主子表评审。
本脚本只读，不改任何配置；结论供实施顾问逐条确认后写入
UnifiedFormEditor:EnabledModuleIds。
.EXAMPLE
.\scripts\review-whitelist-candidates.ps1
#>
param(
    [string]$CandidatesCsv = 'docs/plans/统一表单白名单候选.csv',
    [string]$InventoryCsv = 'docs/plans/sp-porting-inventory.csv',
    [string]$OutDir = ''
)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
if (-not $OutDir) { $OutDir = Join-Path $root 'logs\goal\phase1' }
New-Item -ItemType Directory -Path $OutDir -Force | Out-Null
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'

$candidates = @(Import-Csv -LiteralPath (Join-Path $root $CandidatesCsv))
$inventory = @(Import-Csv -LiteralPath (Join-Path $root $InventoryCsv))
$app = Get-Content -Raw -LiteralPath (Join-Path $root 'EOS.API\appsettings.json') | ConvertFrom-Json
$enabled = @($app.UnifiedFormEditor.EnabledModuleIds | ForEach-Object { [string]$_ })

# 领域 E2E 覆盖映射（与 AcceptanceCommon.ps1 同源）
$coverageMap = @{
    'E2eBusinessFlow.ps1'               = @('1401','1201','1404','1402','1405','170103','1601','1604','1602','170203','1615','1606','1607','1408','1406','170101','170102','170201','170202')
    'E2eProductionFlow.ps1'             = @('1502','1505','3303')
    'E2eProcessFlow.ps1'                = @('2705','2706')
    'E2eInventoryFlow.ps1'              = @('130103','130105','130104','130106','130108','130109')
    'E2eProcessDomain.ps1'              = @('2703','2704','2708','2709','2710','2711','2911','110103','180207','2707')
    'E2eMoldDomain.ps1'                 = @('2903','2904','2905','2906','2907','2908','2909','2912','2913','2914','2915','2916','2917')
    'E2ePrerequisiteCrud.ps1'           = @('1411','1412','1517','1519','2910','180206')
    'E2eCustomsDomain.ps1'              = @('3006','3014','300301','300302','300304','300305')
    'E2eWageDomain.ps1'                 = @('180301','180502','1803091','180504','180310','180651')
    'E2eSampleHalfDomain.ps1'           = @('2401','2406','2403','2404','1201','2603','2604')
    'E2eHrRestDomain.ps1'               = @('180103','180106','180107','180108','180205','180211','180401')
    'E2eOutsourceQcDomain.ps1'          = @('2803','2805','2806','2816','2815','2817','2818','3901','3307')
    'E2eProductionRestDomain.ps1'       = @('1522','1503','1514','1504','1515','1509')
    'E2eSalesProcurementRestDomain.ps1' = @('1413','1418','1407','1409','1608','1612','1609','1610','1908')
    'E2eBaseDomain.ps1'                 = @('1201','1204','1311','2205','2305')
    'E2eHrEmployeeDomain.ps1'           = @('180102','180110','180105','180111','180208','1616')
    'E2eInventoryRestDomain.ps1'        = @('130102','130107','130101','130110')
}
$byModule = @{}
foreach ($scriptName in $coverageMap.Keys) {
    foreach ($id in $coverageMap[$scriptName]) { $byModule[$id] = $scriptName }
}

$prereqModules = @('1207','1404','1411','1412','1517','1519','2910','180206')
$ids = ($candidates.M_IDX -join ',')
$dbRows = & sqlcmd -S localhost -d EOS.ERP -E -h -1 -W -Q @"
SET NOCOUNT ON;
SELECT LTRIM(RTRIM(CAST(M_IDX AS varchar(20)))),
       LTRIM(RTRIM(ISNULL(MODI_URL,''))),
       LTRIM(RTRIM(ISNULL(NEW_URL,''))),
       LTRIM(RTRIM(ISNULL(MASTER_TABLE,''))),
       LTRIM(RTRIM(ISNULL(DETAIL_TABLE,''))),
       LTRIM(RTRIM(ISNULL(FILTER,''))),
       ISNULL(CAST(AUTO_APPROVE AS int),0),
       ISNULL(CAST(M_TAG AS int),0)
FROM MODULES WHERE CAST(M_IDX AS varchar(20)) IN ($ids)
ORDER BY CAST(M_IDX AS INT);
"@ -s "|" 2>$null

$db = @{}
foreach ($line in $dbRows) {
    $c = $line -split '\|'
    if ($c.Count -lt 8) { continue }
    $db[$c[0].Trim()] = [pscustomobject]@{
        ModiUrl = $c[1].Trim(); NewUrl = $c[2].Trim()
        MasterTable = $c[3].Trim(); DetailTable = $c[4].Trim()
        Filter = $c[5].Trim(); AutoApprove = [int]$c[6].Trim(); MTag = [int]$c[7].Trim()
    }
}

$inv = @{}
foreach ($row in $inventory) {
    $inv[[string]$row.MIdx] = [pscustomobject]@{
        Decision = $row.Decision; Status = $row.Status; E2eCoverage = $row.E2eCoverage
    }
}

# 库存表：以它为主/明细表的模块不得进统一表单编辑白名单——通用表单写入会绕过移动引擎
# （无库存流水、无库别级成本同步、无审计）。查询页可以留着，编辑入口必须封。
$inventoryTables = @('INV_PRO_DEPOT', 'INV_DEPOT_LOG')

function Get-Suggestion {
    param($id, $m, $meta)
    $isEnabled = $id -in $enabled
    $invRow = $inv[$id]
    $decision = if ($invRow) { [string]$invRow.Decision } else { '' }
    $covered = $byModule[$id]
    if ($meta.MasterTable -in $inventoryTables -or $meta.DetailTable -in $inventoryTables) {
        return '不建议：主/明细表是库存表（写入须经移动引擎，通用表单会绕过）'
    }
    if ($isEnabled) { return '已启用' }
    if ($meta.ModiUrl -eq '' -and $meta.NewUrl -eq '') { return '需确认：MODI_URL/NEW_URL 均为空' }
    if ($meta.Filter -ne '') { return '需确认：模块 FILTER 行级过滤' }
    if ($meta.AutoApprove -eq 1) { return '需确认：AUTO_APPROVE=1（保存即批核）' }
    if ($id -in $prereqModules) { return '需确认：依赖真实业务前置数据' }
    if ($meta.DetailTable -ne '') { return '可放量（主子表，建议补域 E2E）' }
    return '可放量（单表）'
}

$rows = [System.Collections.Generic.List[object]]::new()
foreach ($c in $candidates) {
    $id = [string]$c.M_IDX
    $meta = $db[$id]
    if (-not $meta) {
        $rows.Add([pscustomobject]@{
            M_IDX = $id; M_DESC = $c.M_DESC; 状态 = '缺库内元数据'; 主表 = ''; 明细表 = ''
            已启用 = ''; MODI_URL = ''; 移植结论 = ''
            域E2E = ''; FILTER = ''; AUTO_APPROVE = ''; 前置数据 = ''; 建议 = '缺库内元数据'
        })
        continue
    }
    $invRow = $inv[$id]
    $decision = if ($invRow) { [string]$invRow.Decision } else { '无' }
    $rows.Add([pscustomobject]@{
        M_IDX = $id
        M_DESC = $c.M_DESC
        状态 = if ($id -in $enabled) { '已启用' } else { '未启用' }
        主表 = $meta.MasterTable
        明细表 = $meta.DetailTable
        已启用 = $id -in $enabled
        MODI_URL = $meta.ModiUrl
        移植结论 = $decision
        域E2E = $byModule[$id]
        FILTER = if ($meta.Filter -ne '') { $meta.Filter } else { '' }
        AUTO_APPROVE = $meta.AutoApprove
        前置数据 = $id -in $prereqModules
        建议 = Get-Suggestion -id $id -m $c -meta $meta
    })
}

$csvPath = Join-Path $OutDir "whitelist-review-$stamp.csv"
$rows | Export-Csv -LiteralPath $csvPath -NoTypeInformation -Encoding UTF8

$md = [System.Collections.Generic.List[string]]::new()
$md.Add("# 统一表单白名单放量候选预筛（$stamp）")
$md.Add('')
$md.Add("候选总数：$($rows.Count)｜已启用：$(@($rows | Where-Object { $_.已启用 }).Count)｜未启用：$(@($rows | Where-Object { -not $_.已启用 }).Count)")
$md.Add('')
$md.Add('## 未启用候选分类')
$md.Add('')
$pending = @($rows | Where-Object { -not $_.已启用 })
$pending | Group-Object 建议 | Sort-Object Name | ForEach-Object {
    $md.Add("### $($_.Name)（$($_.Count)）")
    $md.Add('')
    $md.Add(($_.Group | ForEach-Object { "$($_.M_IDX) $($_.M_DESC)$(if ($_.明细表) { '（主子表 ' + $_.主表 + '/' + $_.明细表 + '）' } else { '（单表 ' + $_.主表 + '）' })" }) -join '；')
    $md.Add('')
}
$md.Add('## 全量清单')
$md.Add('')
$md.Add('| M_IDX | 描述 | 已启用 | 主表 | 明细表 | 移植结论 | 域E2E | FILTER | AUTO_APPROVE | 前置数据 | 建议 |')
$md.Add('|---|---|---|---|---|---|---|---|---|---|---|---|---|')
foreach ($r in ($rows | Sort-Object { [int]$_.M_IDX })) {
    $esc = ([string]$r.M_DESC) -replace '\|', '／'
    $md.Add("| $($r.M_IDX) | $esc | $($r.已启用) | $($r.主表) | $($r.明细表) | $($r.移植结论) | $($r.域E2E) | $($r.FILTER) | $($r.AUTO_APPROVE) | $($r.前置数据) | $($r.建议) |")
}
$mdPath = Join-Path $OutDir "whitelist-review-$stamp.md"
$md -join "`n" | Set-Content -LiteralPath $mdPath -Encoding utf8

Write-Host "候选 $($rows.Count)：已启用 $(@($rows | Where-Object { $_.已启用 }).Count) / 未启用 $($pending.Count)" -ForegroundColor Cyan
$pending | Group-Object 建议 | Sort-Object Name | ForEach-Object {
    Write-Host "  [$($_.Name)] $($_.Count)" -ForegroundColor Yellow
}
Write-Host "报告：$csvPath" -ForegroundColor Cyan
Write-Host "报告：$mdPath" -ForegroundColor Cyan
