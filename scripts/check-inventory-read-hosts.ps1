<#
.SYNOPSIS
    库存读取收口门禁：生产代码与配置面都不得再出现直读库存表的宿主。

.DESCRIPTION
    库存余额是四键（料号 / 库别 / 库位 / 批次）。读取口径散落时，每多一个宿主就多一个
    "忘了某个维度"的漏点，而且**编译与单测都发现不了**——只有真实数据撞上才炸。
    因此读库存一律经 EOS.API/Data/Inventory/InventoryQueryService.cs。

    **两侧检查**：

    ① **代码侧**（`EOS.API/**/*.cs`，排除 `Data/Migrations` —— 一次性迁移脚本不是运行时读取）：
       命中大写表名 INV_PRO_DEPOT / INV_DEPOT_LOG / INV_BATCH_M / INV_BATCH_D 的文件必须在白名单内
      （服务本身 + 两条写路径）。批次账纳入的原因：它是效期能力的读取对象，新增宿主同样会绕过收口。

    ② **配置侧**（连库查）——代码收口之后，**配置仍能让运行期拼出直读库存表的 SQL**，
       而这类引用在 C# 里看不见，是代码侧扫不到的另一半：
         · **FAIL 类**（会形成"聚合/计算口径"，可能与服务口径分叉）：
           `FIELDS.VIRTUAL_EXP` / `FIELDS.CONVERT_FUNCTION`、
           `MODULES.FILTER`、`MODULES.MASTER_TABLE` / `MODULES.DETAIL_TABLE`、
           `MODULE_VALIDATION_RULE.PARAM_STRUCT`、`MODULE_BUSINESS_ACTION.PARAM_STRUCT`；
           命中必须在配置白名单里（两个已加 fail-closed 守卫的库存来源声明 + 两个待收口的虚拟列
           + 七个以库存表 / 批次账为主表的查询、报表模块）。
           `MODULES.MASTER_TABLE` / `DETAIL_TABLE` 另有一层含义：它同时是**写路径**入口——
           主表就是库存表的模块一旦进了统一表单编辑白名单，通用表单就能直接改库存，绕过移动引擎。
           · **零命中类**（必须为空，**不得进白名单**）：`FIELDS:ENGINE_OWNED_EDITABLE` ——
           引擎维护的库存类列（`PRODUCT` 的 `QTY`/`MRP_QTY`/`NOT_*_QTY`/`IN_BUY_QTY` 及两个派生列、
           `INV_BATCH_M` 的 `IN_SUM`/`OUT_SUM`/`LATELY_*_DATE`）必须 `IS_READONLY=1`：
           余额/流水/批次账由移动引擎写、主档统计由 MRP 重算回写，可编辑即等于绕过引擎改库存口径
           （`IN_SUM - OUT_SUM` 正是引擎判定批号是否足够的依据）。服务端对只读字段一律拒收（`READONLY_FIELD`）。
         · **INFO 类**（行级/排序/关联类，不构成聚合口径漂移，只提示不计失败）：
           `TABLES.QUERY_RELATION`、`REPORT_SORT.SORT_FIELDS` / `GROUP_FIELDS`。

    ③ **写路径交叉断言**（配置面 × appsettings）：主表/明细表是余额表、流水表或**批次账**的模块，
       不得出现在 `UnifiedFormEditor.EnabledModuleIds` 里——"能看"与"能改"分开管：
       查询页可以留着，编辑入口必须封（`1303 料件库存资料`、`1302 料件批号资料` 即按此关闭）。
       这两个模块的浏览入口在 `UnifiedFormEditor.ReadOnlyModuleIds`（双击进浏览态 + 自定义按钮可点，
       新增/修改/删除端点仍 404）；只读名单**不是**本断言的放行对象——两名单合并即等于重开写路径。

    白名单**只减不增**（棘轮）：收口推进后应当越来越少，新增命中即 FAIL。

    大小写口径：**代码侧对表名大小写敏感**（报表编号 `INV_Pro_Depot_List` 与物理表不是一回事）；
    配置侧按列内容分别处理——SQL 文本列（虚拟列表达式等）用大小写不敏感匹配（配置里表名可能任意大小写），
    排序字段用**带点限定名**匹配（`INV_PRO_DEPOT.`），既认物理列引用、又不误伤报表编号。

