<#
.SYNOPSIS
    真库用例夹具卫生门禁：写入只能落在用例**自己造**的键上。

.DESCRIPTION
    `EOS.API.Tests` 下的真库用例（`*LiveTests.cs` / `*.ps1`）直连 `EOS.ERP`，不走 API 的权限边界，
    因此"夹具取自哪里"本身就是一道防线。三类写法会让真实数据被动过：

    ① **借真实主档做写入**：运行期 `SELECT TOP 1 ... FROM dbo.DEPOT / DEPOT_LOCATION / INV_PRO_DEPOT /
       PRODUCT / CLIENT / SUPPLIER / ...` 借一个真实键，再把它用于**写入**（SQL 的 WHERE/VALUES 参数，
       或 API 写请求的载荷字段）。危险在于产品侧存在**库别级**动作（如库存策略归位：把该库别全部
       哨兵行的数量改记到目标库位、再重算整库别可用量），借来的真实库别会与"该库别上有哪些料号"
       无关地整体重排。**取样只用于只读断言是允许的**——所以本规则只认"取样值参与写入"。

    ② **真库用例删审计**：`DELETE FROM dbo.AUDIT_EVENT`（含连带删 `AUDIT_FIELD_CHANGE`）。
       语句头可以是 `DELETE [TOP (n)] [别名] FROM` 或 `TRUNCATE TABLE`，目标表可以带方括号、可以省
       `dbo.`、可以把整条语句分片写在相邻的字符串字面量里再拼接——这些形态都算。
       审计是追加型的痕迹，删它等于抹掉"谁在什么时候动过什么"，而排查数据异常时最先要用的就是它。

    ③ **按库别整体删数量承载表**：`DELETE FROM dbo.INV_PRO_DEPOT / INV_DEPOT_LOG / INV_BATCH_* /
       INV_FREEZE / INV_RESERVE WHERE DEPOT_ID=@x`（不带料号限定）。这几张表存的是**数量**，
       按库别删就是"整个仓库清空"。库别是**用例自己建的**（同文件里有 `INSERT INTO dbo.DEPOT`）时不算违规——
       那是清自己的夹具；借来的库别则必须按料号限定。

    三条规则各有一份**具名存量**（棘轮：只减不增）。存量是"当前仓库里已存在、本门禁上线时已知"的落点，
    每条都要写清"为什么可接受"；出现名单外的新落点即 FAIL。

.PARAMETER TestsDir
    扫描目录；默认 `EOS.API.Tests`。

.PARAMETER SelfTest
    正反自检（**不读真实目录、不连库**）：用合成样本分别命中三条规则，并断言干净样本放行、
    只读取样放行、自建库别放行。

.OUTPUTS
    PASS/FAIL + 计数。任一硬指标越界即 exit 1。

.EXAMPLE
    pwsh scripts/check-live-fixture-hygiene.ps1
    pwsh scripts/check-live-fixture-hygiene.ps1 -SelfTest
#>
[CmdletBinding()]
param(
    [string] $TestsDir,
    [switch] $SelfTest
)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8

# 真实主档/真实库存：取样值参与写入即违规（只读取样允许，无需登记）。
$realMasterTables = @(
    'DEPOT', 'DEPOT_LOCATION', 'DEPOT_STOCK_POLICY', 'DEPOT_PRODUCT_LOCATION',
    'PRODUCT', 'CLIENT', 'SUPPLIER', 'HR_EMPLOYEE', 'UNIT', 'CURR', 'FIELDS',
    'INV_PRO_DEPOT', 'INV_DEPOT_LOG', 'INV_BATCH_M', 'INV_BATCH_D', 'INV_FREEZE', 'INV_RESERVE'
)

# 数量承载表：这些表按库别整体删＝销毁数量。
$quantityTables = @('INV_PRO_DEPOT', 'INV_DEPOT_LOG', 'INV_BATCH_M', 'INV_BATCH_D', 'INV_FREEZE', 'INV_RESERVE')

# 赋值目标不是"取样的值"而是承载对象时不参与污染判定（读语句常挂在 command/connection 上）。
$containerNames = @('command', 'cmd', 'connection', 'conn', 'transaction', 'tx', 'reader', 'null', 'var')

