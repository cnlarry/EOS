<#
.SYNOPSIS
    库存一致性常态门禁：库别级字段同键一致、策略档位取值合法、哨兵列尾随空格、库别级字段被聚合读取。

.DESCRIPTION
    库存余额表在启用库位 / 批次后，同一个 (料号, 库别) 会有多行。由此产生四类
    只在多行出现时才显现、且编译与单测都发现不了的问题，本脚本把它们变成可复算
    的机器断言：

      ① 库别级字段同键一致——INIT_QTY / COST_PRICE / COST_AMOUNT 是库别级语义，
         每行冗余存储同一值；同键内取值分裂即说明有写入路径没按"全行同步"写。
      ② 策略档位取值合法——BATCH_MODE=3 与 CAPACITY_MODE>0 本版未实现，界面与
         保存接口都拒存；库内若出现这两类取值，只可能来自直连改库或数据导入。
      ③ 哨兵列尾随空格——LOCATION_NO 是变长列，尾随空格会参与 LIKE 与拼接比较，
         造成"看起来相同、比较起来不同"的重复键。
      ④ 库别级字段被聚合读取——这三列每行冗余同一值，任何 SUM 都会按行数成倍
         虚增；库别级数值应取任一行或 MAX，合计只对行级字段（QTY / USEABLE_QTY）。
      ⑥ 半成品账的键与制程——HALF_PRO_DEPOT 按 (料号, 制程, 库别) 分账：制程落空或带尾随空格
         就等于这本账没了分账依据；它与主账同键重叠则说明同一批货被记进了两本账。
      ⑤ 策略求值口径唯一——"库别无覆盖 → 部署级默认"两跳求值只允许出现在
         DepotStockPolicyService 一处；别处自行读表并拼默认值会产生第二个口径。
         **两侧都要扫**：C# 生产代码与**配置**（校验规则 / 业务动作 / 公式行 / 已发布快照）。
         只扫 C# 会漏掉"把这张表声明进条件编译器候选表白名单"这一路——表名只出现在配置
         JSON 里，C# 一个字都看不到，而它产生的正是第二个口径。

    只读：脚本不修改任何数据，也不启停服务。

.PARAMETER ConnectionString
    可选连接串；默认取 MSSQL_ERP_CONN（与仓库其它脚本一致）。

.PARAMETER SelfTest
    正反自检：在真实数据之上注入人工违规行，断言四类检测都能命中。
    用于证明"门禁确实会失败"，而不是永远绿灯。

.EXAMPLE
    pwsh scripts/check-stock-depot-level-consistency.ps1
    pwsh scripts/check-stock-depot-level-consistency.ps1 -SelfTest
#>
[CmdletBinding()]
param(
    [string] $ConnectionString,
    [switch] $SelfTest
)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8

. (Join-Path $PSScriptRoot 'dev/eos-sql.ps1')

$failures = @()
$notes = @()

function Invoke-Probe {
    param([string] $Label, [string] $Query)
    try {
        $rows = @(Invoke-EosSqlQuery -ConnectionString $ConnectionString -Query $Query | Where-Object { $_ -and $_.Trim() -ne '' })
        return $rows
    }
    catch {
        Write-Output "FAIL $Label 探针执行失败：$($_.Exception.Message)"
        exit 2
    }
}

# ---------- ① 库别级字段同键一致 ----------
$depotLevelDrift = Invoke-Probe -Label 'depot-level' -Query @'
SELECT TOP 20 N'(料号, 库别) ' + LTRIM(RTRIM(PRO_NO)) + N'/' + LTRIM(RTRIM(DEPOT_ID))
       + N' 取值分裂：INIT_QTY=' + CONVERT(nvarchar(30), COUNT(DISTINCT ISNULL(INIT_QTY,-1)))
       + N' COST_PRICE=' + CONVERT(nvarchar(30), COUNT(DISTINCT ISNULL(COST_PRICE,-1)))
       + N' COST_AMOUNT=' + CONVERT(nvarchar(30), COUNT(DISTINCT ISNULL(COST_AMOUNT,-1)))
