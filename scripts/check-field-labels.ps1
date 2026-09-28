<#
.SYNOPSIS
    字段元数据巡检：FIELDS 的字段中文名不得是占位文本，批核/结案状态位不得进入复合格。

.DESCRIPTION
    两类缺陷都属于「编译、单测、发布门都照不出来，只在界面上难看/看不见」的问题：

    ① **占位字段名**。FIELDS.F_DESC 是表单与列表 label 的唯一来源，读取侧的兜底是
       「空则回落字段代号（F_ID）」。但历史上一次性元数据补齐脚本把"无说明"写成了**字符串**
       `'NULL'`（280 行，2026-08-09，`LAST_UPDATE_BY='EOS-DOC-GEN'`）与 HTML 空实体 `'&nbsp;'`
       （36 行）。这两类值**非空**，兜底拦不住，于是原样显示到标签上——用户在统一表单上看到
       字面量 "NULL"。注意 NULL / 空串是**合法**的（读取侧会回落 F_ID），不算失败。

    ② **状态位进入复合格**。`CONFIRM_TAG`/`FINISHED_TAG` 被配成版式行的 `CELL_ROLE=2`（同格从字段，
       如并入「批核人」格）后，表单只渲染主字段的 label，状态位自己的标签永远不出现——表现就是
       "有批核人、批核日期，却没有批核状态"。元数据本身没错，错在布局把它们配成了附件。
       判据落在**模块级版式表**（`MODULE_FORM_LAYOUT`）：字段级 `FIELDS.FORM_CELL_*` 已退役。

    两者都由迁移订正过（212 / 213），本脚本把口径固化为常态巡检，避免再犯。

    -SelfTest 做判别力自检：把同一条件用在合成样本上，命中面必须与预期一致
    （占位文本必须被抓住，NULL / 空串 / 正常名不得被误报；复合格角色 0/1 不得被误报）。
    只跑 SELECT，不改任何数据。

.EXAMPLE
    pwsh scripts/check-field-labels.ps1            # exit 0 = 无占位字段名、状态位均在复合格外
    pwsh scripts/check-field-labels.ps1 -SelfTest   # 额外做正反自检
#>
[CmdletBinding()]
param(
    [string] $ConnectionString,
    [switch] $SelfTest
)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8

# 占位文本：非空但不是字段名。NULL / 空串是合法值（读取侧回落字段代号），故意不在其列。
$placeholderList = "N'NULL', N'&nbsp;'"

