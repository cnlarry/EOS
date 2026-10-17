#Requires -Version 7.0
<#
.SYNOPSIS
    D 层：自造"极端但合法"的模块配置，断言接口要么正常、要么带原因码拒绝，绝不 5xx。

.DESCRIPTION
    前三层验的是"已有配置对不对"，本层验的是"**配置得离谱时服务端会不会崩**"。

    做法：克隆一个真实模块到保留号段（990001–990099），只改**模块级**配置
    （`MODULES` 自身列、`MODULE_FORM_*`），因为 `FIELDS` / `FIELD_DATASOURCE` 是按
    (表, 字段) 共享的——改它们会波及真实模块。每个用例跑三个探针：
    表单定义（新增态）、建单（空载荷）、读回，逐条断言：

      · 5xx            → `FUZZ_5XX`（最严重：离谱配置不该让服务端崩）
      · 4xx 但没有 code → `FUZZ_NO_CODE`（前端只能显示"请求失败"）
      · 2xx            → 正常（若建出了记录，立即删掉）

    安全边界（必须守住，否则会污染配置面）：
      · 只操作 990001–990099 号段，模板模块只做 `SELECT`；
      · 每个用例 try/finally 收尾，**无论成败都删**克隆模块与其版式行；
      · 异常退出也清理；`-Cleanup` 可单独清场（万一脚本被强杀）。

.PARAMETER BaseUrl
    前端基址，默认 http://localhost（登录与 API 同源）。

.PARAMETER Template
    克隆用的模板模块号，默认 1401（客户资料：无明细、无 FILTER、无副作用）。

.PARAMETER Case
    只跑某一个用例（C1/C2/C3/C4）。

.PARAMETER Cleanup
    只清场、不跑用例。

.EXAMPLE
    pwsh scripts/module-config-fuzz.ps1
    pwsh scripts/module-config-fuzz.ps1 -Case C2 -KeepFailed
    pwsh scripts/module-config-fuzz.ps1 -Cleanup
#>
param(
    [string] $BaseUrl = 'http://localhost',
    [int] $Template = 1401,
    [string] $Case,
    [switch] $Cleanup,
    [switch] $KeepFailed
)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8

$root = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot 'dev/eos-sql.ps1')

$script:RangeStart = 990001
$script:RangeEnd = 990099
$script:Findings = New-Object System.Collections.ArrayList
$script:Infos = New-Object System.Collections.ArrayList

function Invoke-Sql([string] $Query) {
    return @(Invoke-EosSqlQuery -Query $Query)
}

function Remove-FuzzModules {
    <# 清场：只动保留号段，且只删本脚本造出来的（M_DESC 以 ZZ-FUZZ 开头）。 #>
    # 清场必须覆盖"setup 造过的一切"。漏掉 `WORKBENCH_DEFINITION_SNAPSHOT` 会让下一轮
    # 复用上一次的孤儿快照：publish 判定"定义没变"→ 回 `published=false` 且 **checks 为空**，
    # 于是同一个用例第一次通过、第二次变成"拒绝但没有原因"——看起来像接口不稳定，
    # 实际是清场漏了一张表（实测残留 4 行才发现）。
    Invoke-Sql "SET NOCOUNT ON;
DELETE FROM dbo.MODULE_FORM_LAYOUT WHERE M_IDX BETWEEN $script:RangeStart AND $script:RangeEnd;
DELETE FROM dbo.MODULE_FORM_TAB WHERE M_IDX BETWEEN $script:RangeStart AND $script:RangeEnd;
DELETE FROM dbo.MODULE_VALIDATION_RULE WHERE M_IDX BETWEEN $script:RangeStart AND $script:RangeEnd;
DELETE FROM dbo.MODULE_BUSINESS_ACTION WHERE M_IDX BETWEEN $script:RangeStart AND $script:RangeEnd;
DELETE FROM dbo.SYSQR_USER WHERE M_IDX BETWEEN $script:RangeStart AND $script:RangeEnd;
DELETE FROM dbo.SYSQR_DEFAULT WHERE M_IDX BETWEEN $script:RangeStart AND $script:RangeEnd;
DELETE FROM dbo.WORKBENCH_DEFINITION_SNAPSHOT WHERE M_IDX BETWEEN $script:RangeStart AND $script:RangeEnd;
DELETE FROM dbo.WORKBENCH_MODULE_DIRTY WHERE M_IDX BETWEEN $script:RangeStart AND $script:RangeEnd;
DELETE FROM dbo.MODULES WHERE M_IDX BETWEEN $script:RangeStart AND $script:RangeEnd AND M_DESC LIKE N'ZZ-FUZZ%';
SELECT 'cleaned';" | Out-Null
}

