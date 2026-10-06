#Requires -Version 7.0
<#
.SYNOPSIS
    清理开发库 EOS.ERP 里的 E2E 夹具残留（按键删除，不按时间范围 + 表名裸删）。

.DESCRIPTION
    删除谓词只允许落在「键列 / 引用列」上（每张表的列在 $Targets 里显式登记），
    审计表（AUDIT_EVENT / AUDIT_FIELD_CHANGE）与借来的主档（DEPOT / UNIT / ...）永不在目标内。
    数量承载表（INV_PRO_DEPOT / INV_DEPOT_LOG / HALF_PRO_DEPOT）只允许按料号删，不按库别删。

    默认 dry-run（只统计不写库）；加 -Apply 才执行 DELETE；-WhatIf 始终优先生效。

.PARAMETER Apply
    真正执行删除。不加则只打印前后行数快照。

.PARAMETER IncludeModules
    只清理归属这些模块的单据表（按 MODULES.MASTER_TABLE / DETAIL_TABLE 解析）。留空 = 全部。

.PARAMETER IncludeTables
    只清理这些表（可多次传或用逗号分隔）。留空 = 全部。

.PARAMETER NoShellPass
    跳过「空壳单据回收」：删掉夹具明细后，整张单据已无任何明细的主表行会被一并删除。

.PARAMETER IncludePermissionResidue
    并入删除「权限残留定项」：USER_ID 无自造前缀、但 CI 被写成 E2E 的 SYSDD / SYSDL 行
    （夹具经 API 建的测试用户及其模块覆盖行）。默认只统计不删。

.PARAMETER ReportPath
    执行记录输出路径（Markdown）。默认 logs/e2e-fixture-cleanup-report.md（留底目录，避免误覆盖已归档的执行记录）。

.PARAMETER SelfTest
    纯静态判别力自检（不连库、不写库）：断言参数面无自由谓词入口、每条删除谓词都带自造标记且无危险记号、
    受保护表不在目标内、数量承载表谓词落在 PRO_NO 列上。**默认不删**由「不给 -Apply 就不进写库分支」保证。

.EXAMPLE
    pwsh scripts/clean-e2e-fixture-residue.ps1                 # dry-run
    pwsh scripts/clean-e2e-fixture-residue.ps1 -Apply -Verbose # 真删
    pwsh scripts/clean-e2e-fixture-residue.ps1 -SelfTest       # 自检（不连库）
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [switch] $Apply,
    [string[]] $IncludeModules = @(),
    [string[]] $IncludeTables = @(),
    [switch] $NoShellPass,
    [switch] $IncludePermissionResidue,
    [string] $ReportPath = 'logs/e2e-fixture-cleanup-report.md',
    [switch] $SelfTest
)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8

trap {
    Write-Host ("FAIL 清理脚本异常终止：" + $_.Exception.Message) -ForegroundColor Red
    exit 1
}

. (Join-Path $PSScriptRoot 'dev/eos-sql.ps1')

# ---------------------------------------------------------------- 目标登记 ---