FROM dbo.INV_PRO_DEPOT
GROUP BY PRO_NO, DEPOT_ID
HAVING COUNT(DISTINCT ISNULL(COST_PRICE,-1)) > 1
    OR COUNT(DISTINCT ISNULL(INIT_QTY,-1)) > 1
    OR COUNT(DISTINCT ISNULL(COST_AMOUNT,-1)) > 1;
'@
if ($depotLevelDrift.Count -gt 0) {
    $failures += '① 库别级字段同键内取值分裂（写入未按全行同步）'
    $depotLevelDrift | ForEach-Object { $notes += "   $_" }
}

# ---------- ② 策略档位取值合法 ----------
$badPolicy = Invoke-Probe -Label 'policy-mode' -Query @'
SELECT TOP 20 N'策略行 ' + LTRIM(RTRIM(DEPOT_ID))
       + N' 使用了未实现档位：BATCH_MODE=' + CONVERT(nvarchar(10), BATCH_MODE)
       + N' CAPACITY_MODE=' + CONVERT(nvarchar(10), CAPACITY_MODE)
FROM dbo.DEPOT_STOCK_POLICY
WHERE BATCH_MODE = 3 OR CAPACITY_MODE > 0;
'@
if ($badPolicy.Count -gt 0) {
    $failures += '② 策略表存在未实现档位的取值（只应来自直连改库或数据导入）'
    $badPolicy | ForEach-Object { $notes += "   $_" }
}

# ---------- ③ 哨兵列尾随空格 ----------
# 变长列用 LIKE 判定（= 比较忽略尾随空格，判定无效）；定长 NCHAR 由存储引擎统一
# 填充到定长，空串与纯空格串本就是同一个值，无需也无法用该规则区分。
$trailing = Invoke-Probe -Label 'sentinel-space' -Query @'
SELECT TOP 20 N'INV_PRO_DEPOT (' + LTRIM(RTRIM(PRO_NO)) + N'/' + LTRIM(RTRIM(DEPOT_ID)) + N') LOCATION_NO 带尾随空格'
FROM dbo.INV_PRO_DEPOT WHERE LOCATION_NO LIKE N'% ';
'@
if ($trailing.Count -gt 0) {
    $failures += '③ 位置号存在尾随空格（LIKE / 拼接比较会与去空格值不等）'
    $trailing | ForEach-Object { $notes += "   $_" }
}

# ---------- ④ 库别级字段被聚合读取（静态检索） ----------
# 这三列每行冗余同一库别值，SUM 会按行数成倍虚增；扫描仓库源码中的聚合写法。
$repoRoot = $null
$probe = Get-Item $PSScriptRoot
while ($probe -and -not (Test-Path (Join-Path $probe.FullName 'EOS.slnx'))) { $probe = $probe.Parent }
if ($probe) { $repoRoot = $probe.FullName }