function New-FuzzModule {
    <# 克隆模板模块到指定号段：动态取列，避免手写列清单随表结构漂移。 #>
    param([int] $NewId, [string] $Desc, [string] $Mutation)

    $sql = @"
SET NOCOUNT ON;
DECLARE @tpl INT = $Template, @new INT = $NewId;
DECLARE @cols NVARCHAR(MAX), @sel NVARCHAR(MAX);
SELECT @cols = STRING_AGG(QUOTENAME(name), N',') WITHIN GROUP (ORDER BY column_id),
       @sel = STRING_AGG(CASE WHEN name = N'M_IDX' THEN CONVERT(NVARCHAR(MAX), @new) ELSE QUOTENAME(name) END, N',')
          WITHIN GROUP (ORDER BY column_id)
  FROM sys.columns WHERE object_id = OBJECT_ID(N'dbo.MODULES') AND is_identity = 0;

EXEC(N'DELETE FROM dbo.MODULES WHERE M_IDX = ' + @new);
EXEC(N'INSERT INTO dbo.MODULES (' + @cols + N') SELECT ' + @sel + N' FROM dbo.MODULES WHERE M_IDX = ' + @tpl);

/* 模块级附属配置必须一起克隆，否则请求会卡在授权阶段（权限行缺失 → 404），
   探针根本走不到配置解析逻辑，等于什么都没测。白名单只收**模块级**配置表，
   绝不碰 AUDIT_EVENT / WF_APPROVE / WORKBENCH_IDEMPOTENCY 这类运行数据。 */
DECLARE @clone TABLE (tbl sysname);
INSERT @clone (tbl) VALUES (N'SYSQR_USER'), (N'SYSQR_DEFAULT'), (N'MODULE_FORM_TAB'),
                           (N'MODULE_FORM_LAYOUT'), (N'MODULE_VALIDATION_RULE'), (N'MODULE_BUSINESS_ACTION');

DECLARE @t sysname, @cols2 NVARCHAR(MAX), @sel2 NVARCHAR(MAX), @stmt NVARCHAR(MAX);
DECLARE cur CURSOR LOCAL FAST_FORWARD FOR SELECT tbl FROM @clone;
OPEN cur;
FETCH NEXT FROM cur INTO @t;
WHILE @@FETCH_STATUS = 0
BEGIN
    SELECT @cols2 = STRING_AGG(QUOTENAME(name), N',') WITHIN GROUP (ORDER BY column_id),
           @sel2 = STRING_AGG(CASE WHEN name = N'M_IDX' THEN CONVERT(NVARCHAR(MAX), @new) ELSE QUOTENAME(name) END, N',')
              WITHIN GROUP (ORDER BY column_id)
      FROM sys.columns WHERE object_id = OBJECT_ID(N'dbo.' + @t) AND is_identity = 0;
    IF @cols2 IS NOT NULL AND OBJECT_ID(N'dbo.' + @t, N'U') IS NOT NULL
    BEGIN
        SET @stmt = N'INSERT INTO dbo.' + QUOTENAME(@t) + N' (' + @cols2 + N') SELECT ' + @sel2
                  + N' FROM dbo.' + QUOTENAME(@t) + N' WHERE M_IDX = ' + CONVERT(NVARCHAR(MAX), @tpl) + N';';
        EXEC sp_executesql @stmt;
    END
    FETCH NEXT FROM cur INTO @t;
END
CLOSE cur; DEALLOCATE cur;

UPDATE dbo.MODULES SET M_DESC = N'ZZ-FUZZ $Desc', M_URL = N'/workbench', UNIFIED_FORM_EDIT_ENABLED = 1
 WHERE M_IDX = @new;
$Mutation
SELECT CONCAT('created=', @new);
"@
    $temp = Join-Path ([IO.Path]::GetTempPath()) "eos-fuzz-$NewId.sql"
    Set-Content -LiteralPath $temp -Value $sql -Encoding utf8
    try { $null = Invoke-EosSqlFile -Path $temp -NoErrorStop } finally { Remove-Item -LiteralPath $temp -Force -ErrorAction SilentlyContinue }
}

