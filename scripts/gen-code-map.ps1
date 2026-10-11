#Requires -Version 7.0
<#
.SYNOPSIS
    代码地图：从源码生成「后端路由」与「前端页面」两张表，并守住它们与源码一致。

.DESCRIPTION
    回答的是同一个问题的另一半：`impact.ps1` 由"改了哪个文件"推影响面，本脚本由"能力名/路径"反查入口符号——
    某个接口在哪个控制器、某个页面在哪个组件、它们各归哪个权限模块。

    两张表都是**确定性提取**，不靠人维护：

      · **后端**：`EOS.API/Controllers/*.cs` 的类级 `[Route("api/v1/…")]` + 方法级 `[HttpGet/Post/Put/Delete/Patch("…")]`，
        拼出完整路径与处理函数名；
      · **前端**：`EOS.Web/src/app/workspaceRoutes.tsx` 的 `path:` 与 `element:`，以及包住它的
        `RequirePermission permission={moduleReadPermission(N)}`——即该页面的读权限模块号。

    刻意不做的两件事：
      · **不猜权限模块到后端**：后端的模块号是运行时从路由参数/请求体里取的，属性上没有，写出来只能是假的；
      · **不做符号级调用图**：那是 LSP 的活（定义/引用/实现），这里只做"路径 ↔ 入口 ↔ 文件"的地图。

    `-Check`（CI 用这条）做三件事：台账文件存在、指纹与源码一致、**行数与源码里的路由条数相等**——
    最后一条是防"解析器悄悄漏行"的那道关（只比指纹的话，少解析一半也算"一致"）。

.PARAMETER OutFile
    地图落盘路径（相对仓库根），默认 `docs/guide/_index/routes.md`。

.PARAMETER Check
    只校验、不写文件；不成立即退出码 1。

.PARAMETER Json
    打印两张表的结构化数据（供 AI 助手或其它脚本消费），不写文件。

.PARAMETER SelfTest
    正反自检：用合成源码驱动两个解析器，含换行/多行属性等写法变体。

.EXAMPLE
    pwsh scripts/gen-code-map.ps1            # 重新生成地图
    pwsh scripts/gen-code-map.ps1 -Check     # 校验（CI 用这条）
    pwsh scripts/gen-code-map.ps1 -Json      # 机器可读
    pwsh scripts/gen-code-map.ps1 -SelfTest
#>
[CmdletBinding()]
param(
    [string] $Root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path,
    [string] $OutFile = 'docs/guide/_index/routes.md',
    [switch] $Check,
    [switch] $Json,
    [switch] $SelfTest
)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8

$ControllersDir = 'EOS.API/Controllers'
$RoutesFile = 'EOS.Web/src/app/workspaceRoutes.tsx'

# ---------- 纯函数：后端路由解析 ----------
function Get-BackendRoutes {
    <#
      输入一个控制器源码字符串，输出 @( @{ Method; Path; Handler } ) —— 不含文件名（由调用方补）。
      类级 `[Route("api/v1/x")]` + 方法级 `[HttpGet("y")]` 拼成 `api/v1/x/y`；方法级不带模板时就用类路由。
      解析不到类路由的控制器返回空数组（由调用方按"未解析"计入行数核对）。
    #>
    param([Parameter(Mandatory)][AllowEmptyString()][string] $Text)

    $classRoute = [regex]::Match($Text, '\[[^\]]*\bRoute\("(?<r>[^"]+)"\)')
    if (-not $classRoute.Success) { return @() }
    $base = $classRoute.Groups['r'].Value.Trim('/')

    $routes = [System.Collections.Generic.List[object]]::new()
    $lines = $Text -split "`r?`n"
    for ($i = 0; $i -lt $lines.Count; $i++) {
        $m = [regex]::Match($lines[$i], '\[Http(?<verb>Get|Post|Put|Delete|Patch|Head)(?:\(\s*"(?<tpl>[^"]*)"\s*\))?')
        if (-not $m.Success) { continue }

        $verb = $m.Groups['verb'].Value.ToUpperInvariant()
        $tpl = if ($m.Groups['tpl'].Success) { $m.Groups['tpl'].Value.Trim('/') } else { '' }
        $path = if ($tpl -eq '') { $base } else { "$base/$tpl" }

        # 处理函数名：属性行之后的第一处 public 方法签名
        $handler = ''
        for ($j = $i; $j -lt [Math]::Min($i + 8, $lines.Count); $j++) {
            $sig = [regex]::Match($lines[$j], 'public\s+(?:async\s+)?[\w<>,\[\]\?\.]+\s+(?<name>\w+)\s*\(')
            if ($sig.Success) { $handler = $sig.Groups['name'].Value; break }
        }

        $routes.Add([pscustomobject]@{ Verb = $verb; Path = $path; Handler = $handler })
    }
    return @($routes)
}