# 规则②的语句形态：语句头（DELETE [TOP (n)] [别名] FROM / TRUNCATE TABLE）+ 目标表
# （`dbo.` 可省、可加方括号、可写成 `[dbo].`）。
$auditDeletePattern = '(?i)(?:DELETE\s+(?:TOP\s*\(\s*\d+\s*\)\s+)?(?:\w+\s+)?FROM|TRUNCATE\s+TABLE)\s+(?:\[\s*dbo\s*\]\s*\.\s*|dbo\s*\.\s*)?\[?\s*AUDIT_(?:EVENT|FIELD_CHANGE)'
# 相邻字符串字面量的拼接运算符（`"a" + "b"`）：SQL 分片写在多处时先拼回一条语句再判。
$literalJoinPattern = '"[ \t\r\n]*\+[ \t\r\n]*"'

# --- 存量登记（棘轮：只减不增）--------------------------------------------------
# ① 借真实主档 + 写入：逐条给"为什么可接受"。
$borrowWriteAllow = @{
    'MonthCloseSnapshotHalfStockLiveTests.cs' =
        '借首个真实库别（按 DEPOT_ID 排序），只作为自造料号那行 HALF_PRO_DEPOT 的库别维度；收尾按自造料号与自造月结单别清，不触发任何库别级动作。'
    'EffectValidationPeriodOverlapConfigLiveTests.cs' =
        '取样值只写进事务内的自造人事单（ADR12K/M/N），用例结束无条件 Rollback，零持久影响。'
    'CompareAfterSave.ps1' =
        '对拍脚本：取样值写进自造单别/单号的单据（E2E*），收尾按自造键删；余下未修项（180301 用真实 FIELDS 行、-Live 路径跑旧过程）见 A3 批执行报告的"未修项"。'
}
# ② 仍在删审计的真库用例（棘轮：只减不增）。正确做法是按 MAX(EVENT_ID) 取基线再断言增量
#    （如 AutoApproveEffectLiveTests），不删审计行——存量已清零，出现新落点即 FAIL。
$auditDeleteAllow = @()
# ③ 按库别整体删数量承载表、且无法判定为自建库别。键 = 文件名|表名。
$depotWideDeleteAllow = @()

# --- 分析器 -------------------------------------------------------------------

function Get-WordPattern {
    param([string] $Name)
    # `$? = 可选的 `$` 前缀（脚本变量带、C# 变量不带）
    return "(?<![\w.])`$?_{0,2}$([regex]::Escape($Name))(?![\w])"
}

function Get-LineNumber {
    param([string] $Text, [int] $Index)
    return ($Text.Substring(0, [Math]::Min($Index, $Text.Length)) -split "`n").Count
}

function Get-EnclosingMethodName {
    param([string] $Text, [int] $Index)
    $head = $Text.Substring(0, $Index)
    $matches = [regex]::Matches($head,
        '(?m)^[ \t]*(?:(?:public|private|protected|internal)\s+)?(?:static\s+)?(?:async\s+)?(?:[\w<>\[\],\.\?]+\s+)+(?<name>[A-Za-z_]\w*)\s*\([^;{)]*\)\s*(?:\{|=>)')
    if ($matches.Count -eq 0) { return $null }
    return $matches[$matches.Count - 1].Groups['name'].Value
}

function Get-AuditDeleteIndexes {
    <#
      返回"删审计"语句在 `$Text` 里的起始下标。先按原文匹配；原文未命中时，把相邻的字符串
      字面量拼起来（`"a" + "b"` → `ab`）再匹配一次，覆盖把一条 SQL 分片写在多处的写法。
      拼接会让下标整体位移，故命中时按拼接表还原成原文下标（行号才对得上）。
    #>
    param([string] $Text)

    $indexes = New-Object System.Collections.Generic.List[int]
    foreach ($m in [regex]::Matches($Text, $auditDeletePattern)) { $indexes.Add($m.Index) }
    if ($indexes.Count -gt 0) { return $indexes }

    $joins = [regex]::Matches($Text, $literalJoinPattern)
    if ($joins.Count -eq 0) { return $indexes }

    $joined = [regex]::Replace($Text, $literalJoinPattern, '')
    if (-not [regex]::IsMatch($joined, $auditDeletePattern)) { return $indexes }

    # 只在拼接后才命中时才建映射，避免为每个含拼接的文件做逐字符扫描。
    $builder = New-Object System.Text.StringBuilder
    $map = New-Object System.Collections.Generic.List[int]
    $cursor = 0
    foreach ($join in $joins) {
        for ($i = $cursor; $i -lt $join.Index; $i++) {
            [void]$builder.Append($Text[$i])
            $map.Add($i)
        }
        $cursor = $join.Index + $join.Length
    }
    for ($i = $cursor; $i -lt $Text.Length; $i++) {
        [void]$builder.Append($Text[$i])
        $map.Add($i)
    }
    foreach ($m in [regex]::Matches($builder.ToString(), $auditDeletePattern)) { $indexes.Add($map[$m.Index]) }
    return $indexes
}