function Invoke-Probe([string] $Path, [string] $Method = 'GET', [object] $Body = $null) {
    $params = @{ Uri = "$BaseUrl$Path"; Method = $Method; WebSession = $script:Session; SkipHttpErrorCheck = $true; TimeoutSec = 30 }
    if ($null -ne $Body) {
        $params.Body = ($Body | ConvertTo-Json -Depth 6 -Compress)
        $params.ContentType = 'application/json'
    }
    $response = Invoke-WebRequest @params
    # `application/problem+json` 不带 charset 时 PS 会把 Content 当字节数组，
    # 直接 ConvertFrom-Json 会静默失败、把"带 code 的 404"误判成"裸 404"。
    $content = $response.Content
    $text = if ($content -is [byte[]]) { [Text.Encoding]::UTF8.GetString($content) } else { [string]$content }
    $json = $null
    try { $json = $text | ConvertFrom-Json } catch { }
    return [pscustomobject]@{ Status = [int]$response.StatusCode; Code = $json.code; Body = $text }
}

function Assert-Probe([string] $CaseName, [string] $Label, $Probe) {
    if ($Probe.Status -ge 500) {
        [void]$script:Findings.Add("$CaseName ${Label}: 5xx（$($Probe.Status)）$(($Probe.Body -replace '\s+', ' ').Substring(0, [Math]::Min(160, $Probe.Body.Length)))")
        Write-Host "    [FAIL] $Label → $($Probe.Status)" -ForegroundColor Red
        return 'fail'
    }
    if ($Probe.Status -ge 400 -and [string]::IsNullOrWhiteSpace([string]$Probe.Code)) {
        # 404 不带 code 是既有约定：权限不足与不存在都回 404，避免泄露"这条记录存在"。
        # 它不构成白屏也不构成 5xx，记观察项；其余 4xx 没码才是真问题（前端只能说"请求失败"）。
        if ($Probe.Status -eq 404) {
            $script:Infos.Add("$CaseName ${Label}: 404 无 code（既有约定：不存在/无权限同码）")
            Write-Host "    [info] $Label → 404（无 code，按约定）" -ForegroundColor DarkYellow
            return 'pass'
        }
        [void]$script:Findings.Add("$CaseName ${Label}: $($Probe.Status) 但没有 problem.code")
        Write-Host "    [FAIL] $Label → $($Probe.Status)（无 code）" -ForegroundColor Red
        return 'fail'
    }
    Write-Host "    [ok]   $Label → $($Probe.Status) $($Probe.Code)" -ForegroundColor DarkGray
    return 'pass'
}

if ($Cleanup) {
    Remove-FuzzModules
    Write-Host '已清场（990001–990099 号段）'
    exit 0
}

# ---------- 登录 ----------
try {
    $null = Invoke-RestMethod -Method Post -Uri "$BaseUrl/api/v1/auth/login" `
        -Body (@{ userId = 'admin'; password = 'admin' } | ConvertTo-Json -Compress) `
        -ContentType 'application/json' -SessionVariable script:Session -TimeoutSec 30
} catch {
    Write-Output "FAIL 登录失败：$($_.Exception.Message)"
    exit 1
}

<#
  用例表：每项是一段"在克隆完成后执行"的 SQL（只动模块级配置）＋说明。
  期望：表单定义能打开（或带 code 拒绝）、建单带 code 拒、绝不 5xx。
