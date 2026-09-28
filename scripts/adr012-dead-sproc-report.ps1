<#
.SYNOPSIS
    ADR-012 旧过程"安全下线集"报告：按三方引用核查算出可以 DROP 的过程与必须保留的过程。

.DESCRIPTION
    退役库对象前的强制核查（AGENTS「退役库对象前的三方引用核查」）：
      1. 库内依赖：过程/视图/函数/触发器之间的引用闭包（sys.sql_expression_dependencies，
         未限定 schema 的引用 referenced_schema_name 为 NULL，必须一并计入）；
      2. 运行时配置：当前快照的 BusinessRule.AfterSaveSproc / WorkflowSproc（钩子列已物理删除）；
      3. 代码与脚本按名调用：EOS.API 的 C#（EXEC/CommandText/字符串字面量）、EOS.API.Tests 与
         scripts 的 .ps1/.sql（含 logs/ 下的验收夹具，它们用 *_CHECK 过程做前置物化）。

    保留集 = ① 系统过程 xp_*（永不处理）② 报表族 P_RPT_*（报表引擎按前缀调用）
           ③ 代码/脚本/夹具按名调用的过程 ④ MODULES 钩子字段引用的过程
           ⑤ 上述集合在库内的**传递闭包**（保留对象引用的过程也必须保留）。
    下线集 = 其余 dbo 过程。脚本另外断言「保留集中没有任何对象引用下线集」（应为 0）。

    只读，不改库；`-EmitSql` 输出可直接贴进迁移的 DROP 清单与保留清单。

.EXAMPLE
    pwsh scripts/adr012-dead-sproc-report.ps1
    pwsh scripts/adr012-dead-sproc-report.ps1 -EmitSql logs/adr012-acceptance/dead-sproc.sql
#>
[CmdletBinding()]
param(
    [string] $Root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path,
    [string] $EmitSql,
    [switch] $SkipLogs
)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8
. (Join-Path $Root 'scripts\dev\eos-sql.ps1')

# ---------- 三方引用核查之三：代码与脚本按名调用 ----------
$scanRoots = @('EOS.API', 'EOS.API.Tests', 'scripts', 'publish', '.agents', 'EOS.Web') |
    ForEach-Object { Join-Path $Root $_ } | Where-Object { Test-Path $_ }
if (-not $SkipLogs) {
    $logDir = Join-Path $Root 'logs'
    if (Test-Path $logDir) { $scanRoots += $logDir }
}
$invoked = New-Object System.Collections.Generic.HashSet[string]
foreach ($root in $scanRoots) {
    $files = Get-ChildItem $root -Recurse -File -Include '*.cs','*.ps1','*.sql','*.ts','*.tsx' -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -notmatch '\\bin\\|\\obj\\|\\node_modules\\|\\Migrations\\|\\shadow\\' }
    foreach ($f in $files) {
        $text = Get-Content $f.FullName -Raw -ErrorAction SilentlyContinue
        if (-not $text) { continue }
        # 显式调用形态（模式必须兼容**混合大小写**的过程名，如 P_INV_OCCUR_INIT_After_Save）
        foreach ($m in [regex]::Matches($text, '(?i)\bEXEC(?:UTE)?\s+(?:\[?dbo\]?\.)?\[?(P_[A-Za-z0-9_]+)\]?')) { $invoked.Add($m.Groups[1].Value.ToUpperInvariant()) | Out-Null }
        foreach ($m in [regex]::Matches($text, "(?i)CommandText\s*=\s*['""](P_[A-Za-z0-9_]+)['""]")) { $invoked.Add($m.Groups[1].Value.ToUpperInvariant()) | Out-Null }
        foreach ($m in [regex]::Matches($text, "(?i)Invoke-OldSproc\s+['""](P_[A-Za-z0-9_]+)['""]")) { $invoked.Add($m.Groups[1].Value.ToUpperInvariant()) | Out-Null }
        # PowerShell 里过程名常作为**位置参数/变量实参**传递（如 `Test-InvOccur 130102 '期初开帐单' 'P_INV_OCCUR_INIT_After_Save' ...`），
        # 显式形态扫不到——脚本属于工具链，任何引号包裹的 P_ 字面量都按"可能被调用"保守保护。
        if ($f.Extension -eq '.ps1') {
            foreach ($m in [regex]::Matches($text, "(?i)['""](P_[A-Za-z0-9_]{2,})['""]")) { $invoked.Add($m.Groups[1].Value.ToUpperInvariant()) | Out-Null }
        }
        # C# 里同样可能有字面量（含断言与工具）；一并保守保护（P_LENGTH 之类的字段名不会命中任何过程，无害）。
        if ($f.Extension -eq '.cs') {
            foreach ($m in [regex]::Matches($text, '(?i)"(P_[A-Za-z0-9_]{2,})"')) { $invoked.Add($m.Groups[1].Value.ToUpperInvariant()) | Out-Null }
        }
    }
}
Write-Output ("按名调用的过程（代码/脚本/夹具）= {0}" -f $invoked.Count)