# ---------- 纯函数：前端路由解析 ----------
function Get-FrontendRoutes {
    <#
      输入 workspaceRoutes.tsx 的源码，输出 @( @{ Path; Element; ModuleId } )。
      权限模块来自包住该路由的 `RequirePermission permission={moduleReadPermission(N)}`：
      本表的写法是父级一行 `{ element: <RequirePermission …/>, children: [`，子路由若干行，再以 `] },` 收尾，
      故按"最近一次打开的权限作用域"归属，遇到收尾行即清空。
    #>
    param([Parameter(Mandatory)][AllowEmptyString()][string] $Text)

    $rows = [System.Collections.Generic.List[object]]::new()
    $moduleId = ''
    $scopeOpen = $false

    foreach ($line in ($Text -split "`r?`n")) {
        if ($line -match 'RequirePermission\s+permission=\{moduleReadPermission\((?<m>\d+)\)\}') {
            $moduleId = $Matches['m']
            $scopeOpen = $line -match 'children\s*:\s*\['
            if ($line -match "path:\s*'(?<p>[^']*)'.*element:\s*.*?<(?<el>\w+)") {
                $rows.Add([pscustomobject]@{ Path = $Matches['p']; Element = $Matches['el']; ModuleId = $moduleId })
            }
            continue
        }
        if ($scopeOpen -and $line -match '^\s*\]\s*\},?\s*$') { $scopeOpen = $false; $moduleId = ''; continue }

        if ($line -match "path:\s*'(?<p>[^']*)'") {
            $p = $Matches['p']
            $el = ''
            $elMatch = [regex]::Match($line, 'element:\s*.*?<(?<el>\w+)')
            if ($elMatch.Success) { $el = $elMatch.Groups['el'].Value }
            $rows.Add([pscustomobject]@{ Path = $p; Element = $el; ModuleId = $(if ($scopeOpen) { $moduleId } else { '' }) })
        }
    }
    return @($rows)
}

# ---------- 纯函数：指纹 ----------
function Get-MapFingerprint {
    <#
      只覆盖"地图语义"（路径 / 处理函数 / 组件 / 权限模块），不含任何时间或行号——
      否则无关提交就会让 CI 乱红。行数核对是另一道独立的关（见 -Check）。
    #>
    param(
        [object[]] $Backend,
        [object[]] $Frontend,
        [int] $BackendSourceCount,
        [int] $FrontendSourceCount
    )

    $sb = [System.Text.StringBuilder]::new()
    foreach ($r in ($Backend | Sort-Object Path, Verb)) {
        $null = $sb.AppendLine(@($r.Verb, $r.Path, $r.Handler, $r.File) -join '|')
    }
    foreach ($r in ($Frontend | Sort-Object Path)) {
        $null = $sb.AppendLine(@($r.Path, $r.Element, $r.ModuleId) -join '|')
    }
    $null = $sb.AppendLine("COUNTS:$BackendSourceCount/$FrontendSourceCount")

    $bytes = [System.Text.Encoding]::UTF8.GetBytes($sb.ToString())
    $hash = [System.Security.Cryptography.SHA256]::HashData($bytes)
    return ([System.BitConverter]::ToString($hash) -replace '-', '').Substring(0, 16).ToLowerInvariant()
}

