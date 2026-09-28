<#
.SYNOPSIS
    库存能力台帐门禁：凡"声明已实现"的策略能力，必须能指出它的运行期消费方。

.DESCRIPTION
    背景：库存策略的档位目录（DepotStockPolicyService.Tiers）里每个档位都带一个
    Implemented 标志，界面据此决定"可选 / 灰显不可选"。这个标志是一句**声称**——
    而声称与实现之间没有任何机制在守，于是会出现下面这类状态：

        · 界面把「存放方式 RANDOM / MIXED」标为已实现、可保存；
        · 而全仓没有任何运行期代码读它，客户选了它行为一点不变；
        · 不报错、不告警，只在客户以为"系统开始帮我找库位了"的时候才暴露。

    本门禁把"声称"与"消费方"对起来，做法与仓库既有门禁同源（确定性事实 + 显式登记）：

      ① 每一个运行期消费点用一个**探针**（正则 + 检索范围 + 排除文件）表达；
      ② 每个能力维度在台帐里必须被**分类**，未分类即 FAIL——新增能力不能悄悄溜过去；
      ③ 分类为 Consumer 的，探针命中数必须达标；
      ④ 分类为 NoConsumerByDesign 的，探针必须为 0（且必须写明理由）；
      ⑤ 分类为 KnownGap 的（= 已登记缺口），探针必须为 0，打印为可见的缺口清单；
         若探针开始命中，说明缺口已补，提示可从台帐移除（棘轮只减不增）。

    **为什么用"探针 + 显式登记"而不是人工核对**：靠人记住的纪律长期必被违反。
    与本脚本配套的台帐文档是 `docs/plans/库存能力台帐.md`（人读的证据与结论），
    本脚本是它的可执行部分（机器守的部分）。

    只读：不修改任何数据，不启停服务，只读源码与配置。

.PARAMETER SelfTest
    正反自检：用合成的档位数据驱动**判定函数本身**，断言五类违规都能被识别、健康数据不误报。
    自检证明的是"判定口径有效"，而不是"当前仓库是干净的"（后者由正常模式回答）。

.EXAMPLE
    pwsh scripts/check-capability-ledger.ps1
    pwsh scripts/check-capability-ledger.ps1 -SelfTest
#>
[CmdletBinding()]
param(
    [string] $ConnectionString,
    [switch] $SelfTest
)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8

# 配置侧探针要连库（复用仓库统一的连接入口，不各自硬编码连接参数）。
. (Join-Path $PSScriptRoot 'dev/eos-sql.ps1')

# 策略实现文件：档位目录（声称）与求值逻辑都在这里，探针必须排除它，
# 否则"声明"会被自己的声明当成"消费"。
$PolicyServiceFile = 'DepotStockPolicyService\.cs$'
$PolicyControllerFile = 'DepotStockPolicyController\.cs$'
$DefaultExclude = "$PolicyServiceFile|$PolicyControllerFile"

# ---------- 纯函数：从 C# 源码解析"声称" ----------
function Get-DeclaredTiers {
    <#
      解析 DepotStockPolicyService.Tiers 的字面量，返回：
        @( @{ Key='storageMode'; Label='存放方式'; Tiers=@( @{Value='FIXED';Text='FIXED 固定';Implemented=$true}, ... ) }, ... )
      纯函数（输入是源码字符串），便于 -SelfTest 用合成源码驱动。
    #>
    param([Parameter(Mandatory = $true)][AllowEmptyString()][string] $Source)

    # 说明文字允许**多行拼接**（`"第一段"` 换行 `+ "第二段"`）：只认单字面量会把这种维度整条漏掉，
    # 而漏掉的维度既不在覆盖性断言的视野里、也不会被"未分类即 FAIL"拦下（它压根不在声明集合里）。
    $dimensionPattern = 'new\(\s*"(?<key>[A-Za-z]+)"\s*,\s*"(?<label>[^"]*)"\s*,\s*(?<desc>"[^"]*"(?:\s*\+\s*"[^"]*")*)\s*,\s*\[(?<body>.*?)\]\s*\)'
    $tierPattern = 'new\(\s*"(?<value>[^"]*)"\s*,\s*"(?<text>[^"]*)"\s*,\s*(?<impl>true|false)\s*\)'

    $result = New-Object System.Collections.Generic.List[object]
    foreach ($dm in [regex]::Matches($Source, $dimensionPattern, 'Singleline')) {
        $tiers = New-Object System.Collections.Generic.List[object]
        foreach ($tm in [regex]::Matches($dm.Groups['body'].Value, $tierPattern)) {
            $tiers.Add(@{
                Value       = $tm.Groups['value'].Value
                Text        = $tm.Groups['text'].Value
                Implemented = ($tm.Groups['impl'].Value -eq 'true')
            })
        }
        $result.Add(@{
            Key   = $dm.Groups['key'].Value
            Label = $dm.Groups['label'].Value
            Tiers = $tiers.ToArray()
        })
    }
    return $result.ToArray()
}