# 表:键列（删除谓词 = 各列 LIKE N'E2E%' 的 OR）
$Targets = @(
    'BOM_STRU_M:PRO_NO',
    'BOM_STRU_D:PRO_NO,ELEMENT_PRO_NO',
    'CAR_ADDUP_M:ADDUP_NO,ADDUP_TYPE',
    'CAR_ADDUP_D:ADDUP_NO,ADDUP_TYPE,MISSION_NO,MISSION_TYPE',
    'CLIENT:CLIENT_ID',
    'CLIENT_LINKMAN:CLIENT_ID',
    'CLIENT_PRICE_M:CLIENT_ID',
    'CLIENT_PRICE_D:CLIENT_ID,CLIENT_PRO_NO,PRO_NO',
    'COP_CALLBACK_M:CALLBACK_NO,CALLBACK_TYPE,CLIENT_ID',
    'COP_CALLBACK_D:CALLBACK_NO,CALLBACK_TYPE,PRO_NO',
    'COP_FITIN_M:FITIN_NO,FITIN_TYPE,CLIENT_ID',
    'COP_FITIN_D:FITIN_NO,FITIN_TYPE,PRO_NO',
    'COP_ORDER_CHANGE_M:CHANGE_ORDER_NO,CHANGE_ORDER_TYPE,CLIENT_ID',
    'COP_ORDER_CHANGE_D:CHANGE_ORDER_NO,CHANGE_ORDER_TYPE,PRO_NO',
    'COP_ORDER_M:CLIENT_ID,CLIENT_ORDER_NO',
    'COP_ORDER_D:CLIENT_ORDER_NO,PRO_NO',
    'COP_QUOTE_M:CLIENT_ID',
    'COP_QUOTE_D:CLIENT_PRO_NO,PRO_NO',
    'COP_RETURN_M:RETURN_NO,RETURN_TYPE,CLIENT_ID',
    'COP_RETURN_D:RETURN_NO,RETURN_TYPE,PRO_NO',
    'COP_SEND_M:CLIENT_ID',
    'COP_SEND_D:CALLBACK_NO,CALLBACK_TYPE,PRO_NO',
    'CURR:CURR_ID',
    'CUS_ACCOUNT_M:ACCOUNT_NO,ACCOUNT_TYPE,CLIENT_ID,EXPORT_NO,EXPORT_TYPE',
    'CUS_ACCOUNT_D:ACCOUNT_NO,ACCOUNT_TYPE,PRO_NO',
    'CUS_EXPORT_M:ACCOUNT_NO,ACCOUNT_TYPE,CLIENT_ID,EXPORT_NO,EXPORT_TYPE,MANUAL_NO',
    'CUS_EXPORT_D:EXPORT_NO,EXPORT_TYPE,MANUAL_NO,PRO_ID',
    'CUS_IMPORT_M:IMPORT_NO,IMPORT_TYPE,MANUAL_NO',
    'CUS_IMPORT_D:IMPORT_NO,IMPORT_TYPE,MANUAL_NO,PRO_ID',
    'CUS_MANUAL_M:MANUAL_NO',
    'CUS_MANUAL_PRO:MANUAL_NO,PRO_ID',
    'HALF_IN_M:IN_NO,IN_TYPE',
    'HALF_IN_D:IN_NO,IN_TYPE,PRO_NO',
    'HALF_OUT_M:OUT_NO,OUT_TYPE',
    'HALF_OUT_D:OUT_NO,OUT_TYPE,PRO_NO',
    'HALF_PRO_DEPOT:PRO_NO',
    'HR_EMPLOYEE:EMP_ID,EMP_NO',
    'HR_EMPLOYEE_CARD:CARD_ID,EMP_ID',
    'HR_EMPLOYEE_D:EMP_ID',
    'HR_WORKTIME_M:WORKTIME_TYPE',
    'HR_WORKTIME_D:WORKTIME_TYPE',
    'INV_CHECK_STOCK_M:CHECK_STOCK_NO,CHECK_STOCK_TYPE',
    'INV_CHECK_STOCK_D:CHECK_STOCK_NO,CHECK_STOCK_TYPE,PRO_NO',
    'INV_LOAN_M:LOAN_NO',
    'INV_LOAN_D:LOAN_NO,PRO_NO',
    'INV_OCCUR_ADJUST_M:OCCUR_NO,OCCUR_TYPE',
    'INV_OCCUR_ADJUST_D:OCCUR_NO,OCCUR_TYPE,PRO_NO',
    'INV_OCCUR_IN_M:OCCUR_NO,OCCUR_TYPE',
    'INV_OCCUR_IN_D:OCCUR_NO,OCCUR_TYPE,PRO_NO',
    'INV_OCCUR_INIT_M:OCCUR_NO,OCCUR_TYPE',
    'INV_OCCUR_INIT_D:OCCUR_NO,OCCUR_TYPE,PRO_NO',
    'INV_OCCUR_OUT_M:OCCUR_NO,OCCUR_TYPE',
    'INV_OCCUR_OUT_D:OCCUR_NO,OCCUR_TYPE,PRO_NO',
    'INV_PRO_DEPOT:PRO_NO',
    'INV_RETURN_M:RETURN_NO',
    'INV_RETURN_D:LOAN_NO,PRO_NO,RETURN_NO',
    'MOC_BACK_M:BACK_NO,BACK_TYPE',
    'MOC_BACK_D:BACK_NO,BACK_TYPE,GET_NO,GET_TYPE,PRO_NO,PRODUCE_NO,PRODUCE_TYPE',
    'MOC_GET_M:GET_NO,GET_TYPE',
    'MOC_GET_D:GET_NO,GET_TYPE,PRO_NO,PRODUCE_NO,PRODUCE_TYPE',
    'MOC_PRODUCE_CHANGE_M:CHANGE_PRODUCE_NO,CHANGE_PRODUCE_TYPE,PRODUCE_NO,PRODUCE_TYPE',
    'MOC_PRODUCE_CHANGE_D:CHANGE_PRODUCE_NO,CHANGE_PRODUCE_TYPE,PRO_NO,PRODUCE_NO',
    'MOC_PRODUCE_M:PRO_NO,PRODUCE_NO,PRODUCE_TYPE',
    'MOC_PRODUCE_D:PRO_NO,PRODUCE_NO,PRODUCE_TYPE',
    'MOC_PRODUCE_PROCESS_D:PRO_NO,PROCEDURE_ID,PROCEDURE_TYPE_ID',
    'MOC_PRODUCT_IN_M:PRODUCT_IN_NO,PRODUCT_IN_TYPE',
    'MOC_PRODUCT_IN_D:PRO_NO,PRODUCE_NO,PRODUCE_TYPE,PRODUCT_IN_NO,PRODUCT_IN_TYPE',
    'MOC_PRODUCT_OUT_M:PRODUCT_OUT_NO,PRODUCT_OUT_TYPE',
    'MOC_PRODUCT_OUT_D:PRO_NO,PRODUCE_NO,PRODUCE_TYPE,PRODUCT_OUT_NO,PRODUCT_OUT_TYPE',
    'MOC_WORK_D:PRO_NO',
    'MOC_WORK_IN_D:PRO_NO',
    'MOC_WORK_OUT_M:WORK_OUT_TYPE',
    'MOC_WORK_OUT_D:WORK_OUT_TYPE',
    'MOU_ACCEPT_M:ACCEPT_TYPE,APPLY_TYPE,MOULD_ID',
    'MOU_ACCEPTDELE_M:ACCEPTDELE_TYPE',
    'MOU_ACCEPTDELE_D:ACCEPT_TYPE,ACCEPTDELE_TYPE',
    'MOU_APPLY_M:ACCEPT_TYPE,APPLY_TYPE,ASSESS_TYPE,MOULD_ID',
    'MOU_APPLY_D:APPLY_TYPE',
    'MOU_ASSESS_M:ASSESS_TYPE,MOULD_ID',
    'MOU_BATCHTOP_M:BATCH_TYPE,MOULD_ID',
    'MOU_BATCHTOP_D:BATCH_NO,BATCH_TYPE',
    'MOU_GET_M:GET_NO,GET_TYPE',
    'MOU_GET_D:GET_NO,GET_TYPE,PRO_NO',
    'MOU_GET2_M:GET_NO,GET_TYPE',
    'MOU_GET2_D:APPLY_NO,APPLY_TYPE,GET_NO,GET_TYPE,PRO_NO',
    'MOU_MOULD:MOULD_ID,CLIENT_ID,CLIENT_PRO_NO',
    'MOU_PRO_D:MOULD_ID',
    'MOU_SCRAP_D:MOULD_ID',
    'PRODUCT:PRO_NO',
    'PUR_APPLY_M:APPLY_NO,APPLY_TYPE',
    'PUR_APPLY_D:APPLY_NO,APPLY_TYPE,PRO_NO,SUPPLIER_ID,SUPPLIER_PRO_NO',
    'PUR_CALLBACK_M:CALLBACK_NO,CALLBACK_TYPE,SUPPLIER_ID',
    'PUR_CALLBACK_D:CALLBACK_NO,CALLBACK_TYPE,PRO_NO',
    'PUR_CANCEL_M:CANCEL_NO,CANCEL_TYPE,SUPPLIER_ID',
    'PUR_CANCEL_D:CANCEL_NO,CANCEL_TYPE,PRO_NO',
    'PUR_PAY_OTHER:CURR_ID,PAY_NO,PREPAY_NO,PREPAY_TYPE',
    'PUR_PREPAY_M:PREPAY_NO,PREPAY_TYPE',
    'PUR_PREPAY_D:PREPAY_NO,PREPAY_TYPE,PURCHASE_TYPE',
    'PUR_PURCHASE_CHANGE_M:CHANGE_PURCHASE_NO,CHANGE_PURCHASE_TYPE,SUPPLIER_ID',
    'PUR_PURCHASE_CHANGE_D:CHANGE_PURCHASE_NO,CHANGE_PURCHASE_TYPE,PRO_NO',
    'PUR_PURCHASE_M:SUPPLIER_ID',
    'PUR_PURCHASE_D:PRO_NO',
    'PUR_RECEIVE_M:SUPPLIER_ID,SUPPLIER_ORDER_NO',
    'PUR_RECEIVE_D:PRO_NO',
    'QC_SAMPLE_M:PRO_NO',
    'SAM_IN_M:IN_NO,IN_TYPE',
    'SAM_IN_D:IN_NO,IN_TYPE,PRO_NO',
    'SAM_OUT_M:OUT_NO,OUT_TYPE',
    'SAM_OUT_D:OUT_NO,OUT_TYPE,PRO_NO',
    'SAMPLE_PRO:PRO_NO,CLIENT_PRO_NO',
    'SFC_PLAN_M:PLAN_TYPE',
    'SFC_PLAN_D:PLAN_TYPE,PROCEDURE_ID',
    'SFC_PLAN_PROCESS_M:PLAN_PROCESS_NO,PLAN_PROCESS_TYPE,PROCEDURE_TYPE_ID',
    'SFC_PLAN_PROCESS_D:PLAN_PROCESS_NO,PLAN_PROCESS_TYPE,PLAN_TYPE,PRO_NO,PROCEDURE_ID',
    'SFC_PROCESS_M:PRO_NO',
    'SFC_PROCESS_D:PRO_NO,PROCEDURE_ID',
    'SUPPLIER:SUPPLIER_ID',
    'SUPPLIER_LINKMAN:SUPPLIER_ID',
    'SYSDD:USER_ID',
    'SYSDL:USER_ID'
)