function Get-HygieneFindings {
    <#
      返回 @{ Rule; Line; Key; Detail } 数组。三条规则各自独立判定。
    #>
    param([string] $FileName, [string] $Text)

    $findings = New-Object System.Collections.Generic.List[object]
    $isScript = $FileName -like '*.ps1'

    # 文件内的常数字符串：用于还原 `FROM dbo.{StockTable}` 这类插值表名。
    $consts = @{}
    foreach ($m in [regex]::Matches($Text, '(?m)(?:const|readonly|\$)\s*(?:string\s+)?\w*\s*(?<n>[\w:]+)\s*=\s*[''"](?<v>[^''"]*)[''"]')) {
        $consts[$m.Groups['n'].Value.ToUpperInvariant()] = $m.Groups['v'].Value
    }

    # ---- 规则①：借真实主档 + 用于写入 ----
    $taintedVars = New-Object System.Collections.Generic.HashSet[string]
    $taintedMethods = New-Object System.Collections.Generic.HashSet[string]
    $sampleTables = New-Object System.Collections.Generic.HashSet[string]

    foreach ($m in [regex]::Matches($Text, '(?is)TOP\s+1(?<mid>.{0,400}?)\bFROM\s+(?:dbo\.)?\[?(?<tbl>\{\w+\}|\w+)')) {
        $tbl = $m.Groups['tbl'].Value.Trim('[', ']')
        if ($tbl -match '^\{(?<n>\w+)\}$') {
            $key = $Matches['n'].ToUpperInvariant()
            $tbl = if ($consts.ContainsKey($key)) { $consts[$key] } else { '' }
        }
        if (-not $tbl) { continue }
        if ($tbl.ToUpperInvariant() -notin $realMasterTables) { continue }
        [void]$sampleTables.Add($tbl.ToUpperInvariant())

        $index = $m.Index
        $backStart = [Math]::Max(0, $index - 400)
        $back = $Text.Substring($backStart, $index - $backStart)
        $semi = $back.LastIndexOf(';')
        if ($semi -ge 0) { $back = $back.Substring($semi + 1) }

        $assigns = [regex]::Matches($back, '(?:^|\n|[^\w])(?<lhs>[A-Za-z_$][\w\$\.]*)\s*=\s*(?:await\s+)?[^=]*$')
        $assigned = $null
        if ($assigns.Count -gt 0) {
            $assigned = $assigns[$assigns.Count - 1].Groups['lhs'].Value
        }
        if ($assigned) {
            $name = ($assigned -replace '^\$', '') -replace '^.*\.', ''
            if ($name -and ($containerNames -notcontains $name.ToLowerInvariant())) {
                [void]$taintedVars.Add($name)
                continue
            }
        }
        $method = Get-EnclosingMethodName -Text $Text -Index $index
        if ($method) { [void]$taintedMethods.Add($method) }
    }

    # 方法级污染扩散：调用"含取样的方法"的左侧变量一并视为污染（两轮足够穿透一层包装）。
    for ($round = 0; $round -lt 2; $round++) {
        foreach ($method in @($taintedMethods)) {
            $pattern = "(?m)^(?<lhs>[^=;\r\n]{1,140}?)=\s*(?:await\s+)?[\w\.]*\b$([regex]::Escape($method))\s*\("
            foreach ($call in [regex]::Matches($Text, $pattern)) {
                foreach ($id in [regex]::Matches($call.Groups['lhs'].Value, '[A-Za-z_]\w*')) {
                    $name = $id.Value
                    if ($containerNames -notcontains $name.ToLowerInvariant()) { [void]$taintedVars.Add($name) }
                }
            }
        }
    }

    if ($taintedVars.Count -gt 0 -and $sampleTables.Count -gt 0) {
        $writePatterns = @(
            '(?is)INSERT\s+INTO\s+(?:dbo\.)?\w+',
            '(?is)UPDATE\s+(?:dbo\.)?\w+',
            '(?is)DELETE\s+FROM\s+(?:dbo\.)?\w+',
            "(?is)(?:Call-Api|Invoke-EosApi|Invoke-EosSqlNonQuery|Invoke-EosSqlTable|Invoke-Sql|Invoke-E2eSql|Query-Scalar)\s+'?(?:POST|PUT|PATCH|UPDATE|INSERT|DELETE)",
            "(?is)-Method\s+'?(?:Post|Put|Patch)'?"
        )
        $windows = New-Object System.Collections.Generic.List[object]
        foreach ($pattern in $writePatterns) {
            foreach ($w in [regex]::Matches($Text, $pattern)) {
                $length = [Math]::Min(500, $Text.Length - $w.Index)
                $windows.Add([pscustomobject]@{ Start = $w.Index; Text = $Text.Substring($w.Index, $length) })
            }
        }

        $hits = New-Object System.Collections.Generic.List[string]
        $hitLine = 0
        foreach ($window in $windows) {
            foreach ($name in $taintedVars) {
                $binding = "(?i)""@?\w+""\s*,\s*`$?_{0,2}$([regex]::Escape($name))\b"
                $param = "(?i)Parameters\.Add(?:WithValue)?\([^;]{0,80}`$?_{0,2}$([regex]::Escape($name))\b"
                $interpolated = "\{`$?_{0,2}$([regex]::Escape($name))[,\}]"
                $hit = [regex]::IsMatch($window.Text, $binding) -or
                       [regex]::IsMatch($window.Text, $param) -or
                       [regex]::IsMatch($window.Text, $interpolated)
                if (-not $hit -and $isScript) {
                    # 脚本的写入常是拼接字符串（N'$depot'）或 HTTP 载荷（@{ DEPOT_ID = $depot }）。
                    $hit = [regex]::IsMatch($window.Text, (Get-WordPattern -Name $name))
                }
                if ($hit) {
                    if ($hitLine -eq 0) { $hitLine = Get-LineNumber -Text $Text -Index $window.Start }
                    [void]$hits.Add($name)
                }
            }
        }
        if ($hits.Count -gt 0) {
            $findings.Add([pscustomobject]@{
                    Rule   = 'BORROW_WRITE'
                    Line   = $hitLine
                    Key    = $FileName
                    Detail = "取样值（$((@($sampleTables) -join '/'))）经变量 $(@($hits | Select-Object -Unique) -join '、') 进入写入语句"
                })
        }
    }

    # ---- 规则②：真库用例删审计 ----
    foreach ($index in @(Get-AuditDeleteIndexes -Text $Text)) {
        $findings.Add([pscustomobject]@{
                Rule   = 'AUDIT_DELETE'
                Line   = (Get-LineNumber -Text $Text -Index $index)
                Key    = $FileName
                Detail = '真库用例删除审计行（审计按设计保留：它是追加型痕迹）'
            })
    }

    # ---- 规则③：按库别整体删数量承载表 ----
    $depotInsertTokens = New-Object System.Collections.Generic.HashSet[string]
    foreach ($m in [regex]::Matches($Text, '(?i)INSERT\s+INTO\s+(?:dbo\.)?DEPOT\b')) {
        $length = [Math]::Min(600, $Text.Length - $m.Index)
        foreach ($token in [regex]::Matches($Text.Substring($m.Index, $length), "@\w+|\b[A-Za-z_]\w*\b")) {
            [void]$depotInsertTokens.Add($token.Value.ToUpperInvariant())
        }
    }

    foreach ($m in [regex]::Matches($Text, '(?is)DELETE\s+FROM\s+(?:dbo\.)?(?<tbl>\w+)(?<body>[^;]{0,300})')) {
        $tbl = $m.Groups['tbl'].Value.ToUpperInvariant()
        if ($tbl -notin $quantityTables) { continue }
        $body = $m.Groups['body'].Value
        if ($body -notmatch '(?i)\bDEPOT_ID\b') { continue }
        if ($body -match '(?i)\bPRO_NO\b') { continue }   # 已按料号限定＝只碰自己的行

        $selfMade = $false
        foreach ($token in [regex]::Matches($body, "@\w+|\b[A-Za-z_]\w*\b")) {
            if ($depotInsertTokens.Contains($token.Value.ToUpperInvariant())) { $selfMade = $true; break }
        }
        if ($selfMade) { continue }

        $findings.Add([pscustomobject]@{
                Rule   = 'DEPOT_WIDE_DELETE'
                Line   = (Get-LineNumber -Text $Text -Index $m.Index)
                Key    = "$FileName|$tbl"
                Detail = "按库别整体删数量承载表 $tbl，且同文件未见自建该库别（未按料号限定）"
            })
    }

    return $findings
}