# ---------- 自检 ----------
if ($SelfTest) {
    $cs = @'
using Microsoft.AspNetCore.Mvc;

/// <summary>合成控制器。</summary>
[ApiController, Authorize, Route("api/v1/chooser")]
public sealed class ChooserController(ChooserRepository repository) : ControllerBase
{
    [HttpPost("query")]
    public async Task<IActionResult> Query(UnifiedChooserQueryRequest request, CancellationToken token)
        => Ok();

    [HttpGet]
    public Task<IActionResult> Status() => Task.FromResult<IActionResult>(Ok());

    [HttpDelete("{id:int}")]
    public IActionResult Delete(int id) => Ok();
}
'@
    $backend = @(Get-BackendRoutes -Text $cs)
    if ($backend.Count -ne 3) { throw "自检失败：应解析出 3 条后端路由，实际 $($backend.Count)" }
    $byPath = @{}
    foreach ($r in $backend) { $byPath["$($r.Verb) $($r.Path)"] = $r.Handler }
    if ($byPath['POST api/v1/chooser/query'] -ne 'Query') { throw "自检失败：方法级模板拼接不对：$($byPath.Keys -join ' / ')" }
    if ($byPath['GET api/v1/chooser'] -ne 'Status') { throw '自检失败：方法级无模板时应回落到类路由' }
    if ($byPath['DELETE api/v1/chooser/{id:int}'] -ne 'Delete') { throw '自检失败：路径参数模板拼接不对' }
    if (@(Get-BackendRoutes -Text 'public class NoRouteController : ControllerBase { }').Count -ne 0) {
        throw '自检失败：没有类级 Route 的控制器不应产出路由'
    }
    Write-Host '  [PASS] 后端自检：模板拼接 / 回落 / 无类路由三种情形'

    $tsx = @'
export const WORKSPACE_ROUTES: RouteObject[] = [
  { index: true, element: <Navigate to="/dashboard" replace /> },
  { path: 'dashboard', element: withSuspense(<DashboardPage />) },
  { element: <RequirePermission permission={moduleReadPermission(2302)} />, children: [
    { path: 'admin/tables', element: withSuspense(<TableAdminPage />) },
    { path: 'admin/tables/:tableId/fields', element: withSuspense(<FieldAdminRoute />) },
  ] },
  { path: 'report/:reportId', element: withSuspense(<ReportIdentityRoute />) },
]
'@
    $frontend = @(Get-FrontendRoutes -Text $tsx)
    $paths = @($frontend | Select-Object -ExpandProperty Path)
    foreach ($need in 'dashboard', 'admin/tables', 'admin/tables/:tableId/fields', 'report/:reportId') {
        if ($paths -notcontains $need) { throw "自检失败：应解析出前端路由 $need，实际 $($paths -join ' / ')" }
    }
    $tables = @($frontend | Where-Object Path -eq 'admin/tables')[0]
    if ($tables.ModuleId -ne '2302') { throw "自检失败：children 内路由应继承权限模块 2302，实际「$($tables.ModuleId)」" }
    $report = @($frontend | Where-Object Path -eq 'report/:reportId')[0]
    if ($report.ModuleId -ne '') { throw '自检失败：作用域外的路由不该带权限模块' }
    if ($report.Element -ne 'ReportIdentityRoute') { throw "自检失败：元素名解析不对：$($report.Element)" }
    Write-Host '  [PASS] 前端自检：作用域继承 / 作用域外不带模块 / 元素名'

    $csKey = Get-MapFingerprint -Backend $backend -Frontend @() -BackendSourceCount 3 -FrontendSourceCount 0
    $csKey2 = Get-MapFingerprint -Backend $backend -Frontend @() -BackendSourceCount 3 -FrontendSourceCount 0
    if ($csKey -ne $csKey2) { throw '自检失败：同一输入两次指纹不一致（CI 会随机红）' }
    $changed = Get-MapFingerprint -Backend $backend -Frontend @() -BackendSourceCount 4 -FrontendSourceCount 0
    if ($changed -eq $csKey) { throw '自检失败：源码路由条数变了指纹却没变（漏行不会被发现）' }
    Write-Host '  [PASS] 指纹自检：同输入同值、条数变则变'

    Write-Host '-- SELFTEST OK'
    exit 0
}

# ---------- 采集 ----------
$controllerDir = Join-Path $Root $ControllersDir
$routesPath = Join-Path $Root $RoutesFile
if (-not (Test-Path -LiteralPath $controllerDir)) { throw "找不到控制器目录：$controllerDir" }
if (-not (Test-Path -LiteralPath $routesPath)) { throw "找不到前端路由表：$routesPath" }

$backend = [System.Collections.Generic.List[object]]::new()
$controllersScanned = 0
$controllersWithoutRoute = [System.Collections.Generic.List[string]]::new()
foreach ($file in (Get-ChildItem -LiteralPath $controllerDir -File -Filter '*.cs' | Sort-Object Name)) {
    $text = Get-Content -LiteralPath $file.FullName -Raw -Encoding UTF8
    $parsed = @(Get-BackendRoutes -Text $text)
    if ($parsed.Count -eq 0) { $controllersWithoutRoute.Add($file.Name); continue }
    $controllersScanned++
    foreach ($r in $parsed) {
        $backend.Add([pscustomobject]@{ Verb = $r.Verb; Path = $r.Path; Handler = $r.Handler; File = $file.Name })
    }
}

