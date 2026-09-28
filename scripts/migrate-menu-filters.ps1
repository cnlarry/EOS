#Requires -Version 7.0
<#
.SYNOPSIS
存量菜单过滤规则语法归一：把 MODULES.FILTER 中的旧系统写法更新为现代写法，
使前端构建器可识别、后端解析语义不变。

.DESCRIPTION
旧系统 MODULES.FILTER 写法：
- `{表.列}` 花括号包裹（后端解析前本就剥离花括号）；
- `表.列` 限定前缀（主表前缀在后端解析中等价于裸列）；
- `TRUE / FALSE` 布尔字面量（后端解析为 bool，`1/0` 语义等价）。

归一规则（仅作用于单引号字符串字面量之外，避免误改字符串内容）：
1. 剥离 `{` / `}`；
2. 剥离与 MASTER_TABLE 同名（忽略大小写）的 `表.` 前缀；
3. 词边界 TRUE/FALSE → 1/0；
4. 其余（ISNULL、getdate()/convert()、列运算、IN 子查询、跨表引用等）原样保留。

范围说明：仅处理 MODULES.FILTER（菜单管理构建器对应的字段）。
FIELDS.CHOOSE_FILTER1..4 的花括号是运行时占位符（`{m.COL}` 取选择器主值、
`{module}` 取模块号），剥离会破坏语义，不在本脚本范围内。

安全性与幂等性：
- 默认 dry-run 只输出变更计划；`-Apply` 才落库；
- 落库前把全部变更写入 logs\menu-admin\filter-migration-<时间戳>.csv 备份；
- 单事务提交，任一行失败整体回滚；UPDATE 带旧值条件防并发覆盖；
- 归一结果是固定点，重复执行无变更。

.EXAMPLE
.\scripts\migrate-menu-filters.ps1              # dry-run：输出变更计划与统计
.\scripts\migrate-menu-filters.ps1 -Apply       # 应用（先写备份 CSV）
#>
param(
    [switch]$Apply
)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
. (Join-Path $root 'EOS.API.Tests\AcceptanceCommon.ps1')

$logDir = Join-Path $root 'logs\menu-admin'
New-Item -ItemType Directory -Path $logDir -Force | Out-Null

function Convert-LegacyFilter {
    <#
    归一单条过滤表达式：剥离花括号、剥离与 master 同名的表前缀、TRUE/FALSE→1/0。
    所有替换只在单引号字符串字面量之外进行（识别 '' 转义）。
    #>
    param([string]$Filter, [string]$MasterTable = '')
    if ([string]::IsNullOrWhiteSpace($Filter)) { return $Filter }
    $master = $MasterTable.Trim()
    $masterUpper = $master.ToUpperInvariant()
    $sb = [System.Text.StringBuilder]::new()
    $inQuote = $false
    $i = 0
    $n = $Filter.Length
    while ($i -lt $n) {
        $ch = $Filter[$i].ToString()
        if ($inQuote) {
            [void]$sb.Append($ch)
            if ($ch -eq "'") {
                if ($i + 1 -lt $n -and $Filter[$i + 1].ToString() -eq "'") {
                    [void]$sb.Append("'")
                    $i += 2
                    continue
                }
                $inQuote = $false
            }
            $i++
            continue
        }
        if ($ch -eq "'") { [void]$sb.Append($ch); $inQuote = $true; $i++; continue }
        if ($ch -eq '{' -or $ch -eq '}') { $i++; continue }

        $prevIsWord = $i -gt 0 -and ($Filter[$i - 1].ToString() -match '^[A-Za-z0-9_]$')
        # 主表前缀剥离：词边界 + master + '.' + 标识符起始字符
        if (-not $prevIsWord -and $master.Length -gt 0 -and $n - $i -gt $master.Length) {
            $candidate = $Filter.Substring($i, $master.Length).ToUpperInvariant()
            if ($candidate -eq $masterUpper -and $Filter[$i + $master.Length].ToString() -eq '.') {
                $afterDot = if ($i + $master.Length + 1 -lt $n) { $Filter[$i + $master.Length + 1].ToString() } else { '' }
                if ($afterDot -match '^[A-Za-z_]$') {
                    $i += $master.Length + 1
                    continue
                }
            }
        }
        # TRUE/FALSE → 1/0（词边界、大小写不敏感）
        $matchedBool = $false
        if (-not $prevIsWord -and $ch -match '^[A-Za-z]$') {
            foreach ($pair in @([pscustomobject]@{ Token = 'TRUE'; Value = '1' }, [pscustomobject]@{ Token = 'FALSE'; Value = '0' })) {
                if ($n - $i -ge $pair.Token.Length -and $Filter.Substring($i, $pair.Token.Length).ToUpperInvariant() -eq $pair.Token) {
                    $after = if ($i + $pair.Token.Length -lt $n) { $Filter[$i + $pair.Token.Length].ToString() } else { '' }
                    if ($after -notmatch '^[A-Za-z0-9_]$') {
                        [void]$sb.Append($pair.Value)
                        $i += $pair.Token.Length
                        $matchedBool = $true
                        break
                    }
                }
            }
        }
        if ($matchedBool) { continue }
        [void]$sb.Append($ch)
        $i++
    }
    return $sb.ToString().Trim()
}

