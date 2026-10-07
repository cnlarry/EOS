<#
.SYNOPSIS
    必填口径巡检：明细主键里「用户能填、库里却非空」的列必须标必填。

.DESCRIPTION
    背景（2026-09-22 真机复现的用户可见 500）：模块 1602 厂商计价表新建时，明细「折扣%」
    （SUPPLIER_PRICE_D.REBATE）留空——界面上的正常填法——保存返回 500，
    日志 `不能将值 NULL 插入列 'REBATE'`。根因是**元数据与物理结构口径不一致**：
    该列是明细主键的一部分、库内 NOT NULL、无默认值，但 FIELDS.IS_VERIFY=0（未标必填），
    于是前端认为可不填、服务端必填校验也不拦，提交空串转成 NULL 后撞库约束。

    第三条断言（模块级表单版式）：模块级版式行把**用户可填的必填列**标成不显示，是一类
    新的用户可见故障——"必填却看不见"，用户填不出来、单据也存不下去。版式表的完整口径
    （含"已定制的表里必填字段整行缺失"、跨度越界、悬空引用）在 `check-form-layout.ps1`；
    这里只守与本脚本同源的那一条：标了必填的列不得被版式隐藏。版式表尚未落库时跳过该条。

    判据（棘轮，必须为 0）：明细表主键列中，满足
      ① 用户可填写：FIELDS.IS_VISIBLE=1 且 ISNULL(IS_READONLY,0)=0 且 ISNULL(IS_VERIFY,0)=0；
      ② 不由服务端承担：既不是该模块主表的主键列（服务端从主表带入），也不是 SERIAL_NO（按行分配）；
    的列一律失败——它们该标必填（迁移 215 已订正首批 11 列）。

    另打印**参考面**：全库「用户可留空 + 库内非空 + 无默认值」的列数。这一类不再判失败，
    因为写入路径已把库层拒绝翻成带字段的 400（`WriteFailureTranslator`），用户拿到的是
    「该字段不能为空」而不是 500；这里只做规模观测。

    -SelfTest 做判别力自检：合成样本里"该抓的"必须命中、"不该抓的"（主表键列 / SERIAL_NO /
    已标必填 / 只读）必须不命中——否则"全绿"没有意义。只跑 SELECT，不改任何数据。

.EXAMPLE
    pwsh scripts/check-required-columns.ps1             # exit 0 = 明细主键必填口径一致
    pwsh scripts/check-required-columns.ps1 -SelfTest    # 额外做正反自检
#>
[CmdletBinding()]
param(
    [string] $ConnectionString,
    [switch] $SelfTest
)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8

# 明细表主键里「用户可填却未标必填」的列（棘轮口径）
$ratchetSql = @'
WITH det AS (
    SELECT DISTINCT m.DETAIL_TABLE AS D_T, m.MASTER_TABLE AS M_T
    FROM dbo.MODULES m
    WHERE m.DETAIL_TABLE IS NOT NULL AND LTRIM(RTRIM(m.DETAIL_TABLE)) <> '' AND m.MASTER_TABLE IS NOT NULL),
mpk AS (
    SELECT DISTINCT d.D_T, c.name AS COL
    FROM det d
    JOIN sys.indexes i ON i.object_id = OBJECT_ID(N'dbo.' + d.M_T) AND i.is_primary_key = 1
    JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
    JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id),
dpk AS (
    SELECT DISTINCT o.name AS D_T, c.name AS COL
    FROM sys.indexes i
    JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
    JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
    JOIN sys.objects o ON o.object_id = i.object_id AND o.name LIKE N'%\_D' ESCAPE N'\'
    WHERE i.is_primary_key = 1 AND c.is_identity = 0 AND c.name <> N'SERIAL_NO')
SELECT N'REQUIRED | ' + LTRIM(RTRIM(p.D_T)) + N'.' + LTRIM(RTRIM(p.COL))
FROM dpk p
JOIN det ON det.D_T = p.D_T
WHERE NOT EXISTS (SELECT 1 FROM mpk WHERE mpk.D_T = p.D_T AND mpk.COL = p.COL)
  AND EXISTS (SELECT 1 FROM dbo.FIELDS f
              WHERE f.T_ID = p.D_T AND f.F_ID = p.COL
                AND f.IS_VISIBLE = 1 AND ISNULL(f.IS_READONLY, 0) = 0 AND ISNULL(f.IS_VERIFY, 0) = 0)