#>
$cases = @(
    @{
        Name = 'C1'; Desc = 'FILTER 引用不存在的列'
        Mutation = "UPDATE dbo.MODULES SET FILTER = N'[ZZ_NO_SUCH_COLUMN]=1' WHERE M_IDX = @new;"
    },
    @{
        Name = 'C2'; Desc = '明细门禁指向不存在的字段'
        Mutation = "UPDATE dbo.MODULES SET DETAIL_TABLE = N'COP_ORDER_D', DETAIL_NO_FIELDS = N'ZZ_GHOST_FIELD' WHERE M_IDX = @new;"
    },
    @{
        Name = 'C3'; Desc = '主表指向无主键的视图'
        Mutation = "UPDATE dbo.MODULES SET MASTER_TABLE = N'V_PUR_DUE_M' WHERE M_IDX = @new;"
    },
    @{
        Name = 'C4'; Desc = '承载页地址不合法'
        Mutation = "UPDATE dbo.MODULES SET M_URL = N'/no-such-page' WHERE M_IDX = @new;"
    },
    # 以下是"**能通过发布校验**的极端配置"：发布成功之后，表单渲染与建单才是真正被 fuzz 的路径。
    # 前四个用例都被校验挡在门外，只证明了"校验器不会 500"，没证明"渲染/写入不会 500"。
    @{
        Name = 'C5'; Desc = '主表有主键但零字段元数据（ATTACHMENT）'
        Mutation = "UPDATE dbo.MODULES SET MASTER_TABLE = N'ATTACHMENT', DETAIL_TABLE = NULL, DETAIL_NO_FIELDS = NULL WHERE M_IDX = @new;"
    },
    @{
        Name = 'C6'; Desc = '过滤条件永不成立（发布的模块一条都录不进）'
        Mutation = "UPDATE dbo.MODULES SET FILTER = N'[CLIENT_ID]=N''ZZ_NO_SUCH_CLIENT''' WHERE M_IDX = @new;"
    },
    @{
        Name = 'C7'; Desc = '统一表单只读（禁用编辑位）'
        Mutation = "UPDATE dbo.MODULES SET UNIFIED_FORM_EDIT_ENABLED = 0 WHERE M_IDX = @new;"
    },
    @{
        # 这条是"**能发布成功**"的极端配置：DETAIL_NO_FIELDS 指向真实存在的字段，
        # 但该字段在新增时永远是空的（客户资料的新增表单不会预填 CLIENT_ID）——
        # 于是"加明细"的前置条件永远不满足。要验的是：发布后的渲染与建单给的是
        # 结构化 400，而不是 500 或白屏。
        Name = 'C8'; Desc = '明细前置字段永不为空但新增时必为空'
        Mutation = "UPDATE dbo.MODULES SET DETAIL_NO_FIELDS = N'CLIENT_ID' WHERE M_IDX = @new;"
    }
)
if ($Case) { $cases = @($cases | Where-Object { $_.Name -eq $Case }) }
if ($cases.Count -eq 0) { Write-Output "FAIL 没有匹配的用例：$Case"; exit 1 }