# ---------- 纯函数：判定 ----------
function Test-CapabilityLedger {
    <#
      输入三个纯数据：声称（Tiers）、台帐分类（Expectations）、探针命中数（Hits）。
      输出 @{ Failures; Warnings; Notes }。
      纯函数：不做任何 I/O，便于 -SelfTest 正反自检。
    #>
    param(
        [Parameter(Mandatory = $true)][AllowEmptyCollection()][array] $Declared,
        [Parameter(Mandatory = $true)][hashtable] $Expectations,
        [Parameter(Mandatory = $true)][hashtable] $Hits
    )

    $failures = New-Object System.Collections.Generic.List[string]
    $warnings = New-Object System.Collections.Generic.List[string]

    $seen = @{}
    foreach ($dim in $Declared) {
        $key = $dim.Key
        $seen[$key] = $true

        if (-not $Expectations.ContainsKey($key)) {
            $failures.Add("能力维度「$key」($($dim.Label)) 未在台帐中分类：新增能力必须先在 scripts/check-capability-ledger.ps1 与 docs/plans/库存能力台帐.md 里声明它的运行期消费方，否则无法判断它是否真的生效")
            continue
        }

        $exp = $Expectations[$key]
        $hit = if ($Hits.ContainsKey($key)) { [int] $Hits[$key] } else { -1 }

        if ($hit -lt 0) {
            $failures.Add("能力维度「$key」的探针未能执行（源码目录缺失？）")
            continue
        }

        $claimed = @($dim.Tiers | Where-Object { $_.Implemented })
        $claimedText = if ($claimed.Count -gt 0) { ($claimed | ForEach-Object { $_.Text }) -join ' / ' } else { '（无）' }

        switch ($exp.Kind) {
            'Consumer' {
                if ($hit -lt $exp.Min) {
                    $failures.Add("能力维度「$key」声明为已实现（$claimedText），但运行期消费点探针仅命中 $hit 次（要求 >= $($exp.Min)）——声称与实现不符")
                }
            }
            'NoConsumerByDesign' {
                if ($hit -gt 0) {
                    $failures.Add("能力维度「$key」登记为「本版未实现、无消费方是正确的」，但探针命中 $hit 次：要么实现已补上（请改为 Consumer），要么存在未经声明的读取点")
                }
            }
            'KnownGap' {
                if ([string]::IsNullOrWhiteSpace($exp.Reason)) {
                    $failures.Add("能力维度「$key」被登记为 KnownGap（已登记缺口），但未写明 Reason：缺口必须可追溯到具体原因，不许匿名登记")
                }
                if ($hit -gt 0) {
                    $warnings.Add("棘轮可减：「$key」已出现 $hit 处运行期消费方，缺口可能已补——确认后请把台帐分类改为 Consumer")
                }
                else {
                    $warnings.Add("已知缺口：「$key」声明为已实现（$claimedText），运行期消费方为 0。$($exp.Reason)")
                }
            }
            default {
                $failures.Add("能力维度「$key」的台帐分类 Kind='$($exp.Kind)' 不是 Consumer / NoConsumerByDesign / KnownGap 之一")
            }
        }
    }

    # 反向：台帐里登记了、Tiers 里已经没有的维度 —— 只提示，不失败（棘轮只减不增的提示位）
    foreach ($key in $Expectations.Keys) {
        if (-not $seen.ContainsKey($key)) {
            $warnings.Add("台帐条目「$key」在 Tiers 中已不存在，可从台帐与门禁中移除")
        }
    }

    return @{
        Failures = $failures.ToArray()
        Warnings = $warnings.ToArray()
    }
}

# ---------- S3 台帐：配置面的库存写入旁路（棘轮） ----------
# 问题：效果链写在**配置面**，一个 `MODULE_BUSINESS_ACTION_OP` 就能让某个效果动作直接写库存表，
# 而代码侧一个字都不变。故这一节不看代码，只看**配置里到底有哪些写入点**。
# 口径：TARGET_TABLE 命中余额 / 流水 / 批次账四张表，且效果键不是 inventory-move（后者是正路）。
# 已登记的旁路必须逐条列出；出现未登记的新行即 FAIL（这是棘轮，只减不增）。
$RegisteredInventoryWrites = @{
    'stamp-last-activity|130101|APPROVE_EFFECT|INV_PRO_DEPOT|LAST_CHECK_DATE' = '只写「最近盘点日」标记列；已在 check-inventory-read-hosts 配置侧白名单登记'
}

# ---------- 纯函数：配置写旁路核对 ----------
function Test-ConfigWriteAllowList {
    <#
      纯函数：实际写入点与登记清单双向核对。
      未登记的新写入点 ⇒ FAIL；登记了但已不存在 ⇒ 提示可移除（棘轮只减不增）。
    #>
    param(
        [Parameter(Mandatory = $true)][AllowEmptyCollection()][array] $Actual,
        [Parameter(Mandatory = $true)][hashtable] $Registered
    )

    $failures = New-Object System.Collections.Generic.List[string]
    $warnings = New-Object System.Collections.Generic.List[string]

    $seen = @{}
    foreach ($row in $Actual) {
        $key = "$row".Trim()
        if ($key -eq '') { continue }
        $seen[$key] = $true
        if (-not $Registered.ContainsKey($key)) {
            $failures.Add("配置面出现未登记的库存写入旁路：$key —— 效果动作直接写余额/流水/批次账，绕过了库存移动引擎；请先确认安全性再登记，或改为走 inventory-move")
        }
    }
    foreach ($key in $Registered.Keys) {
        if (-not $seen.ContainsKey($key)) {
            $warnings.Add("登记在册的库存写入旁路已不存在：$key —— 可从本门禁移除（棘轮只减不增）")
        }
    }

    return @{
        Failures = $failures.ToArray()
        Warnings = $warnings.ToArray()
    }
}