# 需要显式谓词的表（键形特殊，无法用「键列 LIKE E2E%」覆盖）
$Specials = @(
    # SYSDF（旧操作流水）已随迁移 325 整表退役（数据留档 logs/archive/retire-324/），不再需要清理谓词
    @{ Table = 'SYSDG';                  Predicate = "G_DESC = N'E2E 权限测试组'";                            Note = '组号由服务端生成，只有组名带自造标记' },
    @{ Table = 'SYSDG_USER';             Predicate = "USER_ID LIKE N'E2E%' OR G_IDX IN (SELECT G_IDX FROM dbo.SYSDG WITH (NOLOCK) WHERE G_DESC = N'E2E 权限测试组')"; Note = '组成员：随组删除' },
    @{ Table = 'WORKBENCH_IDEMPOTENCY';  Predicate = "IDEMPOTENCY_KEY LIKE N'E2E%'";                         Note = '端到端幂等键' },
    @{ Table = 'INV_DEPOT_LOG';          Predicate = "PRO_NO LIKE N'E2E%'";                                  Note = '数量流水：按料号删，不按库别删' }
)

# 空壳单据回收：这些表的单据号是借用来的（单别/单号不带自造前缀），
# 只能靠「明细料号是自造」识别；删完明细后整张单据已无任何明细 ⇒ 主表行一并删除。
$ShellPairs = @(
    @{ Master = 'INV_OCCUR_SCRAP_M';   Detail = 'INV_OCCUR_SCRAP_D';   TypeCol = 'OCCUR_TYPE';   NoCol = 'OCCUR_NO' },
    @{ Master = 'INV_OCCUR_TRANSFER_M'; Detail = 'INV_OCCUR_TRANSFER_D'; TypeCol = 'OCCUR_TYPE'; NoCol = 'OCCUR_NO' },
    @{ Master = 'QC_ANALYSIS_M';       Detail = 'QC_ANALYSIS_D';       TypeCol = 'ANALYSIS_TYPE'; NoCol = 'ANALYSIS_NO' }
)

