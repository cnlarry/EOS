#Requires -Version 7.0
<#
.SYNOPSIS
    连库门禁：标了「数据仅可选入」（FIELDS.ONLY_CHOOSE=1）且可见的字段，必须有一个
    启用中的数据源把**它自己**列进回填目标（RETURN_ITEMS），否则用户选完拿不到值。

.DESCRIPTION
    背景（2026-10-19 实测）：FIELDS.ONLY_CHOOSE=1 让前端锁掉键入，值只能经选择器回填；
    而回填目标写在 FIELD_DATASOURCE.RETURN_ITEMS 里，形如
    `[{"target":"<本字段>","column":"BILL_CODE"}]`。存量里存在「只回填同组伴生列、
    不含本字段」的配置（如 CUS_IMPORT_M.IMPORT_TYPE 只回填 BILL_NAME）：
    选择器打得开、行也点得下去、确认按钮也可用，**选完字段仍是空的**，界面上不报任何错。

    后果分两种：普通字段变成"永远填不上"；若它同时是明细门禁（DETAIL_NO_FIELDS），
    该模块的明细行永远加不出来（300302 / 300305 实测）。

    判据取四个条件同时成立：启用中的数据源、字段 ONLY_CHOOSE=1、字段可见、字段非虚拟。
    - 不做 IS_READONLY：只读列可能由服务端填充，不一定是用户要填的；
    - 不做 IS_VISIBLE=0：界面上没有这一格，不构成录入阻塞（若它出现在明细门禁里，
      由渲染扫描的 DETAIL_GATE_* 判据兜住）；
    - 排除 IS_VIRTUAL=1：虚拟列的值由 `VIRTUAL_EXP` 算出、不从选择器回填，
      给它配数据源本就多余，拿它判"选完拿不到值"是误报（`SYSDG_USER.G_DESC` 即此形）。
    `NULL` / 空 RETURN_ITEMS 一律算缺失——选择器选完什么也回填不了。

    连库运行，不在 CI 的静态检查里（CI 没有数据库）。存量按**具名棘轮**登记：
    清单内的放行并打印为可见告警，清单外的新增直接 FAIL；清单里已不再命中的条目
    会提示删除，棘轮只减不增。

.PARAMETER SelfTest
    判别性自检：用合成样本驱动**同一段判据**（不做第二份实现），
    缺自回填必命中、含自回填必放行。

.EXAMPLE
    pwsh scripts/check-chooser-self-mapping.ps1
    pwsh scripts/check-chooser-self-mapping.ps1 -SelfTest
#>
param(
    [switch] $SelfTest
)

$ErrorActionPreference = 'Stop'

<#
  判据（唯一真源）。输入是一个名为 src 的行集，含 ID / T_ID / F_ID / RETURN_ITEMS /
  ONLY_CHOOSE / IS_VISIBLE。真实库与自检样本都套用这一段，故自检通过即代表真实库判据一致。
#>
$script:Predicate = @'
LTRIM(RTRIM(ISNULL(CAST(src.ONLY_CHOOSE AS varchar(4)), '0'))) = '1'
AND LTRIM(RTRIM(ISNULL(CAST(src.IS_VISIBLE AS varchar(4)), '1'))) = '1'
AND LTRIM(RTRIM(ISNULL(CAST(src.IS_VIRTUAL AS varchar(4)), '0'))) = '0'
AND NOT EXISTS (
        SELECT 1
          FROM OPENJSON(src.RETURN_ITEMS) WITH (t nvarchar(200) '$.target', z nvarchar(200) '$.Target')
         WHERE UPPER(LTRIM(RTRIM(ISNULL(t, z)))) = UPPER(LTRIM(RTRIM(src.F_ID))))
'@