# ---------- 纯函数：库对象台帐判定（两侧命中之和） ----------
function Test-ObjectLedger {
    <#
      S2（库存域库对象）的判定。与 S1 的差别只有一处，但很关键：
      消费方可能是**配置面**的（通用表单的目标表、效果链的动作行），
      此时 C# 里一个字都没有。故命中数是「代码侧 + 配置侧」之和，
      只给一侧会让结论反转（实测：LAST_CHECK_DATE 代码侧 0、配置侧 1）。
    #>
    param(
        [Parameter(Mandatory = $true)][hashtable] $Expectations,
        [Parameter(Mandatory = $true)][hashtable] $Hits
    )

    $failures = New-Object System.Collections.Generic.List[string]
    $warnings = New-Object System.Collections.Generic.List[string]

    foreach ($name in ($Expectations.Keys | Sort-Object)) {
        $exp = $Expectations[$name]

        if (-not $Hits.ContainsKey($name)) {
            $failures.Add("库对象「$name」的探针未执行")
            continue
        }
        $h = $Hits[$name]
        if ($h.Code -lt 0 -or $h.Config -lt 0) {
            $failures.Add("库对象「$name」的探针未能执行（代码侧 $(if ($h.Code -lt 0) { '失败' } else { '通过' }) / 配置侧 $(if ($h.Config -lt 0) { '失败' } else { '通过' })）")
            continue
        }

        $total = $h.Code + $h.Config
        $detail = "代码侧 $($h.Code) + 配置侧 $($h.Config)"

        switch ($exp.Kind) {
            'Consumer' {
                if ($total -lt $exp.Min) {
                    $failures.Add("库对象「$name」声明为已生效，但两侧探针合计仅命中 $total 次（$detail，要求 >= $($exp.Min)）——声称与实现不符")
                }
            }
            'KnownGap' {
                if ([string]::IsNullOrWhiteSpace($exp.Reason)) {
                    $failures.Add("库对象「$name」被登记为 KnownGap，但未写明 Reason：缺口必须可追溯，不许匿名登记")
                }
                if ($total -gt 0) {
                    $warnings.Add("棘轮可减：「$name」已出现消费方（$detail），缺口可能已补——确认后请把台帐分类改为 Consumer")
                }
                else {
                    $warnings.Add("已知缺口：「$name」无任何消费方。$($exp.Reason)")
                }
            }
            default {
                $failures.Add("库对象「$name」的台帐分类 Kind='$($exp.Kind)' 不是 Consumer / KnownGap 之一")
            }
        }
    }

    return @{
        Failures = $failures.ToArray()
        Warnings = $warnings.ToArray()
    }
}

# ---------- 探针执行 ----------
function Get-ConsumerHits {
    <#统计某个正则在生产代码（EOS.API）里的命中数；声明文件与控制器一律排除。#>
    param(
        [Parameter(Mandatory = $true)][string] $RepoRoot,
        [Parameter(Mandatory = $true)][string] $Pattern,
        [string] $Exclude = $script:DefaultExclude
    )
    $apiRoot = Join-Path $RepoRoot 'EOS.API'
    if (-not (Test-Path $apiRoot)) { return -1 }
    # 词边界是必须的：不加 `\b` 时 `EFFECT_DATE` 会把 `IN_EFFECT_DATE`（另一个列的报价生效日期）
    # 一并算成命中——实测 0 → 10，足以把"零消费方"的结论整个翻过来。
    $bounded = "\b(?:$Pattern)\b"
    $hits = @(
        Get-ChildItem -Path $apiRoot -Recurse -File -Include *.cs -ErrorAction SilentlyContinue |
            Where-Object { $_.FullName -notmatch '\\obj\\|\\bin\\' -and $_.FullName -notmatch $Exclude } |
            Select-String -Pattern $bounded -ErrorAction SilentlyContinue
    )
    return $hits.Count
}

function Get-ConfigHits {
    <#
      配置侧消费方探针：对象名是否出现在**读写路径**里。
      计入的只有"会真的读写它"的位置：
        · MODULES.MASTER_TABLE / DETAIL_TABLE —— 通用表单的写入目标；
        · MODULE_BUSINESS_ACTION_OP 的 SOURCE/TARGET_TABLE 与 TARGET_FIELD —— 效果链的动作行。
      **不计入**两类：
        · FIELDS 的行登记与已发布快照的字段清单 —— 那是"登记/展示"，不是消费
          （否则 INVOICE_* 这类"有元数据、没入口"的表会被误判成有消费方）；
        · 基于 LIKE 的 PARAM_STRUCT / VIRTUAL_EXP 子串匹配 —— SQL 没有正则，
          `%EFFECT_DATE%` 同样会把 `IN_EFFECT_DATE` 算成命中（实测假阳性）。
          这两处的配置面覆盖交由 `check-inventory-read-hosts.ps1` 承担，本门禁不重复实现。
      连不上库时返回 -1，由判定函数报为失败（不静默放行）。
    #>
    param(
        [Parameter(Mandatory = $true)][string] $ObjectName
    )
    $sql = @"
SELECT CONVERT(nvarchar(10), COUNT(*)) FROM (
    SELECT 1 AS X FROM dbo.MODULES WHERE MASTER_TABLE = N'$ObjectName' OR DETAIL_TABLE = N'$ObjectName'
    UNION ALL
    SELECT 1 FROM dbo.MODULE_BUSINESS_ACTION_OP
        WHERE SOURCE_TABLE = N'$ObjectName' OR TARGET_TABLE = N'$ObjectName' OR TARGET_FIELD = N'$ObjectName'
) z;
"@
    try {
        $rows = @(Invoke-EosSqlQuery -ConnectionString $ConnectionString -Query $sql)
        if ($rows.Count -lt 1) { return -1 }
        return [int] ("$($rows[0])".Trim())
    }
    catch {
        Write-Host "  [WARN] 配置侧探针执行失败（$ObjectName）：$($_.Exception.Message)"
        return -1
    }
}