if ($repoRoot) {
    $scanRoots = @('EOS.API', 'scripts', 'EOS.API.Tests') | ForEach-Object { Join-Path $repoRoot $_ } | Where-Object { Test-Path $_ }
    $patterns = @(
        'SUM\s*\(\s*(ISNULL\s*\(\s*)?[A-Za-z_\.\[\]]*\.?(INIT_QTY|COST_PRICE|COST_AMOUNT)',
        'SUM\s*\(\s*\[?(INIT_QTY|COST_PRICE|COST_AMOUNT)\]?\s*\)'
    )
    $aggregations = @()
    foreach ($root in $scanRoots) {
        foreach ($pattern in $patterns) {
            $hits = Get-ChildItem -Path $root -Recurse -File -Include *.cs, *.ps1, *.sql -ErrorAction SilentlyContinue |
                Where-Object { $_.FullName -notmatch '\\obj\\|\\bin\\|\\logs\\' } |
                Select-String -Pattern $pattern -ErrorAction SilentlyContinue
            if ($hits) { $aggregations += $hits }
        }
    }
    if ($aggregations.Count -gt 0) {
        $failures += '④ 存在对库别级字段（INIT_QTY / COST_PRICE / COST_AMOUNT）的聚合读取'
        $aggregations | Select-Object -First 20 | ForEach-Object { $notes += "   $($_.Path):$($_.LineNumber)" }
    }

    # ---------- ⑤ 策略求值口径唯一 ----------
    # 策略的"库别无覆盖 → 部署级默认"两跳求值必须收口在唯一入口。若别处也去读这张表并
    # 自行拼默认值，就会出现多个口径，而分歧只在某个库别真的没有配置行时才显形。
    # 两侧都要扫：生产代码用检索即可，配置侧的表名是**数据**、只有查库才看得见；
    # 只扫 C# 会漏掉"把这张表声明进条件编译器候选表白名单"这一路（P2-00 原稿的写法）。
$configPolicySql = @'
SELECT HIT FROM (
    SELECT N'校验规则 模块' + CONVERT(nvarchar(10), M_IDX) + N' ' + STAGE
         + N' SEQ=' + CONVERT(nvarchar(10), SEQ) AS HIT
    FROM dbo.MODULE_VALIDATION_RULE
    WHERE ISNULL(PARAM_STRUCT, N'') LIKE N'%DEPOT_STOCK_POLICY%'
    UNION ALL
    SELECT N'业务动作 模块' + CONVERT(nvarchar(10), M_IDX) + N' ' + EVENT_CODE
         + N' SEQ=' + CONVERT(nvarchar(10), SEQ)
    FROM dbo.MODULE_BUSINESS_ACTION
    WHERE ISNULL(PARAM_STRUCT, N'') LIKE N'%DEPOT_STOCK_POLICY%'
       OR ISNULL(CONDITION_STRUCT, N'') LIKE N'%DEPOT_STOCK_POLICY%'
    UNION ALL
    SELECT N'动作公式行 模块' + CONVERT(nvarchar(10), a.M_IDX) + N' OPSEQ=' + CONVERT(nvarchar(10), o.OP_SEQ)
    FROM dbo.MODULE_BUSINESS_ACTION_OP o
    JOIN dbo.MODULE_BUSINESS_ACTION a ON a.ACTION_ID = o.ACTION_ID
    WHERE ISNULL(o.SOURCE_TABLE, N'') LIKE N'%DEPOT_STOCK_POLICY%'
       OR ISNULL(o.TARGET_TABLE, N'') LIKE N'%DEPOT_STOCK_POLICY%'
    UNION ALL
    SELECT N'已发布快照 模块' + CONVERT(nvarchar(10), M_IDX) + N' v' + CONVERT(nvarchar(10), VERSION)
    FROM dbo.WORKBENCH_DEFINITION_SNAPSHOT
    WHERE IS_CURRENT = 1 AND DEFINITION_JSON LIKE N'%DEPOT_STOCK_POLICY%'
) x;
'@

    $apiRoot = Join-Path $repoRoot 'EOS.API'
    $policyReaders = @()
    if (Test-Path $apiRoot) {
        $policyReaders = @(
            Get-ChildItem -Path $apiRoot -Recurse -File -Include *.cs -ErrorAction SilentlyContinue |
                Where-Object { $_.FullName -notmatch '\\obj\\|\\bin\\' } |
                Select-String -Pattern 'DEPOT_STOCK_POLICY' -ErrorAction SilentlyContinue |
                Where-Object { $_.Path -notmatch 'DepotStockPolicyService\.cs$' })
    }
    if ($policyReaders.Count -gt 0) {
        $failures += '⑤ 生产代码中存在 DepotStockPolicyService 之外的 DEPOT_STOCK_POLICY 读取点（求值口径必须唯一）'
        $policyReaders | Select-Object -First 20 | ForEach-Object { $notes += "   $($_.Path):$($_.LineNumber)" }
    }

    $configHits = @(Invoke-Probe -Label '⑤配置侧' -Query $configPolicySql)
    if ($configHits.Count -gt 0) {
        $failures += '⑤ 配置侧存在 DEPOT_STOCK_POLICY 引用（策略表不得被声明进校验规则 / 动作 / 快照，求值口径必须唯一）'
        $configHits | Select-Object -First 20 | ForEach-Object { $notes += "   $($_.Trim())" }
    }
}