# 权限残留定项：USER_ID 不带自造前缀、但 CI 被写成 E2E 的行（DBG04U1 ~ DBG07U1 四个用户及其模块覆盖行）。
# 判据是 CI 列：真实用户的 SYSDL.CI 一律 DEFAULT、SYSDD.CI 一律 _ccorp/空，只有夹具经 API 建的测试用户带 E2E。
# 默认只统计不删（避免误删真实授权）；-IncludePermissionResidue 才删。
$PermissionResidue = @(
    @{ Table = 'SYSDD'; Predicate = "CI = N'E2E' AND ISNULL(USER_ID, N'') NOT LIKE N'E2E%'"; Note = '权限残留定项：CI=E2E 且 USER_ID 无自造前缀（夹具建的测试用户的模块覆盖行）' },
    @{ Table = 'SYSDL'; Predicate = "CI = N'E2E' AND ISNULL(USER_ID, N'') NOT LIKE N'E2E%'"; Note = '同上：测试用户本体（删除即移除这几个用户）' }
)
$Holds = if ($IncludePermissionResidue) { @() } else { $PermissionResidue }

# 借来的主档 / 审计 / 元数据：出现在删除目标里就是脚本被改错了
$ProtectedTables = @(
    'DEPOT', 'DEPOT_LOCATION', 'DEPOT_STOCK_POLICY', 'COMPANY', 'DEPT', 'LINE', 'UNIT', 'COLOR', 'SORT', 'STUFF',
    'TAX', 'TAX_TYPE', 'BANK', 'PAYMENT', 'RECEIVE', 'PRICE', 'CUS_COUNTRY', 'CUS_CUSTOMS', 'CUS_DEPOT', 'CUS_IMPOSE', 'CUS_TAX',
    'PAP_TYPE', 'PAP_BRAND', 'PAP_GRAMME', 'PAP_SPECS', 'SFC_PROCEDURE_TYPE', 'MOU_TYPE', 'MOU_OWNER',
    'HR_GRADE', 'HR_TITLE', 'HR_NATION', 'HR_POLITY', 'HR_BLOOD', 'HR_SETUP', 'CAR', 'CAR_OILCARD',
    'BILLKIND', 'MODULES', 'TABLES', 'FIELDS', 'AUDIT_EVENT', 'AUDIT_FIELD_CHANGE'
)

# 数量承载表：谓词必须落在 PRO_NO 上（禁止按库别删）
$QuantityTables = @{ 'INV_PRO_DEPOT' = 'PRO_NO'; 'HALF_PRO_DEPOT' = 'PRO_NO'; 'INV_DEPOT_LOG' = 'PRO_NO' }

# ---------------------------------------------------------------- 工具函数 ---

function Get-EosCount {
    param([string] $Table, [string] $Predicate)
    $sql = "SELECT COUNT(*) FROM dbo.[$Table] WITH (NOLOCK) WHERE $Predicate;"
    $res = @(Invoke-EosSqlQuery -Query $sql)
    if ($res.Count -lt 1) { throw "行数查询失败：$Table" }
    return [int] ($res[0].Trim())
}