ORDER BY p.D_T, p.COL;
'@

# 参考面：全库「用户可留空 + 库内非空 + 无默认值」的列（写入路径已转为 400，故只观测不判失败）
$surfaceSql = @'
SELECT COUNT(*) AS HITS, COUNT(DISTINCT o.name) AS TABLES_HIT
FROM sys.columns c
JOIN sys.objects o ON o.object_id = c.object_id AND o.type = 'U'
JOIN dbo.FIELDS f ON f.T_ID = o.name AND f.F_ID = c.name
WHERE c.is_nullable = 0 AND c.is_identity = 0 AND c.default_object_id = 0
  AND f.IS_VISIBLE = 1 AND ISNULL(f.IS_READONLY, 0) = 0 AND ISNULL(f.IS_VERIFY, 0) = 0
  AND c.name NOT IN (N'SERIAL_NO', N'CREATE_PERSON', N'CREATE_DATE', N'LAST_UPDATE_BY', N'LAST_UPDATE_DATE',
                     N'CONFIRM_PERSON', N'CONFIRM_DATE', N'FINISHED_PERSON', N'FINISHED_DATE');
'@

# 模块级版式把「用户可填的必填列」标成不显示（版式表未落库时跳过）
$layoutHiddenSql = @"
SELECT N'LAYOUT_HIDDEN | M_IDX=' + CONVERT(nvarchar(12), l.M_IDX) + N' | ' + LTRIM(RTRIM(l.T_ID)) + N'.' + LTRIM(RTRIM(l.F_ID))
FROM dbo.MODULE_FORM_LAYOUT l
JOIN dbo.FIELDS f ON f.T_ID = LTRIM(RTRIM(l.T_ID)) AND LTRIM(RTRIM(f.F_ID)) = LTRIM(RTRIM(l.F_ID))
WHERE l.IS_HIDDEN = 1 AND COALESCE(f.IS_VERIFY, 0) = 1
  AND COALESCE(f.IS_VISIBLE, 1) = 1 AND COALESCE(f.IS_READONLY, 0) = 0 AND COALESCE(f.IS_VIRTUAL, 0) = 0
  AND LTRIM(RTRIM(f.F_ID)) NOT IN (N'SERIAL_NO', N'CREATE_PERSON', N'CREATE_DATE', N'LAST_UPDATE_BY', N'LAST_UPDATE_DATE',
                                   N'CONFIRM_PERSON', N'CONFIRM_DATE', N'FINISHED_PERSON', N'FINISHED_DATE')
ORDER BY l.M_IDX, l.T_ID, l.F_ID;
"@

