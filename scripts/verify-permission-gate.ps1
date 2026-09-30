#Requires -Version 7.0
<#
.SYNOPSIS
    权限门端到端验证：确认"没有该模块权限的账号"在真实 HTTP 上被拒（403；选择器类 403/404），而不是 200。

.DESCRIPTION
    「API 是最终权限边界」的可检验形式是：**有登录凭据但无该模块权限**的账号必须被拒。
    此前只验到"未认证访问返回 401"——那只证明接口要求登录，不证明无权限者会被拦住。

    绝大多数入口无权限返回 **403**；**选择器类探针（`/api/v1/chooser/*`）例外，403 与 404 都算被拒**。
    这不是放水：`ChooserController.Query` 对权限不足**故意**返回 `404 SOURCE_NOT_FOUND`，
    注释原文写明理由——"403 会告诉调用方'该数据源存在、只是你没权限'，等于把数据源清单当探测面"。
    防探测口径是既有设计（与单据读取路径同一口径），所以该改的是**本脚本的期望值**，而不是控制器。
    除这两个状态码外一切仍算失败：**整站放行了（200）与整站故障了（500）都必须仍然是红**，
    否则"被拒"会因 404/403 都算通过而被读成"任何返回都通过"。
    这份判别力由 `-SelfTest` 的正反合成用例守着——改期望表或改判定函数都必须先过它。

    两种取得"无权限"条件的方式，优先用第一种：

    1. **受试账号本就无权限**（默认）：零数据改动，就是生产形态。传 -SubjectUser。
       若该账号实际拿到了 200，脚本**不会**含糊地报"期望 403 实得 200"，而是查出
       **是谁给的权限**（哪几个组的哪一行、有没有个人行）——账号"看起来无权限"最常见的原因
       就是被加进了某个有权组，而个人权限表里一行都没有，光看 SYSDD 会误判。

    2. **-DeriveUnprivileged**：受试账号有权限时，在**一个永不提交的事务**里临时让它失去权限
       （脱离全部组 + 把受试模块的个人行置为拒绝）。权限读取带 `WITH (NOLOCK)`，
       因此未提交改动对接口即时可见；结束时一律 ROLLBACK，脚本崩溃、连接断开由 SQL Server
       自动回滚，**不留权限残留**。

    两种方式都先验证**对照账号**（默认 admin）能正常访问同一批端点，以排除
    "接口整体挂了 / 账号被整体禁用"这类会让 403 变成假通过的替代解释。

.EXAMPLE
    pwsh scripts/verify-permission-gate.ps1 -SubjectUser larry
    pwsh scripts/verify-permission-gate.ps1 -SubjectUser larry -DeriveUnprivileged
    pwsh scripts/verify-permission-gate.ps1 -SelfTest
#>
[CmdletBinding()]
param(
    [string] $ApiUrl = 'http://localhost:5261',
    # 受试账号：应由配置决定为"无该模块权限"，脚本不假定、由结果说话。
    [string] $SubjectUser = $env:EOS_VERIFY_SUBJECT_USER,
    [string] $SubjectPassword = $env:EOS_VERIFY_SUBJECT_PASSWORD,
    # 对照账号：必须有权限，用于证明端点本身可用。
    [string] $ControlUser = 'admin',
    [string] $ControlPassword = 'admin',
    # 受试账号有权限时，是否在未提交事务内临时制造无权限条件。
    [switch] $DeriveUnprivileged,
    # 输入边界断言：越界参数必须是 400，不能由数据库抛异常冒成 500。
    [bool] $CheckInputBoundary = $true,
    # 正反自检：合成状态码验证"被拒"的判据仍有判别力（不连库、不连 API）。
    # 改了期望表或 Test-ProbeDenied 之后必跑——它就是防止"放宽成任何状态码都通过"的那道闸。
    [switch] $SelfTest
)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8
. (Join-Path $PSScriptRoot '..\EOS.API.Tests\AcceptanceCommon.ps1')

$ProbeDepot = 'ADR14PG'
$OverlongDepot = 'ADR14POLTOOLONG'

# 各探针在"应当被拒"时接受的状态码：只要不在这个集合里，就**不是**通过。
# 选择器（/api/v1/chooser/*）额外接受 404，原因见 ChooserController.Query 的注释：
# 权限不足按 404 返回（"403 会告诉调用方该数据源存在、只是你没权限，等于把数据源清单当探测面"）。
$script:ModuleDeniedStatuses = @(403)
$script:ChooserDeniedStatuses = @(403, 404)