function Remove-EosRows {
    param([string] $Table, [string] $Predicate)
    $sql = "DELETE FROM dbo.[$Table] WHERE $Predicate; SELECT @@ROWCOUNT;"
    $res = @(Invoke-EosSqlQuery -Query $sql)
    if ($res.Count -lt 1) { throw "删除失败：$Table" }
    return [int] ($res[-1].Trim())
}

function Get-ShellMasterCount {
    param([string] $Master, [string] $Detail, [string] $TypeCol, [string] $NoCol)
    $sql = @"
SELECT COUNT(*) FROM dbo.[$Master] m WITH (NOLOCK)
WHERE EXISTS (SELECT 1 FROM dbo.[$Detail] d WITH (NOLOCK) WHERE d.[$TypeCol] = m.[$TypeCol] AND d.[$NoCol] = m.[$NoCol] AND d.PRO_NO LIKE N'E2E%')
  AND NOT EXISTS (SELECT 1 FROM dbo.[$Detail] d2 WITH (NOLOCK) WHERE d2.[$TypeCol] = m.[$TypeCol] AND d2.[$NoCol] = m.[$NoCol] AND d2.PRO_NO NOT LIKE N'E2E%');
"@
    $res = @(Invoke-EosSqlQuery -Query $sql)
    if ($res.Count -lt 1) { throw "空壳行数查询失败：$Master" }
    return [int] ($res[0].Trim())
}

function New-PrefixPredicate {
    param([string[]] $Columns)
    return (($Columns | ForEach-Object { "[$_] LIKE N'E2E%'" }) -join ' OR ')
}

# ---------------------------------------------------------------- 自检 ---

# 判别力自检（不连库、不写库）：删除谓词只能来自本文件的登记表，调用方无法注入自由谓词。
if ($SelfTest) {
    $problems = New-Object System.Collections.Generic.List[string]

    # ① 参数面白名单：出现能承载自由 SQL 谓词的参数即判失败
    $allowedParams = @('Apply', 'IncludeModules', 'IncludeTables', 'NoShellPass', 'IncludePermissionResidue', 'ReportPath', 'SelfTest')
    $src = Get-Content -LiteralPath $PSCommandPath -Raw -Encoding utf8
    $m = [regex]::Match($src, '(?s)param\s*\((.*?)\r?\n\)')
    if (-not $m.Success) {
        $problems.Add('无法解析 param 块')
    }
    else {
        foreach ($p in [regex]::Matches($m.Groups[1].Value, '\$([A-Za-z_][A-Za-z0-9_]*)')) {
            if ($allowedParams -notcontains $p.Groups[1].Value) {
                $problems.Add("param 块出现白名单外参数：`$$($p.Groups[1].Value)")
            }
        }
    }

    # ② 目标登记形态：表名/列名必须是标识符，谓词必须锚定自造标记且不含危险记号
    $dangerous = @(';', '--', '/*', '1=1', '1 = 1', 'DROP ', 'DELETE ', 'UPDATE ', 'TRUNCATE ', 'EXEC ', 'sp_')
    $predicates = New-Object System.Collections.Generic.List[string]
    foreach ($item in $Targets) {
        $parts = $item -split ':'
        if ($parts.Count -ne 2) { $problems.Add("目标登记格式错误：$item"); continue }
        if ($parts[0] -notmatch '^[A-Z0-9_]+$') { $problems.Add("目标表名非法：$($parts[0])") }
        $cols = @($parts[1] -split ',')
        foreach ($c in $cols) { if ($c -notmatch '^[A-Z0-9_]+$') { $problems.Add("目标列名非法：$c") } }
        $predicates.Add((($cols | ForEach-Object { "[$_] LIKE N'E2E%'" }) -join ' OR '))
    }
    foreach ($sp in $Specials) { $predicates.Add([string] $sp.Predicate) }
    foreach ($r in $PermissionResidue) { $predicates.Add([string] $r.Predicate) }
    foreach ($p in $predicates) {
        foreach ($d in $dangerous) { if ($p.Contains($d)) { $problems.Add("谓词含危险记号 '$d'：$p") } }
        if ($p -notmatch "N'E2E") { $problems.Add("谓词未锚定自造标记：$p") }
    }

    # ③ 受保护表（借来的主档 / 审计 / 元数据）不得出现在任何删除目标里
    $goalTables = @()
    foreach ($item in $Targets) { $goalTables += ($item -split ':')[0] }
    foreach ($sp in $Specials) { $goalTables += $sp.Table }
    foreach ($r in $PermissionResidue) { $goalTables += $r.Table }
    foreach ($t in $goalTables) {
        if ($ProtectedTables -contains $t) { $problems.Add("删除目标命中受保护表：$t") }
    }

    # ④ 数量承载表的谓词必须落在 PRO_NO 上（禁止按库别整体删）
    foreach ($item in $Targets) {
        $parts = $item -split ':'
        if ($QuantityTables.ContainsKey($parts[0])) {
            if (@($parts[1] -split ',') -notcontains $QuantityTables[$parts[0]]) {
                $problems.Add("数量承载表 $($parts[0]) 的谓词未落在 $($QuantityTables[$parts[0]])")
            }
        }
    }
    foreach ($sp in @($Specials | Where-Object { $QuantityTables.ContainsKey($_.Table) })) {
        if ($sp.Predicate -notmatch [regex]::Escape($QuantityTables[$sp.Table])) {
            $problems.Add("数量承载表 $($sp.Table) 的谓词未落在 $($QuantityTables[$sp.Table])")
        }
    }

    # ⑤ 自检绝不与 -Apply 同时使用
    if ($Apply) { $problems.Add('自检不得与 -Apply 同时使用') }

    if ($problems.Count -gt 0) {
        Write-Host 'FAIL 清理脚本自检：' -ForegroundColor Red
        foreach ($p in $problems) { Write-Host "  - $p" -ForegroundColor Red }
        exit 1
    }
    Write-Host "PASS 清理脚本自检：参数面无自由谓词入口（白名单 $($allowedParams.Count) 项）；$($predicates.Count) 条删除谓词均锚定自造标记且无危险记号；受保护表 $($ProtectedTables.Count) 张不在目标内；数量承载表按 PRO_NO 限定。" -ForegroundColor Green
    exit 0
}