# --- 自检 ---------------------------------------------------------------------

$selfTestPositive = @(
    @'
private static async Task<string> PickDepotAsync()
{
    await using var connection = await OpenAsync();
    var depot = await ScalarAsync<string>(connection,
        "SELECT TOP 1 LTRIM(RTRIM(DEPOT_ID)) FROM dbo.DEPOT_LOCATION ORDER BY DEPOT_ID;");
    return depot ?? throw new InvalidOperationException("库位主档里没有库别。");
}
public async Task InitializeAsync()
{
    _depot = await PickDepotAsync();
    await ExecAsync(connection, "UPDATE dbo.INV_PRO_DEPOT SET QTY = 0 WHERE DEPOT_ID=@depot;", ("@depot", _depot));
}
'@,
    @'
var depot = await ScalarAsync<string>(connection, "SELECT TOP 1 LTRIM(RTRIM(DEPOT_ID)) FROM dbo.DEPOT ORDER BY DEPOT_ID;");
await ExecAsync(connection, "INSERT INTO dbo.DEPOT_STOCK_POLICY (DEPOT_ID) VALUES (@d);", ("@d", depot));
'@,
    @'
$depot = [string](Query-Scalar "SELECT TOP 1 LTRIM(RTRIM(DEPOT_ID)) FROM DEPOT WHERE LTRIM(RTRIM(DEPOT_ID))<>''")
Invoke-Sql "UPDATE dbo.DEPOT_STOCK_POLICY SET LOCATION_MODE=0 WHERE DEPOT_ID=N'$depot';"
'@,
    @'
await ExecAsync(connection, "DELETE FROM dbo.AUDIT_EVENT WHERE RESOURCE_KEY=@key;", ("@key", key));
'@,
    @'
await ExecAsync(connection, "DELETE FROM dbo.AUDIT_FIELD_CHANGE WHERE EVENT_ID IN (SELECT EVENT_ID FROM dbo.AUDIT_EVENT WHERE ACTION=@a);");
'@,
    @'
await ExecAsync(connection, "DELETE c FROM dbo.AUDIT_FIELD_CHANGE c JOIN dbo.AUDIT_EVENT e ON e.EVENT_ID = c.EVENT_ID WHERE e.M_IDX = @m;");
'@,
    @'
await ExecAsync(connection, "TRUNCATE TABLE dbo.AUDIT_EVENT;");
'@,
    @'
await ExecAsync(connection, "DELETE TOP (100) FROM dbo.AUDIT_EVENT WHERE M_IDX = @m;");
'@,
    @'
await ExecAsync(connection, "DELETE FROM [dbo].[AUDIT_EVENT] WHERE ACTION = @action;");
'@,
    @'
var sql = "DELETE FROM dbo." + "AUDIT_EVENT WHERE ACTION=@action;"
await ExecAsync(connection, sql, ("@action", action));
'@,
    @'
await ExecAsync(connection, "DELETE FROM dbo.INV_PRO_DEPOT WHERE DEPOT_ID=@d;", ("@d", TestDepot));
'@,
    @'
DELETE FROM dbo.INV_DEPOT_LOG WHERE DEPOT_ID = @Depot;
'@
)

