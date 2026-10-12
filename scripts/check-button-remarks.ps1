<#
.SYNOPSIS
    自定义按钮备注巡检：MANUAL 行的 REMARK 必须是人话，不得为空、不得是写给开发者的编号。

.DESCRIPTION
    背景：自定义按钮（模块 230103）的备注是**配置面的一部分**——配按钮的人与实施人员把它当
    "这个按钮在这张单上做什么"读。但批量生成时它曾被写成两种没用的东西：

    ① **开发锚点**。例如 `ADR-020 §9.7 D7（WS-21）：批次主档的受控写入口`——ADR 编号、章节号、
       WS 编号都只在本仓库内可定位，写进给使用者看的文字里就是不认识的东西；
       另一批则只是把模块名与按钮名重复一遍（"用户在制令单上点击：…"），没有增量信息。

    ② **空备注**。备注列的读者就是配置者，空值等于界面上那一格什么都没有。

    这两类缺陷编译、单测、发布门与界面外观检查都照不出来：备注是自由文本，写什么都不报错，
    只是配置者读不懂或看不到——所以口径固化为常态巡检。判据与文案的订正见迁移 351。

    判据（同一份谓词驱动主查询与自检，两处口径不分叉）：
      · 空判据：NULL / 空串 / 纯空白；
      · 锚点判据：正文出现 `ADR-`、`WS-`、章节号 `§`、或迁移编号（`迁移 NNN`）。

    只跑 SELECT，不改任何数据。

    -SelfTest 做判别力自检：把同一条件用在合成样本上，命中面必须与预期一致
    （锚点与空备注必须被抓住，"可用量 / 在库数量 / BOM"这类正常文案不得被误报）。

.EXAMPLE
    pwsh scripts/check-button-remarks.ps1            # exit 0 = 每个自定义按钮的备注都是人话
    pwsh scripts/check-button-remarks.ps1 -SelfTest   # 额外做正反自检
#>
[CmdletBinding()]
param(
    [string] $ConnectionString,
    [switch] $SelfTest
)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8

# 判据模板：{0} 是备注列。主查询传 'a.REMARK'，自检传 'REMARK'。
$emptyTemplate = "{0} IS NULL OR LTRIM(RTRIM({0})) = N''"
$anchorTemplate = "{0} LIKE N'%ADR-%' OR {0} LIKE N'%WS-%' OR {0} LIKE N'%§%' OR {0} LIKE N'%迁移 %'"

try {
    . (Join-Path $PSScriptRoot 'dev/eos-sql.ps1')

    $empty = $emptyTemplate -f 'a.REMARK'
    $anchor = $anchorTemplate -f 'a.REMARK'

    $problems = Invoke-EosSqlQuery -ConnectionString $ConnectionString -Query @"
SELECT N'| ' + CONVERT(nvarchar(12), a.M_IDX) + N' | ' + LTRIM(RTRIM(ISNULL(m.M_DESC, N'?')))
       + N' | ' + LTRIM(RTRIM(a.EFFECT_KEY))
       + N' | ' + CASE WHEN $empty THEN N'空备注' ELSE N'含开发锚点' END
       + N' | ' + ISNULL(a.REMARK, N'<null>')
FROM dbo.MODULE_BUSINESS_ACTION a
LEFT JOIN dbo.MODULES m WITH (NOLOCK) ON m.M_IDX = a.M_IDX
WHERE a.EVENT_CODE = N'MANUAL'
  AND ($empty OR $anchor)
ORDER BY a.M_IDX, a.SEQ;
"@

    if ($SelfTest) {
        # 判别力自检：同一条件用在合成样本上，命中 5 条（锚点 2 + 空/空串/空白 3），两条正常文案不误报。
        $testPredicate = '(' + ($emptyTemplate -f 'REMARK') + ') OR (' + ($anchorTemplate -f 'REMARK') + ')'
        $sample = Invoke-EosSqlQuery -ConnectionString $ConnectionString -Query @"
WITH sample AS (
    SELECT * FROM (VALUES
        (N'ADR-020 §9.7 D7（WS-21）：批次主档的受控写入口'),
        (N'迁移 351：把备注改写为面向使用者的说明'),
        (NULL),
        (N''),
        (N'   '),
        (N'冻结这条库存的可用量：可用量减少、在库数量不变。'),
        (N'按工单 BOM 重算用料：补齐或更新用料明细。')
    ) AS t(REMARK)
)
SELECT N'PROBLEM=' + CONVERT(nvarchar(12), SUM(CASE WHEN $testPredicate THEN 1 ELSE 0 END))
FROM sample;
"@
        $got = ($sample | Where-Object { $_ -match '\S' } | Select-Object -First 1)
        if ($got -ne 'PROBLEM=5') {
            Write-Output "FAIL -SelfTest 判别力不符：期望 PROBLEM=5（锚点 2 条 + 空/空串/空白 3 条被抓，两条正常文案不误报），实得 $got"
            exit 3
        }
        Write-Output "PASS -SelfTest 判别力：$got（正例被抓住、反例不误报）"
    }
}
catch {
    Write-Output "FAIL button remark probe error: $($_.Exception.Message)"
    exit 2
}

$problems = @($problems | Where-Object { $_ -and $_.Trim() -ne '' })

if ($problems.Count -gt 0) {
    Write-Output 'FAIL 自定义按钮的备注不是给人看的说明（空备注 = 界面上那一格什么都没有；开发锚点 = 配置者读不懂）：'
    Write-Output '  模块号 | 模块名 | 处理器 | 问题 | 备注原文'
    $problems | ForEach-Object { Write-Output "  $_" }
    exit 1
}

Write-Output 'PASS 自定义按钮备注：均为非空的人话说明，且不含 ADR / WS / 章节号 / 迁移编号这类内部锚点。'
exit 0