# ---------------------------------------------------------------- 前置校验 ---

$plan = New-Object System.Collections.Generic.List[object]

foreach ($item in $Targets) {
    $parts = $item -split ':'
    if ($parts.Count -ne 2) { throw "目标登记格式错误：$item" }
    $plan.Add([pscustomobject]@{
            Table    = $parts[0]
            Columns  = @($parts[1] -split ',')
            Predicate = (New-PrefixPredicate -Columns @($parts[1] -split ','))
            Note     = ''
        })
}

foreach ($sp in $Specials) {
    $plan.Add([pscustomobject]@{
            Table     = $sp.Table
            Columns   = @()
            Predicate = $sp.Predicate
            Note      = $sp.Note
        })
}

foreach ($t in $plan) {
    if ($ProtectedTables -contains $t.Table) { throw "删除目标命中受保护表（借来的主档/审计/元数据）：$($t.Table)" }
    if ($QuantityTables.ContainsKey($t.Table) -and $t.Predicate -notlike "*$($QuantityTables[$t.Table])*") {
        throw "数量承载表 $($t.Table) 的谓词必须包含 $($QuantityTables[$t.Table])（禁止按库别删）"
    }
}

# 模块过滤：解析 MODULES.MASTER_TABLE / DETAIL_TABLE
# 两种写法都支持：-IncludeModules 1204,1201 与 -IncludeModules 1204 -IncludeModules 1201
$moduleIds = @($IncludeModules | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim() } | Where-Object { $_ })
if ($moduleIds.Count -gt 0) {
    $idList = (($moduleIds | ForEach-Object { "N'$_'" }) -join ',')
    $modTables = @(Invoke-EosSqlQuery -Query "SELECT MASTER_TABLE + N'|' + ISNULL(DETAIL_TABLE, N'') AS TV FROM dbo.MODULES WITH (NOLOCK) WHERE CAST(M_IDX AS nvarchar(20)) IN ($idList);")
    $allow = @()
    foreach ($row in $modTables) { foreach ($p in ($row -split '\|')) { if ($p) { $allow += $p.Trim() } } }
    $allow = @($allow | Sort-Object -Unique)
    Write-Host "模块过滤（$($moduleIds -join ',')）解析到 $($allow.Count) 张表" -ForegroundColor Cyan
    $plan = @($plan | Where-Object { $allow -contains $_.Table })
    $ShellPairs = @($ShellPairs | Where-Object { $allow -contains $_.Master })
}

if ($IncludeTables.Count -gt 0) {
    $wanted = @($IncludeTables | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim().ToUpper() } | Where-Object { $_ })
    $plan = @($plan | Where-Object { $wanted -contains $_.Table.ToUpper() })
    $ShellPairs = @($ShellPairs | Where-Object { ($wanted -contains $_.Master.ToUpper()) -or ($wanted -contains $_.Detail.ToUpper()) })
}

# 权限残留定项：独立于 -IncludeModules / -IncludeTables（它是一次性定项，不是按表扫）
if ($IncludePermissionResidue) {
    foreach ($r in $PermissionResidue) {
        $plan += [pscustomobject]@{
            Table     = $r.Table
            Columns   = @()
            Predicate = $r.Predicate
            Note      = $r.Note
        }
    }
}