# 存量棘轮：只减不增。条目格式 `<数据源 ID>|<表>|<字段>`。
# 处置方式：给该数据源补 `{"target":"<本字段>","column":"<来源表主键列>"}`（BILLKIND 类来源用 BILL_CODE），
# 再标脏使用它的模块 + 重新发布；确认修好后把对应行从这里删掉。
#
# 2026-10-19 已清零（迁移 427 修 1 条、430 补 10 条并停用 3 条无效配置；虚拟列由判据排除）。
# 此后任何一条命中都是真新增——先确认它是不是"字段只能选入、而来源只回填了伴生列"。
$script:KnownGaps = @()

function Get-OffenderQuery([string] $Source) {
    return @"
SET NOCOUNT ON;
WITH src AS (
$Source
)
SELECT CONCAT(LTRIM(RTRIM(src.ID)), '|', LTRIM(RTRIM(src.T_ID)), '|', LTRIM(RTRIM(src.F_ID)))
  FROM src
 WHERE $($script:Predicate)
 ORDER BY src.ID;
"@
}

try {
    [Console]::OutputEncoding = [Text.Encoding]::UTF8
    . (Join-Path $PSScriptRoot 'dev/eos-sql.ps1')

    if ($SelfTest) {
        # 合成样本：每条只改一个因子，答案写在 Expect 里。
        # JSON 里的双引号用占位符 `~` 写、运行期 NCHAR(34) 替换——查询串要经 sqlcmd -Q 传参，
        # 直接出现双引号会被命令行拆开（与判据无关的传输问题）。
        $lit = {
            param([string] $text)
            "REPLACE(N'$($text -replace '"', '~')', N'~', NCHAR(34))"
        }
        $cases = @(
            @{ Id = 1; Table = 'T'; Field = 'A'; Returns = (& $lit '[{"target":"A","column":"A"}]'); OnlyChoose = 1; Visible = 1; Virtual = 0; Expect = $false }
            @{ Id = 2; Table = 'T'; Field = 'A'; Returns = (& $lit '[{"target":"B","column":"B"}]'); OnlyChoose = 1; Visible = 1; Virtual = 0; Expect = $true }
            @{ Id = 3; Table = 'T'; Field = 'A'; Returns = $null; OnlyChoose = 1; Visible = 1; Virtual = 0; Expect = $true }
            @{ Id = 4; Table = 'T'; Field = 'A'; Returns = (& $lit '[{"target":"B","column":"B"}]'); OnlyChoose = 0; Visible = 1; Virtual = 0; Expect = $false }
            @{ Id = 5; Table = 'T'; Field = 'A'; Returns = (& $lit '[{"target":"B","column":"B"}]'); OnlyChoose = 1; Visible = 0; Virtual = 0; Expect = $false }
            @{ Id = 6; Table = 'T'; Field = 'A'; Returns = (& $lit '[{"Target":"a","Column":"A"}]'); OnlyChoose = 1; Visible = 1; Virtual = 0; Expect = $false }
            @{ Id = 7; Table = 'T'; Field = 'A'; Returns = (& $lit '[{"target":"B","column":"B"}]'); OnlyChoose = 1; Visible = 1; Virtual = 1; Expect = $false }
        )
        $rows = ($cases | ForEach-Object {
            $ret = if ($null -eq $_.Returns) { 'NULL' } else { $_.Returns }
            "({0}, N'{1}', N'{2}', {3}, {4}, {5}, {6})" -f $_.Id, $_.Table, $_.Field, $ret, $_.OnlyChoose, $_.Visible, $_.Virtual
        }) -join ",`n            "
        $source = "    SELECT * FROM (VALUES`n            $rows`n    ) v(ID, T_ID, F_ID, RETURN_ITEMS, ONLY_CHOOSE, IS_VISIBLE, IS_VIRTUAL)"
        $hits = @(Invoke-EosSqlQuery -Query (Get-OffenderQuery $source) | ForEach-Object { $_.Trim() } | Where-Object { $_ -ne '' })
        $expected = @($cases | Where-Object { $_.Expect } | ForEach-Object { "$($_.Id)|$($_.Table)|$($_.Field)" })
        $missing = @($expected | Where-Object { $hits -notcontains $_ })
        $extra = @($hits | Where-Object { $expected -notcontains $_ })
        if ($missing.Count -gt 0 -or $extra.Count -gt 0) {
            Write-Output "FAIL 自检：漏判 [$($missing -join ', ')] 误判 [$($extra -join ', ')]"
            exit 1
        }
        Write-Output "PASS 自检：$($expected.Count) 条应命中、$($cases.Count - $expected.Count) 条应放行，结论全对"
        exit 0
    }

    $source = @'
    SELECT d.ID, d.T_ID, d.F_ID, d.RETURN_ITEMS, f.ONLY_CHOOSE, f.IS_VISIBLE, f.IS_VIRTUAL
      FROM dbo.FIELD_DATASOURCE d
      LEFT JOIN dbo.FIELDS f
        ON LTRIM(RTRIM(f.T_ID)) = LTRIM(RTRIM(d.T_ID))
       AND LTRIM(RTRIM(f.F_ID)) = LTRIM(RTRIM(d.F_ID))
     WHERE COALESCE(d.ACTIVE_TAG, 0) = 1