try {
    . (Join-Path $PSScriptRoot 'dev/eos-sql.ps1')

    # ① 占位字段名
    $placeholder = Invoke-EosSqlQuery -ConnectionString $ConnectionString -Query @"
SELECT N'PLACEHOLDER | ' + LTRIM(RTRIM(f.T_ID)) + N'.' + LTRIM(RTRIM(f.F_ID))
       + N' | F_DESC=' + f.F_DESC + N' | 最近修改=' + LTRIM(RTRIM(ISNULL(f.LAST_UPDATE_BY,'')))
FROM dbo.FIELDS f
WHERE f.F_DESC IN ($placeholderList)
ORDER BY f.T_ID, f.F_ID;
"@

    # ② 状态位进入复合格（表单上出不了自己的标签）：字段级 FIELDS.FORM_CELL_* 已退役，
    #    判据只落在模块级版式表——版式行的复合格组同理会让状态位失去标签
    $layoutComposite = @()
    $layoutTable = Invoke-EosSqlQuery -ConnectionString $ConnectionString -Query `
        "SELECT CAST(COUNT(*) AS varchar(10)) FROM sys.tables WHERE name = N'MODULE_FORM_LAYOUT';"
    if ("$layoutTable".Trim() -eq '1') {
        $layoutComposite = Invoke-EosSqlQuery -ConnectionString $ConnectionString -Query @'
SELECT N'LAYOUT_COMPOSITE | M_IDX=' + CONVERT(nvarchar(12), l.M_IDX) + N' | ' + LTRIM(RTRIM(l.T_ID)) + N'.' + LTRIM(RTRIM(l.F_ID))
       + N' | CELL_ROLE=' + CONVERT(nvarchar(12), l.CELL_ROLE)
       + N' | CELL_GROUP=' + LTRIM(RTRIM(ISNULL(l.CELL_GROUP, N'<null>')))
FROM dbo.MODULE_FORM_LAYOUT l
WHERE LTRIM(RTRIM(l.F_ID)) IN (N'CONFIRM_TAG', N'FINISHED_TAG')
  AND (ISNULL(l.CELL_ROLE, 0) <> 0 OR l.CELL_GROUP IS NOT NULL)
ORDER BY l.M_IDX, l.T_ID, l.F_ID;
'@
    }

    if ($SelfTest) {
        # 判别力自检：同一条件用在合成样本上，命中面必须与预期一致，否则"全绿"没有意义。
        $sample = Invoke-EosSqlQuery -ConnectionString $ConnectionString -Query @"
WITH sample AS (
    SELECT * FROM (VALUES
        (N'NULL'), (N'&nbsp;'), (NULL), (N''), (N'料号')
    ) AS t(F_DESC)
)
SELECT N'PLACEHOLDER=' + CONVERT(nvarchar(10), SUM(CASE WHEN F_DESC IN ($placeholderList) THEN 1 ELSE 0 END))
FROM sample;
"@
        $got = ($sample | Where-Object { $_ -match '\S' } | Select-Object -First 1)
        if ($got -ne 'PLACEHOLDER=2') {
            Write-Output "FAIL -SelfTest 判别力不符：期望 PLACEHOLDER=2（占位文本 2 条被抓、NULL/空串/正常名不误报），实得 $got"
            exit 3
        }

        # 复合格口径的判别力自检：同一条件用在合成样本上（role≠0 或配了组即命中）
        $layoutSample = Invoke-EosSqlQuery -ConnectionString $ConnectionString -Query @'
WITH sample AS (
    SELECT * FROM (VALUES
        (N'CONFIRM_TAG',  CAST(0 AS int), CAST(NULL AS nvarchar(50))),
        (N'CONFIRM_TAG',  CAST(2 AS int), N'CONFIRM_PERSON'),
        (N'FINISHED_TAG', CAST(0 AS int), N'FINISHED_GROUP'),
        (N'FINISHED_TAG', CAST(0 AS int), CAST(NULL AS nvarchar(50)))
    ) AS t(F_ID, CELL_ROLE, CELL_GROUP)
)
SELECT N'LAYOUT_COMPOSITE=' + CONVERT(nvarchar(12), SUM(CASE WHEN F_ID IN (N'CONFIRM_TAG', N'FINISHED_TAG')
        AND (ISNULL(CELL_ROLE, 0) <> 0 OR CELL_GROUP IS NOT NULL) THEN 1 ELSE 0 END))
FROM sample;
'@
        $layoutGot = ($layoutSample | Where-Object { $_ -match '\S' } | Select-Object -First 1)
        if ($layoutGot -ne 'LAYOUT_COMPOSITE=2') {
            Write-Output "FAIL -SelfTest 版式表判别力不符：期望 LAYOUT_COMPOSITE=2（配了组或非 0 角色的状态位 2 条被抓，干净的两条不误报），实得 $layoutGot"
            exit 3
        }
        Write-Output "PASS -SelfTest 判别力：$got + $layoutGot（正例被抓住、反例不误报）"
    }
}
catch {
    Write-Output "FAIL field label probe error: $($_.Exception.Message)"
    exit 2
}

$placeholder = @($placeholder | Where-Object { $_ -and $_.Trim() -ne '' })
$layoutComposite = @($layoutComposite | Where-Object { $_ -and $_.Trim() -ne '' })

if ($placeholder.Count -gt 0) {
    Write-Output 'FAIL FIELDS.F_DESC 是占位文本（非空，读取侧的"空则回落字段代号"兜底拦不住，会原样显示到表单/列表标签上）。请填写真实字段名：'
    $placeholder | ForEach-Object { Write-Output "  $_" }
    exit 1
}

if ($layoutComposite.Count -gt 0) {
    Write-Output 'FAIL 批核/结案状态位在模块级版式里被配成复合格成员（表单只渲染主字段标签，状态位自己的标签不会出现；版式行的 CELL_ROLE 应为 0 且 CELL_GROUP 应为空）：'
    $layoutComposite | ForEach-Object { Write-Output "  $_" }
    exit 1
}

Write-Output 'PASS 字段中文名无占位文本（NULL/空串属合法值，读取侧回落字段代号），批核/结案状态位均在复合格之外（模块级版式）。'
exit 0