# 删除顺序：明细 → 主表 → 主档 → 权限/幂等
# （原先的 rank 0「流水」档是 SYSDF：它已随迁移 325 退役，档位随之取消）
function Get-Rank { param($t)
    if ($t -eq 'WORKBENCH_IDEMPOTENCY') { return 1 }
    if ($t.EndsWith('_D')) { return 2 }
    if ($t.EndsWith('_M')) { return 3 }
    if ($t -in @('INV_PRO_DEPOT', 'INV_DEPOT_LOG', 'HALF_PRO_DEPOT')) { return 2 }
    return 4
}
$plan = @($plan | Sort-Object { Get-Rank $_.Table }, { $_.Table })

if ($plan.Count -eq 0) { throw '没有匹配到任何清理目标（检查 -IncludeModules / -IncludeTables）' }

# ---------------------------------------------------------------- 执行 ---

$willWrite = $Apply -and -not $WhatIfPreference
Write-Host "== E2E 夹具残留清理 ==" -ForegroundColor Cyan
Write-Host "模式: $(if ($willWrite) { 'APPLY（写库）' } else { 'DRY-RUN（只统计）' })  目标表: $($plan.Count)  空壳回收: $(if ($NoShellPass) { '跳过' } else { $ShellPairs.Count }) 对"

$rows = New-Object System.Collections.Generic.List[object]
$totalBefore = 0
$totalAfter = 0

foreach ($t in $plan) {
    $before = Get-EosCount -Table $t.Table -Predicate $t.Predicate
    $deleted = 0
    if ($before -gt 0 -and $willWrite) {
        if ($PSCmdlet.ShouldProcess($t.Table, "删除 $before 行（$($t.Predicate)）")) {
            $deleted = Remove-EosRows -Table $t.Table -Predicate $t.Predicate
        }
    }
    $after = if ($willWrite) { Get-EosCount -Table $t.Table -Predicate $t.Predicate } else { $before }
    $totalBefore += $before
    $totalAfter += $after
    $rows.Add([pscustomobject]@{ 表 = $t.Table; 前 = $before; 删 = $deleted; 后 = $after; 备注 = $t.Note })
    $tag = if (-not $willWrite) { 'DRY' } elseif ($after -ne 0) { 'FAIL' } else { 'PASS' }
    if ($before -gt 0 -or -not $willWrite) {
        Write-Host ("  {0} {1,-26} 前={2,-5} 删={3,-5} 后={4}" -f $tag, $t.Table, $before, $deleted, $after)
    }
}

# 空壳单据回收（明细先删，再把「已无任何明细」的主表行删掉）
$shellRows = New-Object System.Collections.Generic.List[object]
if (-not $NoShellPass) {
    foreach ($p in $ShellPairs) {
        $dPred = "PRO_NO LIKE N'E2E%'"
        $dBefore = Get-EosCount -Table $p.Detail -Predicate $dPred
        $mBefore = Get-ShellMasterCount -Master $p.Master -Detail $p.Detail -TypeCol $p.TypeCol -NoCol $p.NoCol
        $dDel = 0; $mDel = 0
        if ($willWrite) {
            $batch = @"
DECLARE @k TABLE ([TY] nvarchar(20), [NO] nvarchar(40));
INSERT INTO @k ([TY], [NO])
SELECT DISTINCT d.[$($p.TypeCol)], d.[$($p.NoCol)] FROM dbo.[$($p.Detail)] d WITH (NOLOCK) WHERE d.PRO_NO LIKE N'E2E%';
DELETE FROM dbo.[$($p.Detail)] WHERE PRO_NO LIKE N'E2E%';
SELECT @@ROWCOUNT AS DETAIL_DELETED;
DELETE m FROM dbo.[$($p.Master)] m
INNER JOIN @k k ON k.[TY] = m.[$($p.TypeCol)] AND k.[NO] = m.[$($p.NoCol)]
WHERE NOT EXISTS (SELECT 1 FROM dbo.[$($p.Detail)] d3 WHERE d3.[$($p.TypeCol)] = m.[$($p.TypeCol)] AND d3.[$($p.NoCol)] = m.[$($p.NoCol)]);
SELECT @@ROWCOUNT AS MASTER_DELETED;
"@
            $tmp = Join-Path $env:TEMP 'e2e-shell-pass.sql'
            Set-Content -LiteralPath $tmp -Value $batch -Encoding utf8
            $result = Invoke-EosSqlFile -Path $tmp -NoErrorStop
            Remove-Item -LiteralPath $tmp -Force -ErrorAction SilentlyContinue
            # 批里两条 SELECT @@ROWCOUNT 按结果集顺序返回：明细删除数在前，主表删除数在后
            $nums = @($result.Output | Where-Object { $_ -match '^\s*\d+\s*$' } | ForEach-Object { [int] $_.Trim() })
            if ($nums.Count -lt 2) {
                throw "空壳回收批次未返回两个行数（$($p.Master)/$($p.Detail)），输出：$($result.Output -join ' | ')"
            }
            $dDel = $nums[0]; $mDel = $nums[1]
        }
        $totalBefore += ($dBefore + $mBefore)
        $detailAfter = if ($willWrite) { Get-EosCount -Table $p.Detail -Predicate $dPred } else { $dBefore }
        $totalAfter += $detailAfter
        $shellRows.Add([pscustomobject]@{ 主表 = $p.Master; 明细表 = $p.Detail; 明细前 = $dBefore; 明细删 = $dDel; 空壳前 = $mBefore; 空壳删 = $mDel })
        Write-Host ("  PASS 空壳回收 {0}/{1} 明细前={2} 删={3}  空壳前={4} 删={5}" -f $p.Master, $p.Detail, $dBefore, $dDel, $mBefore, $mDel)
    }
}