$frontendText = Get-Content -LiteralPath $routesPath -Raw -Encoding UTF8
$frontend = @(Get-FrontendRoutes -Text $frontendText)

# 源码侧条数：与解析结果核对，防"解析器悄悄漏行"（只比指纹会漏掉这种退化）
$backendSourceCount = 0
foreach ($file in (Get-ChildItem -LiteralPath $controllerDir -File -Filter '*.cs')) {
    $text = Get-Content -LiteralPath $file.FullName -Raw -Encoding UTF8
    $backendSourceCount += @([regex]::Matches($text, '\[Http(Get|Post|Put|Delete|Patch|Head)')).Count
}
$frontendSourceCount = @([regex]::Matches($frontendText, "path:\s*'")).Count

# ---------- 渲染 ----------
function Format-Cell {
    param([string] $Value, [int] $Max = 120)
    $v = ($Value -replace '\s+', ' ').Trim() -replace '\|', '\|'
    if ($v.Length -gt $Max) { $v = $v.Substring(0, $Max - 1) + '…' }
    return $v
}

function New-MapLines {
    param(
        [object[]] $Backend,
        [object[]] $Frontend,
        [int] $BackendSourceCount,
        [int] $FrontendSourceCount,
        [string[]] $WithoutRoute
    )

    $lines = [System.Collections.Generic.List[string]]::new()
    $lines.Add('<!-- code-map-fingerprint: ' + (Get-MapFingerprint -Backend $Backend -Frontend $Frontend -BackendSourceCount $BackendSourceCount -FrontendSourceCount $FrontendSourceCount) + ' -->')
    $lines.Add('# 代码地图（路由与页面）')
    $lines.Add('')
    $lines.Add('> 本文件由 [`scripts/gen-code-map.ps1`](../../../scripts/gen-code-map.ps1) **生成**，不要手改；')
    $lines.Add('> `pwsh scripts/gen-code-map.ps1 -Check` 在 CI 里校验它与源码一致（改了控制器或前端路由表后没重新生成即红）。')
    $lines.Add('>')
    $lines.Add('> 两张表都是**确定性提取**：后端来自 `EOS.API/Controllers/*.cs` 的类级 `Route` + 方法级 `[Http*]`；')
    $lines.Add('> 前端来自 `EOS.Web/src/app/workspaceRoutes.tsx` 的 `path:` / `element:` 与包住它的 `RequirePermission`。')
    $lines.Add('> **权限模块号只在前端表里**——后端模块号是运行时从路由参数或请求体取的，属性上没有，猜出来只会是假的。')
    $lines.Add('> 需要"谁调用了谁""改这里会动到哪"时用编辑器/LSP 的引用与实现导航，本表不管调用关系。')
    $lines.Add('')
    $lines.Add('## 一、后端路由')
    $lines.Add('')
    $lines.Add(('- 控制器 {0} 个、方法级路由属性 {1} 处、解析出 {2} 条（两者必须相等，否则 `-Check` 报 FAIL）。' -f `
            $controllersScanned, $BackendSourceCount, $Backend.Count))
    if ($WithoutRoute.Count -gt 0) {
        $lines.Add('- 未声明类级 `Route` 的控制器（不产出路由）：' + (@($WithoutRoute | ForEach-Object { '`' + $_ + '`' }) -join '、'))
    }
    $lines.Add('')
    $lines.Add('| 方法 | 路径 | 处理函数 | 文件 |')
    $lines.Add('|---|---|---|---|')
    foreach ($r in ($Backend | Sort-Object Path, Verb)) {
        $lines.Add(('| {0} | `{1}` | {2} | `{3}` |' -f $r.Verb, $r.Path, $r.Handler, $r.File))
    }
    $lines.Add('')
    $lines.Add('## 二、前端页面路由')
    $lines.Add('')
    $lines.Add(('- 源文件 `path:` {0} 处、解析出 {1} 条（两者必须相等）。权限模块号来自包住该路由的' -f $FrontendSourceCount, $Frontend.Count))
    $lines.Add('  `RequirePermission permission={moduleReadPermission(N)}`；空表示该页面不需要模块读权限（如登录后即可见的助手页）。')
    $lines.Add('')
    $lines.Add('| 路径 | 页面 | 权限模块 |')
    $lines.Add('|---|---|---|')
    foreach ($r in ($Frontend | Sort-Object Path)) {
        $el = if ($r.Element -ne '') { '`' + $r.Element + '`' } else { '—' }
        $mod = if ($r.ModuleId -ne '') { $r.ModuleId } else { '—' }
        $lines.Add(('| `/{0}` | {1} | {2} |' -f $r.Path.TrimStart('/'), $el, $mod))
    }
    $lines.Add('')
    return @($lines)
}

$mapLines = New-MapLines -Backend $backend -Frontend $frontend -BackendSourceCount $backendSourceCount `
    -FrontendSourceCount $frontendSourceCount -WithoutRoute @($controllersWithoutRoute)
$mapContent = ($mapLines -join "`n") + "`n"
$fingerprint = Get-MapFingerprint -Backend $backend -Frontend $frontend -BackendSourceCount $backendSourceCount -FrontendSourceCount $frontendSourceCount
$outPath = Join-Path $Root $OutFile

if ($Json) {
    [pscustomobject]@{
        backend  = @($backend | Sort-Object Path, Verb)
        frontend = @($frontend | Sort-Object Path)
        counts   = [pscustomobject]@{
            backendParsed   = $backend.Count
            backendInSource = $backendSourceCount
            frontendParsed  = $frontend.Count
            frontendInSource = $frontendSourceCount
        }
        fingerprint = $fingerprint
    } | ConvertTo-Json -Depth 5
    exit 0
}

if ($Check) {
    $failures = [System.Collections.Generic.List[string]]::new()

    # 1) 解析完整：与源码里的路由条数逐一对齐（只比指纹会漏掉"解析器少解析一半"这种退化）
    if ($backend.Count -ne $backendSourceCount) {
        $failures.Add("后端路由解析不完整：Controllers 下 [Http*] 共 $backendSourceCount 处，只解析出 $($backend.Count) 条——解析器需要跟上源码写法")
    }
    if ($frontend.Count -ne $frontendSourceCount) {
        $failures.Add("前端路由解析不完整：workspaceRoutes.tsx 里 path: 共 $frontendSourceCount 处，只解析出 $($frontend.Count) 条——解析器需要跟上源码写法")
    }

    # 2) 地图与源码一致（指纹）
    if (-not (Test-Path -LiteralPath $outPath)) {
        $failures.Add("地图 $OutFile 不存在：跑一次 pwsh scripts/gen-code-map.ps1 生成它")
    } else {
        $diskText = Get-Content -LiteralPath $outPath -Raw -Encoding UTF8
        $fpMatch = [regex]::Match($diskText, 'code-map-fingerprint:\s*(?<fp>[0-9a-f]+)')
        $diskFp = if ($fpMatch.Success) { $fpMatch.Groups['fp'].Value } else { '' }
        if ($diskFp -eq '') {
            $failures.Add("地图 $OutFile 里没有 code-map-fingerprint：跑一次 pwsh scripts/gen-code-map.ps1 重新生成")
        } elseif ($diskFp -ne $fingerprint) {
            $failures.Add("地图 $OutFile 已落后于源码（指纹 $diskFp ≠ $fingerprint）：跑一次 pwsh scripts/gen-code-map.ps1 重新生成并提交")
        }
    }

    if ($failures.Count -gt 0) {
        Write-Host '== 代码地图门禁 ==' -ForegroundColor Cyan
        foreach ($line in $failures) { Write-Host "  [FAIL] $line" -ForegroundColor Red }
        Write-Host ("-- FAIL（后端 {0}/{1} 条，前端 {2}/{3} 条）" -f $backend.Count, $backendSourceCount, $frontend.Count, $frontendSourceCount)
        exit 1
    }
    Write-Host ("PASS 代码地图：后端 {0} 条路由（{1} 个控制器）、前端 {2} 条页面路由，与源码一致" -f `
            $backend.Count, $controllersScanned, $frontend.Count)
    exit 0
}

$outDir = Split-Path -Parent $outPath
if (-not (Test-Path -LiteralPath $outDir)) { $null = New-Item -ItemType Directory -Path $outDir -Force }
Set-Content -LiteralPath $outPath -Value $mapContent -Encoding utf8 -NoNewline

Write-Host '== 代码地图已生成 ==' -ForegroundColor Cyan
Write-Host "  文件：$OutFile"
Write-Host ("  后端路由 {0} 条（{1} 个控制器；[Http*] 属性 {2} 处）" -f $backend.Count, $controllersScanned, $backendSourceCount)
Write-Host ("  前端页面 {0} 条（path: {1} 处）" -f $frontend.Count, $frontendSourceCount)
if ($controllersWithoutRoute.Count -gt 0) {
    Write-Host ("  未声明类级 Route 的控制器：{0}" -f ($controllersWithoutRoute -join '、'))
}
exit 0