function Get-ConfigInventoryWrites {
    <#
      列出配置面里所有直接写库存四表的动作行（排除正路 inventory-move）。
      返回形如 'key|moduleId|event|targetTable|targetField' 的字符串数组；连不上库返回 @()。
    #>
    $sql = @'
SELECT CONCAT(a.EFFECT_KEY, N'|', a.M_IDX, N'|', a.EVENT_CODE, N'|',
              ISNULL(o.TARGET_TABLE, N'-'), N'|', ISNULL(o.TARGET_FIELD, N'-')) AS L
FROM dbo.MODULE_BUSINESS_ACTION_OP o
JOIN dbo.MODULE_BUSINESS_ACTION a ON a.ACTION_ID = o.ACTION_ID
WHERE o.TARGET_TABLE IN ('INV_PRO_DEPOT','INV_DEPOT_LOG','INV_BATCH_M','INV_BATCH_D')
  AND a.EFFECT_KEY <> N'inventory-move'
ORDER BY L;
'@
    try {
        return @(Invoke-EosSqlQuery -ConnectionString $ConnectionString -Query $sql | Where-Object { $_ -and $_.Trim() -ne '' })
    }
    catch {
        Write-Host "  [WARN] 配置面库存写入探针执行失败：$($_.Exception.Message)"
        return @('__PROBE_FAILED__')
    }
}

# ---------- 台帐（人工分类；判定函数消费它） ----------
# Kind 的三种取值：
#   Consumer            —— 声明已实现，且运行期确有消费方（探针必须命中）
#   NoConsumerByDesign  —— 本版未实现，"没有消费方"是正确状态（探针必须为 0）
#   KnownGap            —— 已登记的缺口：声明为已实现、实际无消费方（探针必须为 0，且必须写 Reason）
$Expectations = @{
    locationMode         = @{ Kind = 'Consumer'; Pattern = 'LOCATION_MODE|LocationMode'; Min = 1
        Reason = '位置档位在移动引擎里决定"未指定位置是否拒绝"（PolicyTmp.LOCATION_MODE + 档 3 判据）；档 2「建议」另有保存期消费点 DepotLocationSuggestionService（只做档 2：保存期给出建议位置并写入明细，ADR-020 WS-16），档 1 不做' }
    storageMode          = @{ Kind = 'Consumer'; Pattern = 'STORAGE_MODE|StorageMode'; Min = 2
        Reason = '存放方式照原文由 ADR-020 WS-13/14/15 补齐：解析器 DepotLocationResolver（FIXED 认主货位 / RANDOM 与 MIXED 三级退化）、取值侧 DepotLocationService（按 LOCATION_MODE 门与存放方式取候选）、消费点 InventoryMoveHandler.ResolveInboundLocationsAsync（批核入库、归一化之前写回临时表）。Min=2：单处命中可能只是注释（如解析器头注），两处才说明真的接了。原缺口与证据见台帐 §3 问题 1' }
    batchMode            = @{ Kind = 'Consumer'; Pattern = 'BATCH_MODE|BatchMode'; Min = 1
        Reason = '批次档位决定非批管料件批号是否归零（档 0）、是否保留（档 1）、空批号是否拒绝（档 >=2）' }
    capacityMode         = @{ Kind = 'NoConsumerByDesign'; Pattern = 'CAPACITY_MODE|CapacityMode'; Min = 0
        Reason = '容量维度本版未实现：Tiers 标 false、界面灰显不可选、服务端 R-C4 拒存；因此"没有消费方"是正确状态，不是缺口' }
    mixProduct           = @{ Kind = 'Consumer'; Pattern = 'MIX_PRODUCT|MixProduct'; Min = 1
        Reason = '禁止混品号（档 0）时，同一库位放入不同料号被拒（CheckMixingAsync）' }
    mixBatch             = @{ Kind = 'Consumer'; Pattern = 'MIX_BATCH|MixBatch'; Min = 1
        Reason = '禁止混批次（档 0）时，同一库位放入不同批次被拒（CheckMixingAsync）' }
    monthCloseByBatch    = @{ Kind = 'Consumer'; Pattern = 'MONTH_CLOSE_BY_BATCH|MonthCloseByBatch'; Min = 1
        Reason = '月结按批次：消费方是快照生成器 MonthCloseSnapshotService（读部署级那一行的 MONTH_CLOSE_BY_BATCH 决定明细是否按批次细分），ADR-020 WS-8 交付。原缺口见台帐 §3 问题 1 同批登记' }
    monthCloseByLocation = @{ Kind = 'Consumer'; Pattern = 'MONTH_CLOSE_BY_LOCATION|MonthCloseByLocation'; Min = 1
        Reason = '月结按库位：同 monthCloseByBatch，消费方同为快照生成器（按库位细分快照明细）' }
    expiryMode           = @{ Kind = 'Consumer'; Pattern = 'EXPIRY_MODE|ExpiryMode'; Min = 1
        Reason = '过期批次档位：消费方是过账引擎（InventoryMoveHandler 的 PolicyTmp.EXPIRY_MODE → 出库前判已过期批次：档 2 拒绝、档 1 放行并把告警带回、档 0 不查）。声明文件本身被探针排除，故命中即证明运行期真的读了这一维' }
    monthCloseScopeHalfStock = @{ Kind = 'Consumer'; Pattern = 'MONTH_CLOSE_SCOPE_HALF_STOCK|MonthCloseScopeHalfStock'; Min = 1
        Reason = '月结含半成品账：消费方是半成品过账引擎（HalfStockMoveHandler 按部署级策略判这笔变动是否落在已关账期）与快照侧（同源的月结范围判定）' }
    # 新增维度一旦进 Tiers，本表的覆盖性断言会立刻 FAIL 并要求分类——那是要的行为（"参数不得先于实现存在"）。
    # monthCloseScopeHalfStock 曾因「说明文字写成多行拼接」被解析器整条漏读，从而绕过了这条断言；解析器已修并加了自检样本。
}