function Test-BuilderLoadable {
    <#
    对齐前端 parseFilter 的构建器可编辑子集判断：
    每部分为 `字段 运算符 值`（值限数字或单引号字符串，可带 N 前缀），AND/OR 连接。
    返回 $true 表示前端构建器可直接载入编辑。
    #>
    param([string]$Filter)
    $value = $Filter.Trim()
    if ($value -eq '') { return $true }
    $partPattern = @'
^\s*\(?\s*([A-Za-z_][A-Za-z0-9_]*)\s*(=|<>|>=|<=|>|<)\s*(N?'(?:[^']|'')*'|[+-]?\d+(?:\.\d+)?)\s*\)?\s*$
'@
    # 注意：.NET Regex.Split 会把捕获组也作为结果段返回，必须用非捕获组 (?:AND|OR)
    $parts = [regex]::Split($value, '\s+(?:AND|OR)\s+', [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)
    foreach ($part in $parts) {
        if ($part.Trim() -notmatch $partPattern) { return $false }
    }
    return $true
}

function Invoke-OpenConnection {
    $conn = Get-EosConn
    if ($conn.User) {
        $cs = "Server=$($conn.Server);Database=$($conn.Database);User ID=$($conn.User);Password=$($conn.Password);Encrypt=True;TrustServerCertificate=True;Connect Timeout=120"
    } else {
        $cs = "Server=$($conn.Server);Database=$($conn.Database);Integrated Security=True;Encrypt=True;TrustServerCertificate=True;Connect Timeout=120"
    }
    return [System.Data.SqlClient.SqlConnection]::new($cs)
}

$changes = [System.Collections.Generic.List[object]]::new()
$loadableBefore = 0
$loadableAfter = 0

# ── MODULES.FILTER ──────────────────────────────────────────────
$modules = Invoke-EosSqlTable -Query @"
SELECT M_IDX, MASTER_TABLE, FILTER
FROM dbo.MODULES WITH (NOLOCK)
WHERE LTRIM(RTRIM(ISNULL(FILTER,'')))<>''
ORDER BY M_IDX;
"@
foreach ($row in $modules.Rows) {
    $old = ([string]$row.FILTER).Trim()
    $master = ([string]$row.MASTER_TABLE).Trim()
    $new = Convert-LegacyFilter -Filter $old -MasterTable $master
    if ($old -eq $new) {
        if (Test-BuilderLoadable $old) { $loadableBefore++; $loadableAfter++ }
        continue
    }
    $beforeOk = Test-BuilderLoadable $old
    $afterOk = Test-BuilderLoadable $new
    if ($beforeOk) { $loadableBefore++ }
    if ($afterOk) { $loadableAfter++ }
    [void]$changes.Add([pscustomobject]@{
        Source = 'MODULES.FILTER'
        Key = [string]$row.M_IDX
        MasterTable = $master
        OldFilter = $old
        NewFilter = $new
        BeforeLoadable = $beforeOk
        AfterLoadable = $afterOk
    })
}

# ── 报告与落库 ──────────────────────────────────────────────────
Write-Host "==== 菜单过滤规则语法归一计划 ====" -ForegroundColor Cyan
Write-Host "MODULES.FILTER 非空: $($modules.Rows.Count)；变更: $($changes.Count)"
if ($changes.Count -gt 0) {
    $changes | Format-Table -AutoSize Source, Key, MasterTable, BeforeLoadable, AfterLoadable
    $simple = @($changes | Where-Object { $_.Source -eq 'MODULES.FILTER' -and $_.AfterLoadable }).Count
    Write-Host "其中 MODULES.FILTER 归一后可直接被前端构建器载入: $simple 条"
    if (-not $Apply) {
        Write-Host "dry-run：未写入数据库。确认无误后使用 -Apply 落库。" -ForegroundColor Yellow
        exit 0
    }

    $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    $backupPath = Join-Path $logDir "filter-migration-$stamp.csv"
    $changes | Export-Csv -LiteralPath $backupPath -NoTypeInformation -Encoding utf8
    Write-Host "备份已写入: $backupPath" -ForegroundColor Green

    $connection = Invoke-OpenConnection
    try {
        $connection.Open()
        $transaction = $connection.BeginTransaction()
        try {
            foreach ($change in $changes) {
                $command = [System.Data.SqlClient.SqlCommand]::new($null, $connection, $transaction)
                $command.CommandText = 'UPDATE dbo.MODULES SET FILTER=@New WHERE M_IDX=@Key AND LTRIM(RTRIM(ISNULL(FILTER,''''))) = @Old;'
                $command.Parameters.AddWithValue('@Key', [int]$change.Key) | Out-Null
                $command.Parameters.AddWithValue('@Old', $change.OldFilter) | Out-Null
                $command.Parameters.AddWithValue('@New', $change.NewFilter) | Out-Null
                $affected = $command.ExecuteNonQuery()
                if ($affected -ne 1) { throw "行变更数量异常（$affected），中止回滚：$($change.Source) $($change.Key)" }
            }
            $transaction.Commit()
            Write-Host "已提交 $($changes.Count) 条变更。" -ForegroundColor Green
        } catch {
            $transaction.Rollback()
            throw
        }
    } finally {
        $connection.Dispose()
    }
} else {
    Write-Host "无旧写法存量，无需迁移（幂等）。" -ForegroundColor Green
}