$probes = @(
    [pscustomobject]@{
        Module = 110310
        Kind   = 'module-read'
        Name   = '库存策略'
        DeniedStatuses = $script:ModuleDeniedStatuses
        Read   = @{ Method = 'GET'; Path = '/api/v1/admin/depot-stock-policy'; Body = $null }
        Write  = @{ Method = 'PUT'; Path = "/api/v1/admin/depot-stock-policy/$ProbeDepot"
                    Body = @{ locationMode = 3; storageMode = 'FIXED'; batchMode = 1; capacityMode = 0
                              mixProduct = $true; mixBatch = $true
                              monthCloseByBatch = $true; monthCloseByLocation = $false } }
    }
    [pscustomobject]@{
        Module = 110309
        Kind   = 'chooser'
        Name   = '库位主档'
        DeniedStatuses = $script:ChooserDeniedStatuses
        Read   = @{ Method = 'POST'; Path = '/api/v1/chooser/query'
                    Body = @{ sourceKey = 'depot-admin.locations'; page = 1; pageSize = 5 } }
        Write  = $null   # 该模块只有读入口（选择器数据源）
    }
)

function Test-ProbeDenied {
    <#
      判定某探针在"应当被拒"时返回的状态码是否成立。
      刻意收窄成显式集合，而不是写成"-ne 200"：把"任何非 200"都当成被拒，
      会让整站故障（500）、鉴权没生效（401）都被读成"权限门生效"——那是假通过。
    #>
    param([int[]]$DeniedStatuses, [int]$Status)
    return $DeniedStatuses -contains $Status
}

function Format-DeniedExpectation {
    param([int[]]$DeniedStatuses)
    return ($DeniedStatuses -join '/')
}

function Invoke-GateSelfTest {
    <#
      合成状态码的正反自检。正例与反例缺一不可：只留正例时，把判定改成"任何状态码都通过"
      （或往期望集合里塞 200）仍能全绿，这条门禁就退化成装饰。
      期望值在这里**写死**，不复用 $script:ChooserDeniedStatuses 等变量——
      否则"改期望表"这个动作会连带放松自检，注入就漏过去了。
    #>
    param([object[]]$ProbeTable)

    $selfFails = [System.Collections.Generic.List[string]]::new()
    $cases = @(
        [pscustomobject]@{ Name = '模块读取类 403 ⇒ 被拒'; Statuses = @(403); Status = 403; Expect = $true }
        [pscustomobject]@{ Name = '模块读取类 404 ⇒ 不算被拒（仍要求 403）'; Statuses = @(403); Status = 404; Expect = $false }
        [pscustomobject]@{ Name = '选择器类 403 ⇒ 被拒'; Statuses = @(403, 404); Status = 403; Expect = $true }
        [pscustomobject]@{ Name = '选择器类 404 ⇒ 被拒（防探测口径）'; Statuses = @(403, 404); Status = 404; Expect = $true }
        [pscustomobject]@{ Name = '选择器类 200 ⇒ 不算被拒（权限门没起作用）'; Statuses = @(403, 404); Status = 200; Expect = $false }
        [pscustomobject]@{ Name = '选择器类 401 ⇒ 不算被拒（那只证明要登录）'; Statuses = @(403, 404); Status = 401; Expect = $false }
        [pscustomobject]@{ Name = '选择器类 500 ⇒ 不算被拒（整站故障不是被拒）'; Statuses = @(403, 404); Status = 500; Expect = $false }
    )

    Write-Output '== verify-permission-gate 自检（合成状态码，不连库/不连 API）=='
    foreach ($case in $cases) {
        $actual = Test-ProbeDenied -DeniedStatuses $case.Statuses -Status $case.Status
        $mark = if ($actual -eq $case.Expect) { 'PASS' } else { 'FAIL' }
        Write-Output ("  {0,-4} {1}  实得={2} 期望={3}" -f $mark, $case.Name, $actual, $case.Expect)
        if ($mark -eq 'FAIL') { $selfFails.Add($case.Name) }
    }

    # 期望表本身：选择器类必须恰好是 403/404，其余类必须恰好是 403，多一个少一个都要红。
    foreach ($probe in $ProbeTable) {
        $expectSet = if ($probe.Kind -eq 'chooser') { @(403, 404) } else { @(403) }
        $actualSet = @($probe.DeniedStatuses | Sort-Object)
        $same = (($actualSet -join ',') -eq (($expectSet | Sort-Object) -join ','))
        $mark = if ($same) { 'PASS' } else { 'FAIL' }
        Write-Output ("  {0,-4} 期望表 {1}({2}) kind={3} 被拒集合={4}" -f $mark, $probe.Name, $probe.Module,
            $probe.Kind, ($actualSet -join '/'))
        if (-not $same) {
            $selfFails.Add("期望表 $($probe.Name)($($probe.Module)) 的被拒集合应为 $($expectSet -join '/')。")
        }
    }

    if ($selfFails.Count -gt 0) {
        Write-Output 'FAIL 自检未通过：'
        $selfFails | ForEach-Object { Write-Output "  $_" }
        exit 1
    }
    Write-Output 'PASS 自检通过：选择器类 403/404 判为被拒，200/401/500 与其它状态码仍判为未通过。'
    exit 0
}