.PARAMETER ConnectionString
    可选连接串；默认取 MSSQL_ERP_CONN（与仓库其它脚本一致）。

.PARAMETER SelfTest
    正反自检：① 代码侧埋一个直读命中（临时文件，用后删除）；② 配置侧喂一组合成引用
    （含一条已在白名单内、一条不在）——断言前者放行、后者被判 FAIL。

.EXAMPLE
    pwsh scripts/check-inventory-read-hosts.ps1            # exit 0 = clean
    pwsh scripts/check-inventory-read-hosts.ps1 -SelfTest  # 正反自检
#>
[CmdletBinding()]
param(
    [string] $Root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path,
    [string] $ConnectionString,
    [switch] $SelfTest
)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8

. (Join-Path $PSScriptRoot 'dev/eos-sql.ps1')

$apiDir = Join-Path $Root 'EOS.API'
$tableNames = @('INV_PRO_DEPOT', 'INV_DEPOT_LOG', 'INV_BATCH_M', 'INV_BATCH_D')

# 代码侧白名单：服务本身 + 两条写路径（过账引擎 / 策略服务归位归并）
$allowed = @(
    'Data\Inventory\InventoryQueryService.cs',
    'Data\Effects\ServiceEffectHandlers\InventoryMoveHandler.cs',
    'Data\DepotStockPolicyService.cs'
) | ForEach-Object { $_.Replace('\', [IO.Path]::DirectorySeparatorChar) }

# 配置侧白名单（FAIL 类）。每条都必须写明"为什么可以留着"。
$allowedConfigRefs = [ordered]@{
    'VALIDATION:1406:custom-validation'      = '库存来源声明；处理器有 fail-closed 守卫：配的表必须就是余额表本身'
    'ACTION:130101:stocktake-scope-generate' = '同上（库存来源声明 + 同一道守卫）'
    'FIELDS:VIRTUAL_EXP:INV_PRO_DEPOT.SUB_QTY' = '行级虚拟列：逐行求值语义正确（表达式作用于行级余额表本身），且无聚合消费方 ⇒ 非缺陷；保留登记以覆盖"配置面引用库存表"这一客观事实'
    'FIELDS:VIRTUAL_EXP:INV_Pro_Depot.LONGTH'  = '同上（表达式里表名大小写与物理表不同）'
    'MODULES:MASTER_TABLE:1303'              = '料件库存资料：以余额表为主表的查询模块（只读；写路径已按 110310 同款封掉）'
    'MODULES:MASTER_TABLE:1305'              = '库存日志：以流水表为主表的查询模块（本就不在统一表单白名单内）'
    'MODULES:MASTER_TABLE:139809'            = '料件库存资料明细（报表承载页，只读）'
    'MODULES:MASTER_TABLE:139811'            = '库存日志明细（报表承载页，只读）'
    'MODULES:MASTER_TABLE:139901'            = '库存日报表（报表承载页；聚合口径已走报表聚合注册表）'
    'MODULES:MASTER_TABLE:1302'              = '料件批号资料：以批次账为主表的查询模块（只读；写路径与 1303 同款封掉）'
    'MODULES:MASTER_TABLE:139808'            = '料件批号资料明细（报表承载页，只读）'
}

function Get-InventoryHits {
    param([string] $Directory)

    $hits = @{}
    foreach ($file in Get-ChildItem -Path $Directory -Recurse -File -Filter '*.cs') {
        $relative = [IO.Path]::GetRelativePath($Directory, $file.FullName)
        if ($relative -like 'Data*Migrations*') { continue }
        $lines = @(Get-Content -LiteralPath $file.FullName -Encoding UTF8)
        for ($index = 0; $index -lt $lines.Count; $index++) {
            $trimmed = $lines[$index].TrimStart()
            if ($trimmed.StartsWith('//')) { continue }   # 注释里提到表名不是读取
            foreach ($table in $tableNames) {
                if ($lines[$index].Contains($table)) {
                    if (-not $hits.ContainsKey($relative)) { $hits[$relative] = @() }
                    $hits[$relative] += "$($index + 1): $($trimmed.Trim())"
                    break
                }
            }
        }
    }
    return $hits
}

function Test-Gate {
    param([hashtable] $Hits)

    $problems = New-Object System.Collections.Generic.List[string]
    foreach ($file in ($Hits.Keys | Sort-Object)) {
        if ($allowed -contains $file) { continue }
        $problems.Add("库存读取必须经 InventoryQueryService：$file（$($Hits[$file].Count) 处）")
        foreach ($line in ($Hits[$file] | Select-Object -First 3)) {
            $problems.Add("    $file  L$line")
        }
    }
    return $problems
}

function Get-ConfigRefs {
    <#
      连库取配置面引用：返回 @{ Fail = @(key...); Info = @(key...) }。
      SQL 文本列用大小写不敏感匹配（配置里表名可能任意大小写）；排序字段用带点限定名匹配。
    #>
    param([string] $ConnectionString)

    $sql = @'
SET NOCOUNT ON;
SELECT CONCAT('FAIL|FIELDS:VIRTUAL_EXP:', T_ID, '.', F_ID) FROM dbo.FIELDS
 WHERE ISNULL(VIRTUAL_EXP,'') LIKE '%INV_PRO_DEPOT%' OR ISNULL(VIRTUAL_EXP,'') LIKE '%INV_DEPOT_LOG%';
SELECT CONCAT('FAIL|FIELDS:CONVERT_FUNCTION:', T_ID, '.', F_ID) FROM dbo.FIELDS
 WHERE ISNULL(CONVERT_FUNCTION,'') LIKE '%INV_PRO_DEPOT%' OR ISNULL(CONVERT_FUNCTION,'') LIKE '%INV_DEPOT_LOG%';
SELECT CONCAT('FAIL|MODULES:FILTER:', M_IDX) FROM dbo.MODULES
 WHERE ISNULL(FILTER,'') LIKE '%INV_PRO_DEPOT%' OR ISNULL(FILTER,'') LIKE '%INV_DEPOT_LOG%';
SELECT CONCAT('FAIL|MODULES:MASTER_TABLE:', M_IDX) FROM dbo.MODULES
 WHERE MASTER_TABLE IN ('INV_PRO_DEPOT','INV_DEPOT_LOG','INV_BATCH_M');
SELECT CONCAT('FAIL|MODULES:DETAIL_TABLE:', M_IDX) FROM dbo.MODULES
 WHERE DETAIL_TABLE IN ('INV_PRO_DEPOT','INV_DEPOT_LOG','INV_BATCH_M');
/* 引擎维护列必须只读：可编辑即 FAIL（本类不允许出现在白名单里，期望命中 0 条）。
   余额/流水/批次账由移动引擎写，产品主档的库存与 MRP 派生列由 MRP 重算回写；
   这些列一旦可编辑，通用表单就能绕过引擎改库存口径（批次账的 IN_SUM - OUT_SUM 正是
   引擎判定批号是否足够的依据）。 */
SELECT CONCAT('FAIL|FIELDS:ENGINE_OWNED_EDITABLE:', T_ID, '.', F_ID) FROM dbo.FIELDS
 WHERE ISNULL(IS_READONLY,0)=0 AND (
       (T_ID='PRODUCT' AND F_ID IN ('QTY','MRP_QTY','NOT_SEND_QTY','NOT_IN_QTY','NOT_GET_QTY','IN_BUY_QTY',
                                    'MRP_QTY_ABS','SAFETY_MRP_QTY','AMOUNT'))
    OR (T_ID='INV_BATCH_M' AND F_ID IN ('IN_SUM','OUT_SUM','LATELY_IN_DATE','LATELY_OUT_DATE','LATELY_CHECK_DATE')));
/* 盘点单明细的重复校验键必须含库位与批次：两键判重会把同一料号同一库别下的多个库位/批次
   误判成明细重复，而按库区生成明细恰恰会写出这样的多行。期望命中 0 条。 */
SELECT CONCAT('FAIL|TABLES:DF_VERIFY_MISSING_DIMENSION:', T_ID) FROM dbo.TABLES
 WHERE T_ID='INV_CHECK_STOCK_D'
   AND (ISNULL(DF_VERIFY,'') NOT LIKE '%LOCATION_NO%' OR ISNULL(DF_VERIFY,'') NOT LIKE '%BATCH_NO%');
SELECT CONCAT('FAIL|VALIDATION:', M_IDX, ':', VALIDATION_KEY) FROM dbo.MODULE_VALIDATION_RULE
 WHERE PARAM_STRUCT LIKE '%INV_PRO_DEPOT%' OR PARAM_STRUCT LIKE '%INV_DEPOT_LOG%';
SELECT CONCAT('FAIL|ACTION:', M_IDX, ':', EFFECT_KEY) FROM dbo.MODULE_BUSINESS_ACTION
 WHERE PARAM_STRUCT LIKE '%INV_PRO_DEPOT%' OR PARAM_STRUCT LIKE '%INV_DEPOT_LOG%';
SELECT CONCAT('INFO|TABLES:QUERY_RELATION:', T_ID) FROM dbo.TABLES
 WHERE ISNULL(QUERY_RELATION,'') LIKE '%INV_PRO_DEPOT%' OR ISNULL(QUERY_RELATION,'') LIKE '%INV_DEPOT_LOG%';
SELECT CONCAT('INFO|REPORT_SORT:', REPORT_ID) FROM dbo.REPORT_SORT
 WHERE ISNULL(SORT_FIELDS,'') LIKE '%INV_PRO_DEPOT.%' OR ISNULL(SORT_FIELDS,'') LIKE '%INV_DEPOT_LOG.%'
    OR ISNULL(GROUP_FIELDS,'') LIKE '%INV_PRO_DEPOT.%' OR ISNULL(GROUP_FIELDS,'') LIKE '%INV_DEPOT_LOG.%';
'@

    $fail = New-Object System.Collections.Generic.List[string]
    $info = New-Object System.Collections.Generic.List[string]
    foreach ($row in @(Invoke-EosSqlQuery -ConnectionString $ConnectionString -Query $sql)) {
        $text = "$row".Trim()
        if ($text -eq '') { continue }
        $parts = $text -split '\|', 2
        if ($parts.Count -ne 2) { continue }
        if ($parts[0] -eq 'FAIL') { [void]$fail.Add($parts[1].Trim()) } else { [void]$info.Add($parts[1].Trim()) }
    }
    return @{ Fail = @($fail | Sort-Object -Unique); Info = @($info | Sort-Object -Unique) }
}

function Test-ConfigRefs {
    <#
      纯函数（便于自检）：给定 FAIL 类引用与白名单，返回问题清单。
      白名单外的引用 ⇒ 问题（提示词形如"库存读取必须经 InventoryQueryService"）。
    #>
    param([string[]] $Refs, [System.Collections.IDictionary] $Allowed)

    $problems = New-Object System.Collections.Generic.List[string]
    foreach ($ref in ($Refs | Sort-Object -Unique)) {
        if ($Allowed.Contains($ref)) { continue }
        $problems.Add("配置面直读库存表，必须收口或显式登记：$ref")
    }
    return $problems
}

function Get-UnifiedFormWhitelist {
    <#
      读统一表单编辑白名单（appsettings*.json 的 UnifiedFormEditor.EnabledModuleIds）。
      读不到即返回空数组：白名单为空表示不启用任何模块，写路径断言自然通过。
    #>
    param([string] $ApiDirectory)

    $ids = New-Object System.Collections.Generic.List[int]
    foreach ($file in @(Get-ChildItem -Path $ApiDirectory -Filter 'appsettings*.json' -File | Sort-Object Name)) {
        $json = Get-Content -LiteralPath $file.FullName -Raw -Encoding UTF8 | ConvertFrom-Json
        if ($null -eq $json.UnifiedFormEditor -or $null -eq $json.UnifiedFormEditor.EnabledModuleIds) { continue }
        foreach ($id in @($json.UnifiedFormEditor.EnabledModuleIds)) { [void]$ids.Add([int]$id) }
    }
    return @($ids | Sort-Object -Unique)
}

function Test-WritableInventoryModules {
    <#
      纯函数（便于自检）：主表或明细表就是库存表的模块，不得出现在统一表单编辑白名单里。
      原因：通用表单的写入直接落主表——不产生库存流水、不做库别级成本同步、不经移动引擎，
      等于绕开库存写入收口（与库存策略表被通用表单直写属同一类问题）。
    #>
    param([int[]] $EnabledModuleIds, [string[]] $InventoryModules)

    $problems = New-Object System.Collections.Generic.List[string]
    $flagged = @{}
    foreach ($ref in $InventoryModules) { $flagged[$ref] = $true }
    foreach ($id in @($EnabledModuleIds | Sort-Object -Unique)) {
        foreach ($entry in @(
            @{ Key = "MODULES:MASTER_TABLE:$id"; Slot = '主表' },
            @{ Key = "MODULES:DETAIL_TABLE:$id"; Slot = '明细表' }
        )) {
            if ($flagged.ContainsKey($entry.Key)) {
                $problems.Add("模块 $id 的$($entry.Slot)就是库存表，却在统一表单编辑白名单内：通用表单写入会绕过库存写入收口（无流水、无库别级成本同步、无审计）。")
            }
        }
    }
    return $problems
}

if ($SelfTest) {
    # ① 代码侧：埋一个直读命中，断言被判 FAIL
    $probe = Join-Path $apiDir 'Data/__inventory_gate_selftest.cs'
    $null = New-Item -ItemType File -Path $probe -Force
    try {
        Set-Content -LiteralPath $probe -Encoding UTF8 -Value 'var sql = "SELECT QTY FROM dbo.INV_PRO_DEPOT";'
        $dirty = Get-InventoryHits -Directory $apiDir
        $found = Test-Gate -Hits $dirty
        if ($found.Count -eq 0) { throw '自检失败：埋入的直读命中没有被门禁发现（门禁形同虚设）。' }
        Write-Host "  [PASS] 代码侧反向自检：埋入的命中被判 FAIL（$($found[0])）"
    }
    finally {
        Remove-Item -LiteralPath $probe -Force -ErrorAction SilentlyContinue
    }
    $clean = Get-InventoryHits -Directory $apiDir
    $cleanProblems = @(Test-Gate -Hits $clean)
    if ($cleanProblems.Count -gt 0) { throw "自检失败：清理探针后仍报命中 ⇒ $($cleanProblems[0])" }
    Write-Host "  [PASS] 代码侧正向自检：当前白名单内命中全部放行（$($clean.Keys.Count) 个文件）"

    # ②-b 零命中类：引擎维护列、盘点单判重键——必须"命中即 FAIL"，绝不允许被白名单收编
    $engineProbe = @(
        'FIELDS:ENGINE_OWNED_EDITABLE:PRODUCT.MRP_QTY',
        'FIELDS:ENGINE_OWNED_EDITABLE:INV_BATCH_M.IN_SUM',
        'TABLES:DF_VERIFY_MISSING_DIMENSION:INV_CHECK_STOCK_D'
    )
    $engineProblems = @(Test-ConfigRefs -Refs $engineProbe -Allowed $allowedConfigRefs)
    if ($engineProblems.Count -ne $engineProbe.Count) {
        throw "自检失败：引擎维护列可编辑未被判 FAIL（实际 $($engineProblems.Count) 条，期望 $($engineProbe.Count) 条）——该类别不得进入白名单。"
    }
    Write-Host '  [PASS] 引擎维护列自检：可编辑一律 FAIL，不被白名单收编'

    # ② 配置侧：合成引用，断言"白名单内放行、白名单外 FAIL"
    $synthetic = @($allowedConfigRefs.Keys) + @('FIELDS:VIRTUAL_EXP:SOME_NEW_TABLE.NEW_COL')
    $configProblems = @(Test-ConfigRefs -Refs $synthetic -Allowed $allowedConfigRefs)
    if ($configProblems.Count -ne 1) {
        throw "自检失败：配置侧合成引用应恰好命中 1 条（白名单外的那条），实际 $($configProblems.Count) 条。"
    }
    if ($configProblems[0] -notmatch 'SOME_NEW_TABLE') {
        throw "自检失败：配置侧命中的不是白名单外那条 ⇒ $($configProblems[0])"
    }
    Write-Host '  [PASS] 配置侧正反自检：白名单内放行、白名单外 FAIL'

    # ③ 写路径：主表是库存表的模块进了统一表单编辑白名单 ⇒ 必须 FAIL
    $dirtyWrite = @(Test-WritableInventoryModules -EnabledModuleIds @(1302, 1303) -InventoryModules @('MODULES:MASTER_TABLE:1303'))
    if ($dirtyWrite.Count -ne 1) {
        throw "自检失败：写路径断言应恰好命中 1 条（余额表模块 1303），实际 $($dirtyWrite.Count) 条。"
    }
    $cleanWrite = @(Test-WritableInventoryModules -EnabledModuleIds @(1302, 110309) -InventoryModules @('MODULES:MASTER_TABLE:1303'))
    if ($cleanWrite.Count -ne 0) {
        throw "自检失败：写路径断言对干净白名单误报 ⇒ $($cleanWrite[0])"
    }
    Write-Host '  [PASS] 写路径正反自检：库存表模块进统一表单白名单被判 FAIL、干净白名单放行'
    Write-Host '-- SELFTEST OK'
    exit 0
}

$problems = New-Object System.Collections.Generic.List[string]
$enabledModules = @()

$hits = Get-InventoryHits -Directory $apiDir
foreach ($problem in (Test-Gate -Hits $hits)) { [void]$problems.Add($problem) }

try {
    $configRefs = Get-ConfigRefs -ConnectionString $ConnectionString
}
catch {
    [void]$problems.Add("配置面检查未能执行（连库失败）：$($_.Exception.Message)")
    $configRefs = $null
}

if ($configRefs) {
    foreach ($problem in (Test-ConfigRefs -Refs $configRefs.Fail -Allowed $allowedConfigRefs)) {
        [void]$problems.Add($problem)
    }

    # ③ 写路径：以库存表为主/明细表的模块不得进入统一表单编辑白名单（配置面 × 代码侧白名单的交叉断言）
    $enabledModules = Get-UnifiedFormWhitelist -ApiDirectory $apiDir
    foreach ($problem in (Test-WritableInventoryModules -EnabledModuleIds $enabledModules -InventoryModules $configRefs.Fail)) {
        [void]$problems.Add($problem)
    }
}

if ($problems.Count -gt 0) {
    Write-Host '== 库存读取收口门禁 =='
    foreach ($line in $problems) { Write-Host "  [FAIL] $line" }
    Write-Host '-- FAIL'
    exit 1
}

# 棘轮：白名单项若已不再命中，说明收口推进，提示可移除（只减不增）。
foreach ($file in ($allowed | Where-Object { -not $hits.ContainsKey($_) })) {
    Write-Host "  [INFO] $file 已不再直接引用库存表：可从白名单移除（棘轮只减不增）。"
}
foreach ($ref in $allowedConfigRefs.Keys) {
    if ($configRefs -and $configRefs.Fail -notcontains $ref) {
        # 只说明"本轮没命中 FAIL 类"，**不等于**该引用已不存在：查询变通、白名单项与
        # 实际配置脱钩都会落到这里。据此删白名单项是错的，故文案只说事实。
        Write-Host "  [INFO] 配置引用 $ref 当前未命中 FAIL 类（不代表该引用已不存在）：确认它确实不再引用库存表后，才可从配置白名单移除（棘轮只减不增）。"
    }
}

Write-Host "PASS 库存读取收口：代码侧命中 $($hits.Keys.Count) 个文件（上限 $($allowed.Count)）；配置侧 FAIL 类引用 $($configRefs.Fail.Count) 条（上限 $($allowedConfigRefs.Count)），全部在白名单内；写路径断言通过（统一表单白名单 $($enabledModules.Count) 个模块中无库存表主表模块）。"
if ($configRefs -and $configRefs.Info.Count -gt 0) {
    Write-Host "  [INFO] 行级/排序类引用 $($configRefs.Info.Count) 条（不构成聚合口径漂移，仅提示）：$($configRefs.Info -join '、')"
}
exit 0