# ---------- 正反自检：注入人工违规，断言检测确实会命中 ----------
if ($SelfTest) {
    $injected = @(Invoke-Probe -Label 'selftest' -Query @'
WITH FAKE AS (
    SELECT CAST(N'__SELFTEST__' AS nvarchar(30)) AS PRO_NO, CAST(N'ZZ' AS nvarchar(10)) AS DEPOT_ID,
           CAST(1 AS float) AS INIT_QTY, CAST(1 AS float) AS COST_PRICE, CAST(1 AS float) AS COST_AMOUNT
    UNION ALL
    SELECT N'__SELFTEST__', N'ZZ', 2, 1, 1
)
SELECT CONVERT(nvarchar(10), COUNT(*)) FROM (
    SELECT PRO_NO, DEPOT_ID FROM FAKE
    GROUP BY PRO_NO, DEPOT_ID
    HAVING COUNT(DISTINCT ISNULL(COST_PRICE,-1)) > 1
        OR COUNT(DISTINCT ISNULL(INIT_QTY,-1)) > 1
        OR COUNT(DISTINCT ISNULL(COST_AMOUNT,-1)) > 1) x;
'@)
    # 注入的是恰好 1 组分裂数据，命中数必须是 1；同时验证返回的是数字而非被
    # 误当成字符（PowerShell 里 "1"[0] 是字符，[int] 会得到其码位）。
    $hits = if ($injected.Count -eq 1 -and "$($injected[0])".Trim() -eq '1') { 1 } else { 0 }
    if ($hits -lt 1) {
        Write-Output "FAIL -SelfTest：注入的库别级字段分裂未被正确检测（返回值 [$($injected -join ',')]）"
        exit 2
    }
    Write-Output '  PASS 自检：注入 2 行同键分裂数据，检测命中 1 组（判定口径有效）'

    # 配置侧自检：插一条**停用**的合成校验规则（表名写在 PARAM_STRUCT 里），断言门禁
    # **真正那条查询**能命中，再删除。四点讲究：
    #   · ENABLED=0 ⇒ 这条行在任何运行路径上都不生效（规则加载按 ENABLED 过滤），
    #     窗口期内即使有人保存单据也不会受影响；
    #   · RULE_ID 是 IDENTITY 列，**不能显式插值**，所以用 SOURCE_REF 作自检标记来识别与清理；
    #   · PARAM_STRUCT 用 CHAR(34) 拼出真实 JSON —— 整条 SQL 里**不能出现双引号**，
    #     否则经 sqlcmd 传参会把引号吃掉、自检自己以 rc=1 失败；
    #   · 先断言"自检行确实插进去了"再断言命中：语句级 SQL 错误（Msg 544 之类）**不改变
    #     sqlcmd 退出码**，只靠返回值判断会让自检在"其实没插进去"时假装通过。
    $sentinel = '__gate_selftest__'
    try {
        $null = Invoke-Probe -Label 'selftest-config-insert' -Query @"
DECLARE @q NCHAR(1) = CHAR(34);
DELETE FROM dbo.MODULE_VALIDATION_RULE WHERE SOURCE_REF = N'$sentinel';
INSERT INTO dbo.MODULE_VALIDATION_RULE (M_IDX, STAGE, SEQ, VALIDATION_KEY, ENABLED, PARAM_STRUCT, REMARK, SOURCE_REF)
    VALUES (130101, N'SAVE', 99, N'custom-validation', 0,
            N'{' + @q + N'handler' + @q + N':' + @q + N'__selftest__' + @q + N',' + @q + N'check' + @q
              + N':{' + @q + N'table' + @q + N':' + @q + N'DEPOT_STOCK_POLICY' + @q + N'}}',
            N'门禁自检临时行（应已删除）', N'$sentinel');
SELECT CONVERT(nvarchar(10), COUNT(*)) FROM dbo.MODULE_VALIDATION_RULE WHERE SOURCE_REF = N'$sentinel';
"@
        $inserted = @(Invoke-Probe -Label 'selftest-config-inserted' -Query "SELECT CONVERT(nvarchar(10), COUNT(*)) FROM dbo.MODULE_VALIDATION_RULE WHERE SOURCE_REF = N'$sentinel';")
        if ($inserted.Count -lt 1 -or "$($inserted[0])".Trim() -ne '1') {
            Write-Output "FAIL -SelfTest：配置侧自检行未能插入（命中数 [$($inserted -join ',')]）——插入语句级错误不会被 sqlcmd 退出码暴露"
            exit 2
        }
        $configSelf = @(Invoke-Probe -Label 'selftest-config-detect' -Query $configPolicySql)
        if ($configSelf.Count -lt 1) {
            Write-Output "FAIL -SelfTest：配置侧注入的 DEPOT_STOCK_POLICY 引用未被检测（命中数 $($configSelf.Count)）"
            exit 2
        }
        Write-Output '  PASS 自检：配置侧注入 1 条引用策略表的停用规则，门禁查询命中（配置侧检测有效）'
    }
    finally {
        $null = Invoke-Probe -Label 'selftest-config-cleanup' -Query "DELETE FROM dbo.MODULE_VALIDATION_RULE WHERE SOURCE_REF = N'$sentinel'; SELECT N'ok';"
        $left = @(Invoke-Probe -Label 'selftest-config-leftover' -Query "SELECT CONVERT(nvarchar(10), COUNT(*)) FROM dbo.MODULE_VALIDATION_RULE WHERE SOURCE_REF = N'$sentinel';")
        if ($left.Count -ge 1 -and "$($left[0])".Trim() -ne '0') {
            Write-Output "FAIL -SelfTest：自检行未清理干净（残留 [$($left -join ',')]）"
            exit 2
        }
    }
}

# ---------- ⑥ 半成品账的键与制程 ----------
# 半成品账（HALF_PRO_DEPOT）按 (料号, 制程, 库别) 三键分账：**制程是它的身份**——
# 落空或带尾随空格就等于"这本账没了分账依据"，之后按制程汇总会算错、按制程查又查不到。
# 它与主账**不许同键重叠**：重叠说明同一批货被记进了两本账，结存与成本会各算一遍。
$halfKey = Invoke-Probe -Label 'half-stock-key' -Query @'
SELECT TOP 20 N'半成品账坏键：(料号, 制程, 库别) = ' + LTRIM(RTRIM(PRO_NO)) + N' / ' + LTRIM(RTRIM(PROCEDURE_TYPE_ID)) + N' / ' + LTRIM(RTRIM(DEPOT_ID))
  FROM dbo.HALF_PRO_DEPOT
 WHERE LTRIM(RTRIM(PRO_NO)) = N'' OR LTRIM(RTRIM(PROCEDURE_TYPE_ID)) = N'' OR LTRIM(RTRIM(DEPOT_ID)) = N''
    OR PROCEDURE_TYPE_ID <> LTRIM(RTRIM(PROCEDURE_TYPE_ID));
'@
if ($halfKey.Count -gt 0) {
    $failures += '⑥ 半成品账存在不全的键（料号 / 制程 / 库别 落空或带尾随空格）'
    $halfKey | ForEach-Object { $notes += "   $_" }
}

$halfOverlap = Invoke-Probe -Label 'half-stock-overlap' -Query @'
SELECT TOP 20 N'两本账同键：(料号, 库别) = ' + LTRIM(RTRIM(h.PRO_NO)) + N' / ' + LTRIM(RTRIM(h.DEPOT_ID))
  FROM dbo.HALF_PRO_DEPOT h
  JOIN dbo.INV_PRO_DEPOT b
    ON LTRIM(RTRIM(b.PRO_NO)) = LTRIM(RTRIM(h.PRO_NO))
   AND LTRIM(RTRIM(b.DEPOT_ID)) = LTRIM(RTRIM(h.DEPOT_ID));
'@
if ($halfOverlap.Count -gt 0) {
    $failures += '⑥ 半成品账与主账存在同键重叠（同一批货被记进两本账）'
    $halfOverlap | ForEach-Object { $notes += "   $_" }
}

if ($failures.Count -gt 0) {
    Write-Output 'FAIL 库存一致性门禁未通过：'
    $failures | ForEach-Object { Write-Output "  $_" }
    if ($notes.Count -gt 0) {
        Write-Output '  明细（最多 20 条）：'
        $notes | ForEach-Object { Write-Output $_ }
    }
    exit 1
}

Write-Output 'PASS 库存一致性门禁通过（库别级字段同键一致 / 策略档位合法 / 哨兵列无尾随空格 / 无库别级字段聚合读取 / 策略求值口径唯一（含配置侧） / 半成品账三键齐全且与主账不重叠）'
exit 0