$failCount = 0
$index = 0
try {
    Remove-FuzzModules
    foreach ($item in $cases) {
        $index++
        $moduleId = $script:RangeStart + $index
        Write-Host "`n[$($item.Name)] $($item.Desc)（克隆 $Template → $moduleId）"
        $failed = $false
        try {
            New-FuzzModule -NewId $moduleId -Desc "$($item.Name) $($item.Desc)" -Mutation $item.Mutation

            # 主探针：发布校验。它才是"解析配置"的入口——FILTER/版式/校验规则都在这里被读。
            # 期望：200 + 结构化结论（passed），失败项必须带 check.code；绝不 5xx。
            $validate = Invoke-Probe '/api/v1/workbench-definitions/validate' 'POST' @{ moduleId = $moduleId }
            if ((Assert-Probe $item.Name 'validate' $validate) -eq 'fail') { $failed = $true; }
            elseif ($validate.Status -eq 200) {
                $parsed = $null
                try { $parsed = $validate.Body | ConvertFrom-Json } catch { }
                if ($null -eq $parsed -or $null -eq $parsed.passed) {
                    [void]$script:Findings.Add("$($item.Name) validate 返回 200 但不是结构化结论（缺 passed）")
                    $failed = $true
                } elseif (-not $parsed.passed) {
                    $reasons = @($parsed.checks | Where-Object { -not $_.passed } | ForEach-Object { $_.code })
                    if ($reasons.Count -eq 0 -or @($reasons | Where-Object { [string]::IsNullOrWhiteSpace($_) }).Count -gt 0) {
                        [void]$script:Findings.Add("$($item.Name) validate 判未通过但没有给出 check.code（用户不知道为什么）")
                        $failed = $true
                    } else {
                        Write-Host "    [ok]   validate 拒绝并给出原因码：$($reasons -join ', ')" -ForegroundColor DarkGray
                    }
                } else {
                    # 校验通过 → 发布。这一步把"极端配置"真正落到快照上，
                    # 之后表单渲染/建单才会读到它——这才是本轮要 fuzz 的路径。
                    $publish = Invoke-Probe '/api/v1/workbench-definitions/publish' 'POST' @{ moduleIds = @($moduleId) }
                    if ((Assert-Probe $item.Name 'publish' $publish) -eq 'fail') { $failed = $true }
                    $published = $false
                    if ($publish.Status -eq 200) {
                        $parsedPublish = $null
                        try { $parsedPublish = $publish.Body | ConvertFrom-Json } catch { }
                        $entry = @($parsedPublish) | Where-Object { $_.moduleId -eq $moduleId } | Select-Object -First 1
                        $published = [bool]($entry.published)
                        if (-not $published) {
                            $codes = @($entry.checks | Where-Object { -not $_.passed } | ForEach-Object { $_.code })
                            if ($codes.Count -eq 0 -or @($codes | Where-Object { [string]::IsNullOrWhiteSpace($_) }).Count -gt 0) {
                                [void]$script:Findings.Add("$($item.Name) publish 判未通过但没有给出 check.code（配置者不知道为什么）")
                                $failed = $true
                                Write-Host '    [FAIL] publish 拒绝但无原因码' -ForegroundColor Red
                            } else {
                                Write-Host "    [ok]   publish 拒绝并给出原因码：$($codes -join ', ')" -ForegroundColor DarkGray
                            }
                        }
                    }
                    if ($published) {
                        # 1) 表单定义必须能打开（0 字段也算"能打开"），不能 5xx
                        $form = Invoke-Probe "/api/v1/document-workbench/$moduleId/form-definition?mode=new"
                        if ((Assert-Probe $item.Name 'form-definition' $form) -eq 'fail') { $failed = $true }
                        if ($form.Status -eq 200) {
                            $formJson = $null
                            try { $formJson = $form.Body | ConvertFrom-Json } catch { }
                            $fieldCount = @($formJson.masterFields).Count
                            Write-Host "    [info] 表单字段数=$fieldCount" -ForegroundColor DarkGray
                            # 2) 建单：空载荷要么明确拒（带 code），要么被接受（那就清理）；绝不 5xx
                            $create = Invoke-Probe "/api/v1/document-workbench/$moduleId/record" 'POST' @{ values = @{}; idempotencyKey = "fuzz-$moduleId-$([DateTime]::UtcNow.Ticks)" }
                            if ((Assert-Probe $item.Name 'create(empty)' $create) -eq 'fail') { $failed = $true }
                            if ($create.Status -eq 200) {
                                [void]$script:Findings.Add("$($item.Name) 空载荷建单被接受（必填全放空了）")
                            }
                            # 3) 读回不存在的键：结构化 404 即可，不能 5xx
                            $read = Invoke-Probe "/api/v1/document-workbench/$moduleId/record?key=%5B%22ZZFUZZ%22%5D"
                            if ((Assert-Probe $item.Name 'read' $read) -eq 'fail') { $failed = $true }
                        }
                    }
                }
            }
        } catch {
            [void]$script:Findings.Add("$($item.Name) 执行异常：$($_.Exception.Message)")
            $failed = $true
        } finally {
            if (-not ($failed -and $KeepFailed)) { Remove-FuzzModules }
        }
        if ($failed) { $failCount++ }
    }
} finally {
    Remove-FuzzModules
}

Write-Host ''
if ($script:Findings.Count -gt 0) {
    Write-Output "FAIL 合计 $($script:Findings.Count)："
    $script:Findings | ForEach-Object { Write-Output "  $_" }
    exit 1
}
Write-Output "PASS D 层：$($cases.Count) 个极端配置均未出现 5xx / 无码拒绝，克隆模块已清理"
exit 0