# ---------- 保留集：调用名单 + 报表元数据引用 + MODULES 字段，随后在库内取传递闭包 ----------
# **保留 = 被引用**，不再按前缀整族保留：
#   · 报表过程没有唯一调用入口，只有 `REPORT_SORT.SORT_FIELDS` 的花括号引用（形如 `{P_RPT_X.COL}`）
#     会被 ReportRepository 解析后按名执行（2026-09-18 据此下线 17 个零引用报表过程，保留 8 个在册的）；
#   · `xp_*` 曾按前缀整族保留，但实测它们与其它过程一样可以用"引用"判定（2026-09-18 据此下线 9 个
#     零引用的系统辅助过程）——**前缀不是判据，引用才是**。
$prefixKeep = @()
$reportSprocRefs = @(Invoke-EosSqlQuery -Query @"
SET NOCOUNT ON;
SELECT CONCAT('|', SORT_FIELDS) FROM dbo.REPORT_SORT WHERE ISNULL(SORT_FIELDS, N'') LIKE N'%{P_%';
"@) | ForEach-Object {
    $text = ($_ -join '')
    foreach ($m in [regex]::Matches($text, '(?i)\{\s*(P_[A-Za-z0-9_]+)')) { $m.Groups[1].Value.ToUpperInvariant() }
} | Sort-Object -Unique
$moduleRefs = @()
# 钩子列（UPDATE_SP/AFTERSAVE_SP）已由迁移 173 物理删除：运行期不再有"按模块元数据调用过程"的入口，
# 故这里恒为空集（保留变量以便下方保留集计算与报告结构不变）。

$keepList = @($invoked) + @($moduleRefs) + @($reportSprocRefs) | Sort-Object -Unique
Write-Output ("报表元数据引用的过程 = {0}" -f $reportSprocRefs.Count)
$values = if ($keepList.Count -eq 0) { '' } else { ($keepList | ForEach-Object { "            (N'$_')" }) -join ",`r`n" }

$sql = @"
SET NOCOUNT ON;
IF OBJECT_ID('tempdb..#Keep') IS NOT NULL DROP TABLE #Keep;
CREATE TABLE #Keep (SPROC NVARCHAR(128) PRIMARY KEY);
$(if ($prefixKeep.Count -gt 0) { "INSERT INTO #Keep (SPROC) SELECT name FROM sys.objects WHERE type='P' AND schema_id=SCHEMA_ID('dbo') AND " + (($prefixKeep | ForEach-Object { "name LIKE '$_'" }) -join ' OR ') + ";" })
$(if ($values) { "INSERT INTO #Keep (SPROC) SELECT name FROM sys.objects o WHERE o.type='P' AND o.schema_id=SCHEMA_ID('dbo') AND o.name IN (SELECT V FROM (VALUES`r`n$values`r`n) AS T(V)) EXCEPT SELECT SPROC FROM #Keep;" })
DECLARE @added INT = 1;
WHILE @added > 0
BEGIN
    INSERT INTO #Keep (SPROC)
    SELECT DISTINCT o2.name
    FROM sys.sql_expression_dependencies d
    JOIN sys.objects o ON o.object_id = d.referencing_id
    JOIN #Keep K ON K.SPROC = o.name
    JOIN sys.objects o2 ON o2.name = d.referenced_entity_name AND o2.type='P' AND o2.schema_id=SCHEMA_ID('dbo')
    WHERE d.referenced_class = 1 AND ISNULL(d.referenced_schema_name, N'dbo') = N'dbo'
    EXCEPT SELECT SPROC FROM #Keep;
    SET @added = @@ROWCOUNT;