# 权限残留：未加 -IncludePermissionResidue 时只统计不删
$holdRows = New-Object System.Collections.Generic.List[object]
foreach ($h in $Holds) {
    $c = Get-EosCount -Table $h.Table -Predicate $h.Predicate
    $holdRows.Add([pscustomobject]@{ 表 = $h.Table; 行数 = $c; 原因 = $h.Note })
}

# ---------------------------------------------------------------- 结论 ---

$failed = @($rows | Where-Object { $_.后 -ne 0 })
$ok = ($failed.Count -eq 0)

Write-Host ''
$holdSum = (($holdRows | Measure-Object -Property 行数 -Sum).Sum ?? 0)
$holdNote = if ($IncludePermissionResidue) { '（权限残留已并入删除）' } else { "（权限残留另计 $holdSum 行，未删）" }
Write-Host ("合计：前 {0} 行 / 后 {1} 行{2}" -f $totalBefore, $totalAfter, $holdNote)
if ($willWrite) {
    if ($ok) { Write-Host 'PASS 清理完成：所有目标表二次统计为 0（再跑一次即为 0 行，幂等）' -ForegroundColor Green }
    else { Write-Host ("FAIL 仍有残留：{0}" -f (($failed | ForEach-Object { $_.表 }) -join ', ')) -ForegroundColor Red }
}
else {
    Write-Host 'DRY-RUN 结束：未执行任何 DELETE。确认后加 -Apply 执行。' -ForegroundColor Yellow
}

# ---------------------------------------------------------------- 记录 ---

$lines = New-Object System.Collections.Generic.List[string]
$lines.Add('# ADR-016 E2E 夹具清理 — 执行记录')
$lines.Add('')
$lines.Add("- 时间：$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')")
$lines.Add("- 模式：$(if ($willWrite) { 'APPLY（已写库）' } else { 'DRY-RUN（未写库）' })")
$lines.Add("- 目标表：$($plan.Count)；空壳回收：$($(if ($NoShellPass) { 0 } else { $ShellPairs.Count })) 对")
$lines.Add("- 合计：前 $totalBefore 行 / 后 $totalAfter 行")
$lines.Add('')
$lines.Add('## 逐表')
$lines.Add('')
$lines.Add('| 表 | 前 | 删 | 后 | 备注 |')
$lines.Add('|---|---:|---:|---:|---|')
foreach ($r in ($rows | Sort-Object { -$_.'前' })) {
    $lines.Add(('| {0} | {1} | {2} | {3} | {4} |' -f $r.表, $r.前, $r.删, $r.后, $r.备注))
}
if ($shellRows.Count -gt 0) {
    $lines.Add('')
    $lines.Add('## 空壳单据回收（借来的单别/单号，只能靠明细料号识别）')
    $lines.Add('')
    $lines.Add('| 主表 | 明细表 | 明细前 | 明细删 | 空壳前 | 空壳删 |')
    $lines.Add('|---|---|---:|---:|---:|---:|')
    foreach ($r in $shellRows) { $lines.Add(('| {0} | {1} | {2} | {3} | {4} | {5} |' -f $r.主表, $r.明细表, $r.明细前, $r.明细删, $r.空壳前, $r.空壳删)) }
}
$lines.Add('')
$lines.Add($(if ($IncludePermissionResidue) { '## 权限残留定项（已并入删除）' } else { '## 权限残留定项（未删，需人工确认）' }))
$lines.Add('')
$lines.Add('| 表 | 行数 | 说明 |')
$lines.Add('|---|---:|---|')
foreach ($r in $holdRows) { $lines.Add(('| {0} | {1} | {2} |' -f $r.表, $r.行数, $r.原因)) }
if ($IncludePermissionResidue) {
    foreach ($r in $rows | Where-Object { $_.表 -in @('SYSDD', 'SYSDL') -and $_.备注 -like '权限残留定项*' }) {
        $lines.Add(('| {0} | {1} | {2} |' -f $r.表, $r.前, $r.备注)) 
    }
}

$outDir = Split-Path -Parent $ReportPath
if ($outDir -and -not (Test-Path -LiteralPath $outDir)) { New-Item -ItemType Directory -Path $outDir -Force | Out-Null }
Set-Content -LiteralPath $ReportPath -Value ($lines -join "`n") -Encoding utf8
Write-Host "执行记录已写入：$ReportPath"

if ($willWrite -and -not $ok) { exit 1 }
exit 0