# ---------- S2 台帐：库存域库对象（两侧探针） ----------
# 与 S1 的差别：S1 的消费方在 C# 里；S2 的消费方**可能在配置面**（通用表单的目标表、效果链的动作行），
# 这些表在 C# 里一个字都不出现。故每项给两个探针，判定用两侧之和。
# 登记口径与证据见 docs/plans/库存能力台帐.md §2 S2 表与 §3 问题 3–8。
$ObjectExpectations = @{
    'INV_DEPOT_LOG.LOCATION_PATH' = @{ Kind = 'Consumer'; CodePattern = 'LOCATION_PATH'; Config = 'LOCATION_PATH'; Min = 1
        Reason = '位置路径快照：移动引擎写入、盘点与查询读取' }
    'INV_PRO_DEPOT.LAST_CHECK_DATE' = @{ Kind = 'Consumer'; CodePattern = 'LAST_CHECK_DATE'; Config = 'LAST_CHECK_DATE'; Min = 1
        Reason = '最近盘点日：由 130101 APPROVE_EFFECT / stamp-last-activity 写入（配置面消费方），查询侧经 InventoryQueryService 读取' }
    # 对照项：刻意保留一个"代码侧 0 / 配置侧 1"的对象，使"两侧之和"这条口径可被门禁自证。
    # 若哪天有人摘掉配置侧探针，本条会立刻变红——而不是等到某天发现一整族库存单据表被误判成空壳。
    'INV_LOAN_M' = @{ Kind = 'Consumer'; CodePattern = 'INV_LOAN_M'; Config = 'INV_LOAN_M'; Min = 1
        Reason = '借出单主表：配置面驱动的对照项（代码侧 0、配置侧 1，模块 130108），用于自证两侧求和口径有效' }
    'INV_PRO_DEPOT.USEABLE_QTY' = @{ Kind = 'Consumer'; CodePattern = 'USEABLE_QTY'; Config = 'USEABLE_QTY'; Min = 1
        Reason = '可用量已启用（ADR-020 §9.7 D7 / WS-19..WS-25 全部落地）：口径唯一出口是 InventoryAvailabilityService（SyncSlotsAsync 是唯一落列点，移动引擎、冻结/解冻、预留/释放、来源结案钩子都走它），消费方三处同源——出库校验按它拦（CheckStockAsync）、读取侧连同数量一起下发（InventoryQueryService 行级与聚合）、门禁逐键断言它（check-inventory-availability.ps1：可用量 = 数量 − Σ有效冻结 − Σ有效预留）。只读位由迁移 241 订正（它是引擎维护列）' }
    'INV_BATCH_M.EFFECT_DATE' = @{ Kind = 'Consumer'; CodePattern = 'EFFECT_DATE'; Config = 'EFFECT_DATE'; Min = 1
        Reason = '有效日期：由批次主档受控写入口写入（master-field-write，ADR-020 §9.2 / WS-17）——它在字段白名单内，且服务端拒写引擎维护列；1302 不在统一表单白名单是**有意的**（写路径断言要求）' }
    'INV_BATCH_M.AGAIN_CHECK_DATE' = @{ Kind = 'Consumer'; CodePattern = 'AGAIN_CHECK_DATE'; Config = 'AGAIN_CHECK_DATE'; Min = 1
        Reason = '复检日期：同 EFFECT_DATE，由受控写入口写入（WS-17）' }
    'DEPOT_PRODUCT_LOCATION' = @{ Kind = 'Consumer'; CodePattern = 'DEPOT_PRODUCT_LOCATION'; Config = 'DEPOT_PRODUCT_LOCATION'; Min = 1
        Reason = '物料默认货位已从"空壳"变为活体：代码侧 DepotLocationService.GetPrimaryLocationsAsync 读主货位（FIXED 存放方式据此落位），配置侧模块 110311 以它为主表（迁移 238/239/240 补元数据、默认字段位与组权限）。原缺口见台帐 §3 问题 8 / 问题 3' }
    # 2026-09-26 C5 批：原来登记在这里的 5 个 KnownGap 已**退役**（用户拍板 + 迁移 255 落库），
    # 条目随之移除（台帐缺口 5 → 0）。移除而不是改类别：对象已不存在，"有没有消费方"这个问题不再成立。
    # 退役依据与三方引用核查见 Migrations/255_retire_orphan_invoice_and_backup_tables.sql 与 logs/c5-retire/；
    # 退役后的对象名与"不得再被引用"的守卫在 scripts/check-retired-db-objects.ps1 里（清单集中一处，
    # 本文件不再出现这些名字——否则会被那道门禁当成"退役对象仍被引用"扫到）。
    # 原文（供追溯，指向台帐文档）：
    #   · 1 个孤立库别留底对象 —— 台帐 §3 问题 4
    #   · 4 个孤立元数据对象（0 模块 / 0 代码 / 0 数据）—— 台帐 §3 问题 7
}