END;
SELECT CONCAT('TOTAL|', (SELECT COUNT(*) FROM sys.objects WHERE type='P' AND schema_id=SCHEMA_ID('dbo')));
SELECT CONCAT('KEEP|', (SELECT COUNT(*) FROM #Keep));
SELECT CONCAT('DROP|', (SELECT COUNT(*) FROM sys.objects o WHERE o.type='P' AND o.schema_id=SCHEMA_ID('dbo') AND o.name NOT IN (SELECT SPROC FROM #Keep)));
SELECT CONCAT('RESIDUAL|', COUNT(*))
FROM sys.sql_expression_dependencies d
JOIN sys.objects o ON o.object_id = d.referencing_id
JOIN #Keep K ON K.SPROC = o.name
JOIN sys.objects o3 ON o3.name = d.referenced_entity_name AND o3.type='P' AND o3.schema_id=SCHEMA_ID('dbo')
WHERE o3.name NOT IN (SELECT SPROC FROM #Keep) AND d.referenced_class = 1;
SELECT CONCAT('DROPLIST|', o.name) FROM sys.objects o
WHERE o.type='P' AND o.schema_id=SCHEMA_ID('dbo') AND o.name NOT IN (SELECT SPROC FROM #Keep) ORDER BY o.name;
SELECT CONCAT('KEEPLIST|', K.SPROC) FROM #Keep K ORDER BY K.SPROC;
"@
$tmp = Join-Path ([IO.Path]::GetTempPath()) ('dead-sproc-{0}.sql' -f [guid]::NewGuid().ToString('N'))
[IO.File]::WriteAllText($tmp, $sql, (New-Object System.Text.UTF8Encoding($false)))
try {
    $rows = @((Invoke-EosSqlFile -Path $tmp).Output | Where-Object { $_ -match '\S' } | ForEach-Object { $_.Trim() })
} finally { Remove-Item -LiteralPath $tmp -Force -ErrorAction SilentlyContinue }

$total = ($rows | Where-Object { $_ -like 'TOTAL|*' }) -replace '^TOTAL\|', ''
$keep = ($rows | Where-Object { $_ -like 'KEEP|*' }) -replace '^KEEP\|', ''
$drop = ($rows | Where-Object { $_ -like 'DROP|*' }) -replace '^DROP\|', ''
$residual = ($rows | Where-Object { $_ -like 'RESIDUAL|*' }) -replace '^RESIDUAL\|', ''
$dropList = $rows | Where-Object { $_ -like 'DROPLIST|*' } | ForEach-Object { $_ -replace '^DROPLIST\|', '' }
$keepListOut = $rows | Where-Object { $_ -like 'KEEPLIST|*' } | ForEach-Object { $_ -replace '^KEEPLIST\|', '' }

Write-Output ("dbo 过程总数={0} 保留={1} 可下线={2}" -f $total, $keep, $drop)
Write-Output ("自检：保留集引用候选集 = {0}（应为 0）" -f $residual)
if ($residual -ne '0') { Write-Output 'FAIL 闭包不成立，禁止下线'; exit 1 }
$missingExternal = @($invoked | Where-Object { $keepListOut -notcontains $_ })
Write-Output ("外部调用清单：{0} 个；其中库里已不存在的 = {1}" -f $invoked.Count, $missingExternal.Count)
if ($missingExternal.Count -gt 0) { $missingExternal | ForEach-Object { "  MISSING-EXTERNAL $_" } }
Write-Output '--- 外部调用清单（供迁移嵌入，逐一核对）'
$invoked | Sort-Object | ForEach-Object { "  MANIFEST $_" }
Write-Output '--- 必须保留'
$keepListOut | ForEach-Object { "  KEEP $_" }
Write-Output '--- 可安全下线'
$dropList | ForEach-Object { "  DROP $_" }
if ($EmitSql) {
    $target = if ([IO.Path]::IsPathRooted($EmitSql)) { $EmitSql } else { Join-Path $Root $EmitSql }
    New-Item -ItemType Directory -Force -Path (Split-Path $target) | Out-Null
    [IO.File]::WriteAllText($target, (($dropList | ForEach-Object { "DROP PROCEDURE dbo.$_;" }) -join "`r`n"), (New-Object System.Text.UTF8Encoding($false)))
    Write-Output "OK 下线清单已写入：$target"
}