try {
    . (Join-Path $PSScriptRoot 'dev/eos-sql.ps1')

    $violations = @(Invoke-EosSqlQuery -ConnectionString $ConnectionString -Query $ratchetSql)
    # 该查询函数按「每行一个字符串」返回，单行结果不是数组——必须先包成数组再取首行，
    # 否则 $surface[0] 取到的是字符串的首字符（曾把 548 显示成 5）。
    $surfaceRows = @(Invoke-EosSqlQuery -ConnectionString $ConnectionString -Query $surfaceSql)

    if ($SelfTest) {
        # 判别力自检：合成样本必须"该抓的抓住、不该抓的放过"
        $sampleSql = @'
WITH det AS (SELECT N'SELFTEST_D' AS D_T, N'SELFTEST_M' AS M_T),
mpk AS (SELECT N'SELFTEST_D' AS D_T, N'MASTER_KEY' AS COL),
dpk AS (SELECT * FROM (VALUES
        (N'SELFTEST_D', N'NEED_REQUIRED',  CAST(1 AS bit), CAST(0 AS bit), CAST(0 AS bit), CAST(0 AS bit)),
        (N'SELFTEST_D', N'MASTER_KEY',     CAST(1 AS bit), CAST(0 AS bit), CAST(0 AS bit), CAST(0 AS bit)),
        (N'SELFTEST_D', N'SERIAL_NO',      CAST(1 AS bit), CAST(0 AS bit), CAST(0 AS bit), CAST(0 AS bit)),
        (N'SELFTEST_D', N'ALREADY_REQUIRED', CAST(1 AS bit), CAST(0 AS bit), CAST(1 AS bit), CAST(0 AS bit)),
        (N'SELFTEST_D', N'READONLY_ONE',   CAST(1 AS bit), CAST(1 AS bit), CAST(0 AS bit), CAST(0 AS bit)),
        (N'SELFTEST_D', N'INVISIBLE_ONE',  CAST(0 AS bit), CAST(0 AS bit), CAST(0 AS bit), CAST(0 AS bit)))
        v(D_T, COL, VISIBLE, READONLY_, REQUIRED_, IS_IDENTITY))
SELECT N'REQUIRED | ' + LTRIM(RTRIM(p.D_T)) + N'.' + LTRIM(RTRIM(p.COL))
FROM dpk p
JOIN det ON det.D_T = p.D_T
WHERE NOT EXISTS (SELECT 1 FROM mpk WHERE mpk.D_T = p.D_T AND mpk.COL = p.COL)
  AND p.IS_IDENTITY = 0 AND p.COL <> N'SERIAL_NO'
  AND p.VISIBLE = 1 AND p.READONLY_ = 0 AND p.REQUIRED_ = 0
ORDER BY p.COL;
'@
        $hits = @(Invoke-EosSqlQuery -ConnectionString $ConnectionString -Query $sampleSql)
        Write-Host "== 判别力自检 =="
        if ($hits.Count -ne 1 -or $hits[0] -notmatch 'NEED_REQUIRED') {
            Write-Host "  [FAIL] 合成样本命中面不符预期：$($hits -join ' / ')"
            Write-Host "SELFTEST FAIL 必填口径巡检的判据失去判别力。"
            exit 1
        }
        Write-Host "  [PASS] 只命中 NEED_REQUIRED；主表键列 / SERIAL_NO / 已必填 / 只读 / 不可见 均未误报"
    }

    if ($violations.Count -gt 0) {
        Write-Host "== 明细主键必填口径（应为 0）=="
        $violations | ForEach-Object { Write-Host "  $_" }
        Write-Host "FAIL 有 $($violations.Count) 个明细主键列未标必填：留空即撞库约束（曾表现为 500）。"
        exit 1
    }

    $layoutTable = @(Invoke-EosSqlQuery -ConnectionString $ConnectionString -Query `
        "SELECT CAST(COUNT(*) AS varchar(10)) FROM sys.tables WHERE name = N'MODULE_FORM_LAYOUT';")
    if ("$($layoutTable[0])".Trim() -eq '1') {
        $layoutHidden = @(Invoke-EosSqlQuery -ConnectionString $ConnectionString -Query $layoutHiddenSql |
            Where-Object { $_ -and $_.Trim() -ne '' })
        if ($layoutHidden.Count -gt 0) {
            Write-Host "== 模块级版式隐藏了必填列（应为 0）=="
            $layoutHidden | ForEach-Object { Write-Host "  $_" }
            Write-Host "FAIL 有 $($layoutHidden.Count) 个必填列被版式隐藏：界面填不出来，单据存不下去。"
            exit 1
        }
    }
    else {
        Write-Host "  [SKIP] 版式表未落库，跳过「必填列被版式隐藏」一条（完整口径见 check-form-layout.ps1）"
    }

    $surfaceLine = if ($surfaceRows.Count -gt 0) { [string]$surfaceRows[0] } else { '(未取到)' }
    Write-Host "== 参考面（不判失败）=="
    Write-Host "  用户可留空 + 库内非空 + 无默认值的列：${surfaceLine}（写入路径已把库层拒绝转为带字段的 400）"
    Write-Host "PASS 明细主键必填口径一致（棘轮 0 命中）。"
    exit 0
}
catch {
    Write-Host "ERROR 必填口径巡检执行失败：$($_.Exception.Message)"
    exit 1
}