# ---------- 正反自检 ----------
if ($SelfTest) {
    $scriptFailed = 0

    # 自检 0：解析器本身要能读懂真实的档位字面量（防"正则悄悄失效 ⇒ 门禁永远绿灯"）
    $probe = Get-Item $PSScriptRoot
    while ($probe -and -not (Test-Path (Join-Path $probe.FullName 'EOS.slnx'))) { $probe = $probe.Parent }
    if (-not $probe) {
        Write-Output 'FAIL -SelfTest：未能定位仓库根（EOS.slnx）'
        exit 2
    }
    $policyPath = Join-Path $probe.FullName 'EOS.API/Data/DepotStockPolicyService.cs'
    $realTiers = Get-DeclaredTiers -Source (Get-Content -LiteralPath $policyPath -Raw -Encoding UTF8)
    if ($realTiers.Count -ne 10) {
        Write-Output "FAIL -SelfTest：解析器从真实 Tiers 里读到 $($realTiers.Count) 个维度，期望 10 个——解析正则已失效，门禁会假装通过"
        exit 2
    }
    $storage = $realTiers | Where-Object { $_.Key -eq 'storageMode' }
    if (-not $storage -or $storage.Tiers.Count -ne 3 -or -not ($storage.Tiers | Where-Object { $_.Implemented }).Count -eq 3) {
        Write-Output 'FAIL -SelfTest：解析出的 storageMode 档位与预期不符（期望 3 档且全部标为已实现）'
        exit 2
    }
    Write-Output "  PASS 自检：解析器读到 $($realTiers.Count) 个维度，storageMode 3 档均标为已实现（声称来源可靠）"

    # 自检 0b：说明文字写成**多行拼接**时也必须解析到该维度（写法变化不该让维度整条消失）
    $synthetic = @'
new("alphaMode", "甲", "单行说明",
[
    new("1", "是", true),
]),
new("betaMode", "乙",
    "第一段说明"
    + "第二段说明",
[
    new("1", "是", true),
]),
'@
    $syntheticTiers = @(Get-DeclaredTiers -Source $synthetic)
    if ($syntheticTiers.Count -ne 2 -or -not ($syntheticTiers | Where-Object { $_.Key -eq 'betaMode' })) {
        Write-Output "FAIL -SelfTest：解析器漏掉「多行拼接说明」的维度（读到 $($syntheticTiers.Count) 个，期望 2 个且含 betaMode）"
        $scriptFailed++
    }
    else { Write-Output '  PASS 自检：多行拼接说明的维度照常解析（不会因写法变化整条消失）' }

    # 自检 1：未分类的新维度必须 FAIL
    $r = Test-CapabilityLedger -Declared @(@{ Key = 'brandNewThing'; Label = '新维度'; Tiers = @(@{ Value = '0'; Text = '档0'; Implemented = $true }) }) `
        -Expectations $Expectations -Hits @{ brandNewThing = 0 }
    if ($r.Failures.Count -lt 1) { Write-Output 'FAIL -SelfTest：未分类的新维度未被拦下'; $scriptFailed++ }
    else { Write-Output '  PASS 自检：未分类的新能力维度被拦下（新增能力无法悄悄溜过）' }

    # 自检 2：声明为 Consumer 但探针 0 命中必须 FAIL
    $r = Test-CapabilityLedger -Declared @(@{ Key = 'mixBatch'; Label = '混批次'; Tiers = @(@{ Value = '0'; Text = '禁止'; Implemented = $true }) }) `
        -Expectations $Expectations -Hits @{ mixBatch = 0 }
    if ($r.Failures.Count -lt 1) { Write-Output 'FAIL -SelfTest：Consumer 零命中未被拦下'; $scriptFailed++ }
    else { Write-Output '  PASS 自检：声明已实现却无消费方被拦下（本门禁的核心断言）' }

    # 自检 3：声明 Consumer 且命中达标必须通过
    $r = Test-CapabilityLedger -Declared @(@{ Key = 'mixBatch'; Label = '混批次'; Tiers = @(@{ Value = '0'; Text = '禁止'; Implemented = $true }) }) `
        -Expectations $Expectations -Hits @{ mixBatch = 6 }
    if ($r.Failures.Count -ne 0) { Write-Output "FAIL -SelfTest：健康维度被误报为失败（$($r.Failures -join '; ')）"; $scriptFailed++ }
    else { Write-Output '  PASS 自检：有消费方的维度不误报' }

    # 自检 4：NoConsumerByDesign 却出现命中必须 FAIL
    $r = Test-CapabilityLedger -Declared @(@{ Key = 'capacityMode'; Label = '容量档位'; Tiers = @(@{ Value = '0'; Text = '不校验'; Implemented = $true }) }) `
        -Expectations $Expectations -Hits @{ capacityMode = 3 }
    if ($r.Failures.Count -lt 1) { Write-Output 'FAIL -SelfTest：登记为"无消费方"却出现读取点，未被拦下'; $scriptFailed++ }
    else { Write-Output '  PASS 自检：登记为无消费方却出现读取点被拦下' }

    # 自检 5：KnownGap 未写 Reason 必须 FAIL；写了 Reason 只告警
    $r = Test-CapabilityLedger -Declared @(@{ Key = 'gapNoReason'; Label = '匿名缺口'; Tiers = @(@{ Value = '0'; Text = '档0'; Implemented = $true }) }) `
        -Expectations @{ gapNoReason = @{ Kind = 'KnownGap'; Pattern = 'x'; Min = 0; Reason = '' } } -Hits @{ gapNoReason = 0 }
    if ($r.Failures.Count -lt 1) { Write-Output 'FAIL -SelfTest：匿名 KnownGap（无 Reason）未被拦下'; $scriptFailed++ }
    else { Write-Output '  PASS 自检：缺口必须具名（无 Reason 的 KnownGap 被拦下）' }

    $r = Test-CapabilityLedger -Declared @(@{ Key = 'gapOk'; Label = '具名缺口'; Tiers = @(@{ Value = '0'; Text = '档0'; Implemented = $true }) }) `
        -Expectations @{ gapOk = @{ Kind = 'KnownGap'; Pattern = 'x'; Min = 0; Reason = '因为所以' } } -Hits @{ gapOk = 0 }
    if ($r.Failures.Count -ne 0 -or $r.Warnings.Count -lt 1) { Write-Output 'FAIL -SelfTest：具名 KnownGap 应"通过但有告警"，实际不符'; $scriptFailed++ }
    else { Write-Output '  PASS 自检：具名缺口通过并产生可见告警（登记 ≠ 遗忘）' }

    # 自检 6：棘轮可减——KnownGap 一旦出现消费方，必须提示移除
    $r = Test-CapabilityLedger -Declared @(@{ Key = 'gapFixed'; Label = '已补缺口'; Tiers = @(@{ Value = '0'; Text = '档0'; Implemented = $true }) }) `
        -Expectations @{ gapFixed = @{ Kind = 'KnownGap'; Pattern = 'x'; Min = 0; Reason = '因为所以' } } -Hits @{ gapFixed = 4 }
    if ($r.Failures.Count -ne 0 -or -not ($r.Warnings -join ' ') -match '棘轮可减') {
        Write-Output 'FAIL -SelfTest：缺口补上后未给出"棘轮可减"提示'; $scriptFailed++
    }
    else { Write-Output '  PASS 自检：缺口补上后提示可从台帐移除（棘轮只减不增）' }

    # 自检 7（S2 核心）：库对象的消费方可能在配置面 —— 只给代码侧会让结论反转
    $r = Test-ObjectLedger -Expectations @{ 'X.FLD' = @{ Kind = 'Consumer'; CodePattern = 'x'; Config = 'x'; Min = 1; Reason = 'r' } } `
        -Hits @{ 'X.FLD' = @{ Code = 0; Config = 0 } }
    if ($r.Failures.Count -lt 1) { Write-Output 'FAIL -SelfTest：库对象两侧皆 0 命中未被拦下'; $scriptFailed++ }
    else { Write-Output '  PASS 自检：库对象两侧皆无消费方被拦下' }

    $r = Test-ObjectLedger -Expectations @{ 'X.FLD' = @{ Kind = 'Consumer'; CodePattern = 'x'; Config = 'x'; Min = 1; Reason = 'r' } } `
        -Hits @{ 'X.FLD' = @{ Code = 0; Config = 1 } }
    if ($r.Failures.Count -ne 0) { Write-Output 'FAIL -SelfTest：仅有配置侧消费方时被误报为失败（这正是只扫 C# 会犯的错）'; $scriptFailed++ }
    else { Write-Output '  PASS 自检：仅配置侧有消费方时判定为通过（两侧之和口径有效）' }

    $r = Test-ObjectLedger -Expectations @{ 'Y.FLD' = @{ Kind = 'KnownGap'; CodePattern = 'y'; Config = 'y'; Min = 1; Reason = '' } } `
        -Hits @{ 'Y.FLD' = @{ Code = 0; Config = 0 } }
    if ($r.Failures.Count -lt 1) { Write-Output 'FAIL -SelfTest：库对象的匿名 KnownGap 未被拦下'; $scriptFailed++ }
    else { Write-Output '  PASS 自检：库对象缺口必须具名（无 Reason 被拦下）' }

    $r = Test-ObjectLedger -Expectations @{ 'Z.FLD' = @{ Kind = 'KnownGap'; CodePattern = 'z'; Config = 'z'; Min = 1; Reason = '因为所以' } } `
        -Hits @{ 'Z.FLD' = @{ Code = 2; Config = 0 } }
    if ($r.Failures.Count -ne 0 -or -not ($r.Warnings -join ' ') -match '棘轮可减') {
        Write-Output 'FAIL -SelfTest：库对象缺口补上后未给出棘轮提示'; $scriptFailed++
    }
    else { Write-Output '  PASS 自检：库对象缺口补上后提示可从台帐移除' }

    # 自检 8（S3）：配置面库存写入旁路的棘轮
    $r = Test-ConfigWriteAllowList -Actual @('some-new-effect|9999|APPROVE_EFFECT|INV_PRO_DEPOT|QTY') -Registered $RegisteredInventoryWrites
    if ($r.Failures.Count -lt 1) { Write-Output 'FAIL -SelfTest：未登记的新配置写入旁路未被拦下'; $scriptFailed++ }
    else { Write-Output '  PASS 自检：配置面新增的库存写入旁路被拦下（不看代码也拦得住）' }

    $r = Test-ConfigWriteAllowList -Actual @('stamp-last-activity|130101|APPROVE_EFFECT|INV_PRO_DEPOT|LAST_CHECK_DATE') -Registered $RegisteredInventoryWrites
    if ($r.Failures.Count -ne 0) { Write-Output 'FAIL -SelfTest：已登记的写入旁路被误报为失败'; $scriptFailed++ }
    else { Write-Output '  PASS 自检：已登记的写入旁路不误报' }

    $r = Test-ConfigWriteAllowList -Actual @() -Registered $RegisteredInventoryWrites
    if ($r.Failures.Count -ne 0 -or -not ($r.Warnings -join ' ') -match '棘轮只减不增') {
        Write-Output 'FAIL -SelfTest：旁路全部消失后未给出棘轮提示'; $scriptFailed++
    }
    else { Write-Output '  PASS 自检：旁路清空后提示可移除登记（棘轮只减不增）' }

    if ($scriptFailed -gt 0) { exit 2 }
    Write-Output 'PASS 能力台帐门禁自检通过（策略 6 类 + 库对象 4 类 + 配置写旁路 3 类判定，正反均有效）'
    exit 0
}

# ---------- 正常模式 ----------
$probe = Get-Item $PSScriptRoot
while ($probe -and -not (Test-Path (Join-Path $probe.FullName 'EOS.slnx'))) { $probe = $probe.Parent }
if (-not $probe) {
    Write-Output 'FAIL 未能定位仓库根（EOS.slnx）'
    exit 2
}
$repoRoot = $probe.FullName

$policyPath = Join-Path $repoRoot 'EOS.API/Data/DepotStockPolicyService.cs'
if (-not (Test-Path $policyPath)) {
    Write-Output "FAIL 未找到策略档位目录来源：$policyPath"
    exit 2
}

$declared = Get-DeclaredTiers -Source (Get-Content -LiteralPath $policyPath -Raw -Encoding UTF8)
if ($declared.Count -eq 0) {
    Write-Output 'FAIL 未能从 DepotStockPolicyService.cs 解析出任何档位维度——解析正则已失效，门禁会假装通过'
    exit 2
}

$hits = @{}
foreach ($key in $Expectations.Keys) {
    $hits[$key] = Get-ConsumerHits -RepoRoot $repoRoot -Pattern $Expectations[$key].Pattern
}

$result = Test-CapabilityLedger -Declared $declared -Expectations $Expectations -Hits $hits

# S2：库存域库对象（两侧探针）
$objHits = @{}
foreach ($name in $ObjectExpectations.Keys) {
    $objHits[$name] = @{
        Code   = Get-ConsumerHits -RepoRoot $repoRoot -Pattern $ObjectExpectations[$name].CodePattern
        Config = Get-ConfigHits -ObjectName $ObjectExpectations[$name].Config
    }
}
$objResult = Test-ObjectLedger -Expectations $ObjectExpectations -Hits $objHits

# S3：配置面库存写入旁路（棘轮）
$actualWrites = @(Get-ConfigInventoryWrites)
if ($actualWrites.Count -eq 1 -and $actualWrites[0] -eq '__PROBE_FAILED__') {
    $writeResult = @{ Failures = @('配置面库存写入探针未能执行（连库失败）——本门禁不能在看不到配置的情况下宣称通过'); Warnings = @() }
}
else {
    $writeResult = Test-ConfigWriteAllowList -Actual $actualWrites -Registered $RegisteredInventoryWrites
}

$allWarnings = @($result.Warnings) + @($objResult.Warnings) + @($writeResult.Warnings)
$allFailures = @($result.Failures) + @($objResult.Failures) + @($writeResult.Failures)

foreach ($w in $allWarnings) { Write-Output "  [WARN] $w" }

if ($allFailures.Count -gt 0) {
    Write-Output 'FAIL 能力台帐门禁未通过：'
    foreach ($f in $allFailures) { Write-Output "  $f" }
    exit 1
}

$gaps = @($Expectations.Keys | Where-Object { $Expectations[$_].Kind -eq 'KnownGap' })
$objGaps = @($ObjectExpectations.Keys | Where-Object { $ObjectExpectations[$_].Kind -eq 'KnownGap' })
$summary = "PASS 能力台帐门禁通过：策略能力 $($declared.Count) 个维度、库对象 $($ObjectExpectations.Count) 项、配置写旁路 $($actualWrites.Count) 条，全部已分类；声明为已生效者均有消费方（代码侧或配置侧）"
if (($gaps.Count + $objGaps.Count) -gt 0) {
    $summary += "；另有 $($gaps.Count + $objGaps.Count) 个已登记缺口（策略 $($gaps.Count) + 库对象 $($objGaps.Count)），见 docs/plans/库存能力台帐.md"
}
Write-Output $summary
exit 0