$selfTestNegative = @(
    @'
var depot = await ScalarAsync<string>(connection, "SELECT TOP 1 LTRIM(RTRIM(DEPOT_ID)) FROM dbo.DEPOT ORDER BY DEPOT_ID;");
Assert.Equal(depot, await ScalarAsync<string>(connection, "SELECT TOP 1 DEPOT_ID FROM dbo.DEPOT_STOCK_POLICY;"));
'@,
    @'
var row = await AnyStockRowAsync();
Assert.Equal(3d, row.Qty, 6);
await ExecAsync(connection, "INSERT INTO dbo.S_CHECK_STOCK_D (PRO_NO) VALUES (@pro);", ("@pro", TestProduct));
'@,
    @'
await ExecAsync(connection, """
    INSERT INTO dbo.DEPOT (DEPOT_ID, DEPOT_NAME) VALUES (@d, N'ZZ 测试仓');
    """, ("@d", Depot));
await ExecAsync(connection, "DELETE FROM dbo.INV_PRO_DEPOT WHERE DEPOT_ID=@d;", ("@d", Depot));
'@,
    @'
await ExecAsync(connection, "DELETE FROM dbo.INV_PRO_DEPOT WHERE PRO_NO IN (@pa, @pb) AND DEPOT_ID=@d;", ("@d", Depot));
'@,
    @'
INSERT INTO dbo.INV_PRO_DEPOT (PRO_NO, DEPOT_ID, LOCATION_NO, BATCH_NO, QTY) VALUES (@pro, @depot, N'-', N'', 100);
'@,
    @'
await ExecAsync(connection, "DELETE FROM dbo.WORKBENCH_IDEMPOTENCY WHERE M_IDX=@module AND ACTION=N'ACTION';", ("@module", ModuleId));
'@,
    @'
var total = await ScalarAsync<int>(connection, "SELECT COUNT(*) FROM dbo.AUDIT_EVENT WHERE M_IDX=@module;");
'@
)