'@
    $coverageSql = @"
SET NOCOUNT ON;
SELECT CONCAT('enabled=', COUNT(*), '|onlychoose=', SUM(CASE WHEN f.ONLY_CHOOSE = 1 THEN 1 ELSE 0 END))
  FROM dbo.FIELD_DATASOURCE d
  LEFT JOIN dbo.FIELDS f
    ON LTRIM(RTRIM(f.T_ID)) = LTRIM(RTRIM(d.T_ID))
   AND LTRIM(RTRIM(f.F_ID)) = LTRIM(RTRIM(d.F_ID))
 WHERE COALESCE(d.ACTIVE_TAG, 0) = 1;
"@
    $coverage = @(Invoke-EosSqlQuery -Query $coverageSql | ForEach-Object { $_.Trim() } | Where-Object { $_ -ne '' })
    $coverageText = if ($coverage.Count -gt 0) { $coverage[0] } else { '<未取到>' }

    $offenders = @(Invoke-EosSqlQuery -Query (Get-OffenderQuery $source) | ForEach-Object { $_.Trim() } | Where-Object { $_ -ne '' })
    $known = [System.Collections.Generic.HashSet[string]]::new([string[]] $script:KnownGaps)
    $new = @($offenders | Where-Object { -not $known.Contains($_) })
    $resolved = @($script:KnownGaps | Where-Object { $offenders -notcontains $_ })

    Write-Output "覆盖范围：$coverageText（判据：ONLY_CHOOSE=1、可见、非虚拟）；命中 $($offenders.Count) 条，其中具名存量 $($offenders.Count - $new.Count) 条、本次新增 $($new.Count) 条。"

    if ($resolved.Count -gt 0) {
        Write-Output "棘轮可减：$($resolved.Count) 条已不再命中，从脚本的 KnownGaps 删除——"
        $resolved | ForEach-Object { Write-Output "  $_" }
    }

    if ($new.Count -gt 0) {
        Write-Output "FAIL 这些字段标了「仅可选入」，但没有任何启用数据源把本字段列进回填目标（选完拿不到值）："
        $new | ForEach-Object { Write-Output "  $_" }
        Write-Output '  修法：在该字段的数据源 RETURN_ITEMS 里补 {"target":"<本字段>","column":"<来源表主键列>"}，'
        Write-Output '        再标脏使用它的模块并重新发布（选择器来源随定义快照冻结）。'
        exit 1
    }

    if ($offenders.Count -gt 0) {
        Write-Output 'WARN 具名存量尚未处置（棘轮只减不增，清单见脚本内 KnownGaps）：'
        $offenders | ForEach-Object { Write-Output "  $_" }
    }

    Write-Output 'PASS 没有新增「仅可选入但选择器不回填本字段」的配置'
    exit 0
}
catch {
    Write-Error $_.Exception.Message
    exit 1
}