if ($SelfTest) {
    Invoke-GateSelfTest -ProbeTable $probes
}

if ([string]::IsNullOrWhiteSpace($SubjectUser)) {
    throw '必须指定受试账号：-SubjectUser <账号>（或设环境变量 EOS_VERIFY_SUBJECT_USER）。'
}
if ([string]::IsNullOrEmpty($SubjectPassword)) {
    throw "受试账号 $SubjectUser 未提供密码：-SubjectPassword <密码>（或设环境变量 EOS_VERIFY_SUBJECT_PASSWORD）。"
}

function New-EosRawConnection {
    $conn = Get-EosConn
    $cs = if ($conn.User) {
        "Server=$($conn.Server);Database=$($conn.Database);User ID=$($conn.User);Password=$($conn.Password);Encrypt=True;TrustServerCertificate=True;Connect Timeout=60"
    } else {
        "Server=$($conn.Server);Database=$($conn.Database);Integrated Security=True;Encrypt=True;TrustServerCertificate=True;Connect Timeout=60"
    }
    $sql = [System.Data.SqlClient.SqlConnection]::new($cs)
    $sql.Open()
    return $sql
}

function Invoke-Probe {
    param($Session, $Probe)
    $read = Invoke-EosApi -Method $Probe.Read.Method -Path $Probe.Read.Path -Body $Probe.Read.Body -Session $Session -ApiUrl $ApiUrl
    $writeStatus = $null
    if ($Probe.Write) {
        $writeStatus = (Invoke-EosApi -Method $Probe.Write.Method -Path $Probe.Write.Path -Body $Probe.Write.Body `
            -Session $Session -ApiUrl $ApiUrl).Status
    }
    return @{ Read = $read.Status; Write = $writeStatus }
}

function Get-AccessSource {
    <#
      查清受试账号当前是**凭什么**拿到某模块权限的：个人行，还是某几个组。
      这条诊断是"账号看起来没权限、实际有权限"时唯一能直接给出答案的东西。
    #>
    param($Connection, [string]$UserId, [int]$ModuleId)
    $sql = @"
SET NOCOUNT ON;
SELECT 'personal:' + LTRIM(RTRIM(EXEC_TAG)) + '/' + CAST(ISNULL(SETUP_TAG,0) AS varchar(1))
FROM dbo.SYSDD WITH (NOLOCK)
WHERE LTRIM(RTRIM(USER_ID))=@u AND M_IDX=@m
UNION ALL
SELECT 'group ' + LTRIM(RTRIM(h.G_IDX)) + ':' + LTRIM(RTRIM(ISNULL(h.EXEC_TAG,'A'))) + '/' + CAST(ISNULL(h.SETUP_TAG,0) AS varchar(1))
FROM dbo.SYSDH h WITH (NOLOCK)
JOIN dbo.SYSDG_USER gu WITH (NOLOCK) ON gu.G_IDX = h.G_IDX
WHERE LTRIM(RTRIM(gu.USER_ID))=@u AND h.M_IDX=@m;
"@
    $cmd = $Connection.CreateCommand()
    $cmd.CommandText = $sql
    [void]$cmd.Parameters.AddWithValue('@u', $UserId)
    [void]$cmd.Parameters.AddWithValue('@m', $ModuleId)
    $rows = [System.Collections.Generic.List[string]]::new()
    $reader = $cmd.ExecuteReader()
    while ($reader.Read()) { $rows.Add($reader.GetString(0)) }
    $reader.Dispose(); $cmd.Dispose()
    return $rows
}

$failures = [System.Collections.Generic.List[string]]::new()
$diagnostics = [System.Collections.Generic.List[string]]::new()

Write-Output "== 权限门端到端验证 subject=$SubjectUser control=$ControlUser api=$ApiUrl =="

try {
    $controlSession = New-EosSession -ApiUrl $ApiUrl -UserId $ControlUser -Password $ControlPassword
} catch {
    throw "对照账号 $ControlUser 登录失败：$($_.Exception.Message)"
}
try {
    $subjectSession = New-EosSession -ApiUrl $ApiUrl -UserId $SubjectUser -Password $SubjectPassword
} catch {
    throw "受试账号 $SubjectUser 登录失败：$($_.Exception.Message)"
}

$connection = New-EosRawConnection
try {
    # ---------- 对照：端点本身必须可用（否则 403 可能是整站故障，属假通过） ----------
    foreach ($probe in $probes) {
        $status = Invoke-Probe -Session $controlSession -Probe $probe
        Write-Output ("  对照  {0}({1})  读={2} 写={3}" -f $probe.Name, $probe.Module, $status.Read,
            ($(if ($null -eq $status.Write) { '-' } else { $status.Write })))
        if ($status.Read -ne 200 -or ($null -ne $status.Write -and $status.Write -ne 200)) {
            $failures.Add("对照账号 $ControlUser 访问 $($probe.Name)($($probe.Module)) 返回 读=$($status.Read) 写=$($status.Write)，端点本身异常，本次拒绝结论不成立。")
        }
    }
    if ($failures.Count -gt 0) { throw ($failures -join "`n") }

    # ---------- 受试账号：无权限时必须被拒（403；选择器类 403/404，见顶部说明） ----------
    foreach ($probe in $probes) {
        $baseline = Invoke-Probe -Session $subjectSession -Probe $probe
        Write-Output ("  受试  {0}({1})  读={2} 写={3}" -f $probe.Name, $probe.Module, $baseline.Read,
            ($(if ($null -eq $baseline.Write) { '-' } else { $baseline.Write })))

        if ((Test-ProbeDenied -DeniedStatuses $probe.DeniedStatuses -Status $baseline.Read) -and
            ($null -eq $baseline.Write -or
             (Test-ProbeDenied -DeniedStatuses $probe.DeniedStatuses -Status $baseline.Write))) {
            continue   # 本就无权限：零改动，断言已成立
        }
        if ($baseline.Read -ne 200) {
            $failures.Add("受试账号 $SubjectUser 访问 $($probe.Name)($($probe.Module)) 返回 $($baseline.Read)，既不是 200 也不是被拒状态码（$(Format-DeniedExpectation -DeniedStatuses $probe.DeniedStatuses)）。")
            continue
        }

        # 拿到 200 ⇒ 该账号当下**有**权限。查清来源再决定怎么办，不要含糊地报"期望被拒"。
        $sources = Get-AccessSource -Connection $connection -UserId $SubjectUser -ModuleId $probe.Module
        $origin = if ($sources.Count -eq 0) { '（查不到权限来源，请人工核对）' } else { $sources -join '、' }
        $diagnostics.Add("$($probe.Name)($($probe.Module)) 的权限来源：$origin")

        if (-not $DeriveUnprivileged) {
            $failures.Add(
                "受试账号 $SubjectUser 并非无权限账号（$($probe.Name)($($probe.Module)) 返回 200，权限来源：$origin）。" +
                "请改配置（通常是把它移出有权的组），或加 -DeriveUnprivileged 在未提交事务内临时制造无权限条件。")
            continue
        }

        # ---------- 事务内临时制造无权限条件 ----------
        $transaction = $connection.BeginTransaction()
        try {
            $deny = $connection.CreateCommand()
            $deny.Transaction = $transaction
            $deny.CommandText = "DELETE FROM dbo.SYSDG_USER WHERE LTRIM(RTRIM(USER_ID))=@u; " +
                                "UPDATE dbo.SYSDD SET EXEC_TAG='A', SETUP_TAG=0 WHERE LTRIM(RTRIM(USER_ID))=@u AND M_IDX=@m;"
            [void]$deny.Parameters.AddWithValue('@u', $SubjectUser)
            [void]$deny.Parameters.AddWithValue('@m', $probe.Module)
            [void]$deny.ExecuteNonQuery()
            $deny.Dispose()

            # 选择器类探针在这里会是 404 SOURCE_NOT_FOUND 而不是 403（防探测口径，见顶部说明）。
            $denied = Invoke-Probe -Session $subjectSession -Probe $probe
            Write-Output ("  └ 临时收回后  读={0} 写={1}" -f $denied.Read,
                ($(if ($null -eq $denied.Write) { '-' } else { $denied.Write })))
            $expect = Format-DeniedExpectation -DeniedStatuses $probe.DeniedStatuses
            if (-not (Test-ProbeDenied -DeniedStatuses $probe.DeniedStatuses -Status $denied.Read)) {
                $failures.Add("$($probe.Name)($($probe.Module)) 临时收回权限后读请求返回 $($denied.Read)，期望 $expect。")
            }
            if ($null -ne $denied.Write -and
                -not (Test-ProbeDenied -DeniedStatuses $probe.DeniedStatuses -Status $denied.Write)) {
                $failures.Add("$($probe.Name)($($probe.Module)) 临时收回权限后写请求返回 $($denied.Write)，期望 $expect。")
            }
        }
        finally {
            $transaction.Rollback()
            $transaction.Dispose()
        }

        # 回滚后必须回到 200：这一条顺带排除了"选择器那个 404 其实来自 SOURCE_EMPTY / 数据源不存在"的解释。
        $restored = Invoke-Probe -Session $subjectSession -Probe $probe
        Write-Output ("  └ 回滚后      读={0}" -f $restored.Read)
        if ($restored.Read -ne 200) {
            $failures.Add("回滚后 $($probe.Name)($($probe.Module)) 读请求返回 $($restored.Read)，期望 200（权限未还原）。")
        }
    }

    # ---------- 输入边界：越界参数必须 400，不能冒成 500 ----------
    if ($CheckInputBoundary) {
        $boundary = Invoke-EosApi -Method 'PUT' -Path "/api/v1/admin/depot-stock-policy/$OverlongDepot" `
            -Body @{ locationMode = 3; storageMode = 'FIXED'; batchMode = 1; capacityMode = 0
                     mixProduct = $true; mixBatch = $true
                     monthCloseByBatch = $true; monthCloseByLocation = $false } `
            -Session $controlSession -ApiUrl $ApiUrl
        Write-Output ("  越界库别代号（{0} 字符）PUT = HTTP {1}" -f $OverlongDepot.Length, $boundary.Status)
        if ($boundary.Status -ne 400) {
            $failures.Add("越界库别代号 PUT 返回 $($boundary.Status)，期望 400（500 说明取值域校验漏到了数据库层）。")
        }
    }

    # ---------- 还原核对 ----------
    if ($DeriveUnprivileged) {
        $verify = $connection.CreateCommand()
        $verify.CommandText = "SELECT COUNT(*) FROM dbo.SYSDG_USER WHERE LTRIM(RTRIM(USER_ID))=@u"
        [void]$verify.Parameters.AddWithValue('@u', $SubjectUser)
        $groups = [int]$verify.ExecuteScalar()
        $verify.Dispose()
        Write-Output "  还原核对：$SubjectUser 的组行数 = $groups"
    }
}
finally {
    $connection.Dispose()
    try {
        # 若权限门失效导致写请求真的落库，这里兜底清掉探针用的库别行。
        $null = Invoke-EosSqlNonQuery -Query "DELETE FROM dbo.DEPOT_STOCK_POLICY WHERE DEPOT_ID=N'$ProbeDepot'"
    } catch {
        Write-Output "  [WARN] 探针库别行清理失败：$($_.Exception.Message)"
    }
}

if ($diagnostics.Count -gt 0) {
    Write-Output '诊断：'
    $diagnostics | ForEach-Object { Write-Output "  $_" }
}
if ($failures.Count -gt 0) {
    Write-Output 'FAIL 权限门验证未通过：'
    $failures | ForEach-Object { Write-Output "  $_" }
    exit 1
}
$mode = if ($DeriveUnprivileged) { '临时收回（事务已回滚，权限无残留）' } else { '账号配置本身即为无权限' }
Write-Output "PASS 无权限账号 $SubjectUser 被拒（模块读取类要求 403；选择器类 403 或 404 均可，防探测口径见 ChooserController.Query），对照账号 $ControlUser 正常（200）；取值条件：$mode。"
exit 0