# 规则②的具名正反：断言"命中的就是删审计这条规则"，免得样本被别的规则顺带命中而虚过。
$selfTestAuditCases = @(
    [pscustomobject]@{ Expect = $true; Text = @'
await ExecAsync(connection, "DELETE c FROM dbo.AUDIT_FIELD_CHANGE c JOIN dbo.AUDIT_EVENT e ON e.EVENT_ID = c.EVENT_ID WHERE e.M_IDX = @m;");
'@ },
    [pscustomobject]@{ Expect = $true; Text = @'
await ExecAsync(connection, "DELETE e FROM dbo.AUDIT_EVENT e WHERE e.M_IDX = @m;");
'@ },
    [pscustomobject]@{ Expect = $true; Text = @'
await ExecAsync(connection, "TRUNCATE TABLE dbo.AUDIT_EVENT;");
'@ },
    [pscustomobject]@{ Expect = $true; Text = @'
await ExecAsync(connection, "DELETE TOP (100) FROM dbo.AUDIT_EVENT WHERE M_IDX = @m;");
'@ },
    [pscustomobject]@{ Expect = $true; Text = @'
await ExecAsync(connection, "DELETE FROM [dbo].[AUDIT_FIELD_CHANGE] WHERE EVENT_ID = @id;");
'@ },
    [pscustomobject]@{ Expect = $true; Text = @'
var sql = "DELETE FROM dbo." + "AUDIT_EVENT WHERE ACTION=@action;"
await ExecAsync(connection, sql, ("@action", action));
'@ },
    [pscustomobject]@{ Expect = $false; Text = @'
await ExecAsync(connection, "DELETE FROM dbo.WORKBENCH_IDEMPOTENCY WHERE M_IDX=@module AND ACTION=N'ACTION';", ("@module", ModuleId));
'@ },
    [pscustomobject]@{ Expect = $false; Text = @'
var total = await ScalarAsync<int>(connection, "SELECT COUNT(*) FROM dbo.AUDIT_EVENT WHERE M_IDX=@module;");
'@ },
    [pscustomobject]@{ Expect = $false; Text = @'
await ExecAsync(connection, "DELETE FROM dbo.AUDIT_LOG_ARCHIVE WHERE M_IDX=@module;", ("@module", ModuleId));
'@ }
)

function Invoke-SelfTest {
    param([string[]] $Positive, [string[]] $Negative, [object[]] $AuditCases = @())

    $cases = @()
    foreach ($sample in $Positive) { $cases += [pscustomobject]@{ Kind = '正样本'; Expect = $true; Text = $sample } }
    foreach ($sample in $Negative) { $cases += [pscustomobject]@{ Kind = '反样本'; Expect = $false; Text = $sample } }

    $failed = 0
    $index = 0
    foreach ($case in $cases) {
        $index++
        # 自检样本按脚本形态判：脚本的写入是拼接字符串/HTTP 载荷，规则①走的分支不同。
        $fileName = if ($case.Text -match '(?m)^\s*\$\w+\s*=') { "SelfTest$index.ps1" } else { "SelfTest${index}LiveTests.cs" }
        $found = @(Get-HygieneFindings -FileName $fileName -Text $case.Text)
        $actual = $found.Count -gt 0
        $rules = (@($found | ForEach-Object { "$($_.Rule)@$($_.Line)" }) -join ',')
        if ($actual -ne $case.Expect) {
            $failed++
            Write-Output ("FAIL 自检[{0} #{1}] 期望命中={2} 实际命中={3}（{4}）" -f $case.Kind, $index, $case.Expect, $actual, $rules)
        }
        else {
            Write-Output ("PASS 自检[{0} #{1}] 期望命中={2} 实际命中={3} {4}" -f $case.Kind, $index, $case.Expect, $actual, $rules)
        }
    }

    # 规则②单列一轮：只看 AUDIT_DELETE 这条规则，证明各删除形态确实被它（而不是别的规则）抓到。
    $auditFailed = 0
    $auditIndex = 0
    foreach ($case in $AuditCases) {
        $auditIndex++
        $fileName = if ($case.Text -match '(?m)^\s*\$\w+\s*=') { "SelfTestAudit$auditIndex.ps1" } else { "SelfTestAudit${auditIndex}LiveTests.cs" }
        $hits = @(Get-HygieneFindings -FileName $fileName -Text $case.Text | Where-Object { $_.Rule -eq 'AUDIT_DELETE' })
        $actual = $hits.Count -gt 0
        if ($actual -ne $case.Expect) {
            $auditFailed++
            Write-Output ("FAIL 自检[规则② #{0}] 期望 AUDIT_DELETE 命中={1} 实际命中={2}" -f $auditIndex, $case.Expect, $actual)
        }
        else {
            $lines = (@($hits | ForEach-Object { $_.Line }) -join ',')
            Write-Output ("PASS 自检[规则② #{0}] 期望 AUDIT_DELETE 命中={1} 实际命中={2}（行 {3}）" -f $auditIndex, $case.Expect, $actual, $lines)
        }
    }

    $positiveCount = $Positive.Count
    $negativeCount = $Negative.Count
    if ($positiveCount -lt 2 -or $negativeCount -lt 2) {
        Write-Output 'FAIL 自检样本不足（正反各需 ≥2 条）'
        exit 1
    }
    if ($auditIndex -lt 4) {
        Write-Output 'FAIL 规则②自检样本不足（形态正反需 ≥4 条）'
        exit 1
    }
    $failures = @()
    if ($failed -gt 0) { $failures += "总体命中 {0} 例不符" -f $failed }
    if ($auditFailed -gt 0) { $failures += "规则② {0} 例不符" -f $auditFailed }
    if ($failures.Count -gt 0) {
        Write-Output ("FAIL 自检：{0}（正 {1} / 反 {2} / 规则② {3}）" -f
            ($failures -join '；'), $positiveCount, $negativeCount, $auditIndex)
        exit 1
    }
    Write-Output ("PASS 自检：{0} 例全部符合预期（正样本 {1} / 反样本 {2} / 规则②形态 {3}）" -f
        ($cases.Count + $auditIndex), $positiveCount, $negativeCount, $auditIndex)
    exit 0
}

if ($SelfTest) { Invoke-SelfTest -Positive $selfTestPositive -Negative $selfTestNegative -AuditCases $selfTestAuditCases }

# --- 主流程 -------------------------------------------------------------------

if (-not $TestsDir) { $TestsDir = Join-Path (Split-Path -Parent $PSScriptRoot) 'EOS.API.Tests' }
$TestsDir = (Resolve-Path -LiteralPath $TestsDir).Path

$liveFiles = @(Get-ChildItem -LiteralPath $TestsDir -Recurse -File |
        Where-Object { $_.FullName -notmatch '\\(bin|obj|obj-tmp)\\' } |
        Where-Object { $_.Name -like '*LiveTests.cs' -or $_.Extension -eq '.ps1' } |
        Sort-Object Name)

if ($liveFiles.Count -eq 0) { Write-Output "FAIL 未在 $TestsDir 找到真库用例文件"; exit 1 }

$newBorrow = @(); $newAudit = @(); $newDepotWide = @()
$allowedBorrow = @{}; $allowedAudit = @{}; $allowedDepotWide = @{}

foreach ($file in $liveFiles) {
    $text = Get-Content -Raw -LiteralPath $file.FullName
    foreach ($finding in @(Get-HygieneFindings -FileName $file.Name -Text $text)) {
        switch ($finding.Rule) {
            'BORROW_WRITE' {
                if ($borrowWriteAllow.ContainsKey($file.Name)) { $allowedBorrow[$file.Name] = $true }
                else { $newBorrow += $finding }
            }
            'AUDIT_DELETE' {
                if ($auditDeleteAllow -contains $file.Name) { $allowedAudit[$file.Name] = $true }
                else { $newAudit += $finding }
            }
            'DEPOT_WIDE_DELETE' {
                if ($depotWideDeleteAllow -contains $finding.Key) { $allowedDepotWide[$finding.Key] = $true }
                else { $newDepotWide += $finding }
            }
        }
    }
}

$fail = $false

if ($newBorrow.Count -gt 0) {
    Write-Output ("FAIL 借真实主档用于写入：{0} 处" -f $newBorrow.Count)
    foreach ($finding in $newBorrow) {
        Write-Output ("       {0}:{1} {2}" -f $finding.Key, $finding.Line, $finding.Detail)
    }
    $fail = $true
}
else {
    Write-Output ("PASS 借真实主档：{0} 处未登记的写入用取样值（已登记存量 {1} 个文件）" -f 0, $allowedBorrow.Count)
}

if ($newAudit.Count -gt 0) {
    Write-Output ("FAIL 真库用例删审计：{0} 处" -f $newAudit.Count)
    foreach ($finding in $newAudit) {
        Write-Output ("       {0}:{1} {2}" -f $finding.Key, $finding.Line, $finding.Detail)
    }
    $fail = $true
}
else {
    Write-Output ("PASS 删审计：0 处未登记（存量 {0} 个文件已具名登记，棘轮只减不增）" -f $allowedAudit.Count)
}

if ($newDepotWide.Count -gt 0) {
    Write-Output ("FAIL 按库别整体删数量承载表：{0} 处" -f $newDepotWide.Count)
    foreach ($finding in $newDepotWide) {
        Write-Output ("       {0}:{1} {2}" -f $finding.Key, $finding.Line, $finding.Detail)
    }
    $fail = $true
}
else {
    Write-Output ("PASS 按库别删数量承载表：0 处未登记（存量 {0} 处）" -f $allowedDepotWide.Count)
}

# 存量登记若已不再命中，提示下调（棘轮只减不增的另一半：减了要登记）
$staleAudit = @($auditDeleteAllow | Where-Object { -not $allowedAudit.ContainsKey($_) })
if ($staleAudit.Count -gt 0) {
    Write-Output ("NOTE 删审计存量里这 {0} 个文件已无命中，请从 `$auditDeleteAllow` 移除：{1}" -f
        $staleAudit.Count, ($staleAudit -join ', '))
}
$staleBorrow = @($borrowWriteAllow.Keys | Where-Object { -not $allowedBorrow.ContainsKey($_) })
if ($staleBorrow.Count -gt 0) {
    Write-Output ("NOTE 借主档存量里这 {0} 个文件已无命中，请从 `$borrowWriteAllow` 移除：{1}" -f
        $staleBorrow.Count, ($staleBorrow -join ', '))
}

exit ($fail ? 1 : 0)
