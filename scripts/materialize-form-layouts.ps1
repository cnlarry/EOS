# 把「推导默认版式」落成真实的版式行（模块级版式退役字段级配置的前置数据搬运）。
#
# 背景：FIELDS 的字段级排布列（FORM_ORDER/FORM_TAB_NO/FORM_SPAN/FORM_NEW_LINE/FORM_CELL_GROUP/
# FORM_CELL_ROLE）与 MODULES 的页签列曾是**推导默认的输入**。退役这些列的前提是"每个模块都有版式行"，
# 否则删列后零配置模块的默认版式会退化为"全挤一个页签、半行宽、无复合格"。
# 该前置已由本脚本完成（261 个模块固化版式行），上述列**已退役**；本脚本保留作演练/复核手段，
# 但它不再从字段级配置推导——版式行一律由服务端设计态读法生成（见下方"安全设计"）。
#
# **安全设计（本脚本不做任何推导）**：
#   版式行由**服务端自己的设计态读法**生成（`GET /api/v1/admin/form-layout/{moduleId}`），
#   本脚本只做"读出来 → 原样回写"，不做任何字段级推导重写——谁推导谁落库，避免脚本与服务端
#   在备注类判定/排序/隐藏规则上出现分叉（那会把分叉结果永久固化进库里）。
#
# 用法（仓库根执行）：
#   pwsh scripts/materialize-form-layouts.ps1                 # 演练：只统计与复核转换，**不写库**
#   pwsh scripts/materialize-form-layouts.ps1 -Apply           # 真正落库（逐模块回写并复核）
#   pwsh scripts/materialize-form-layouts.ps1 -Apply -Limit 5 -Verbose
#
# 前置：EOS.API 已启动（含设计态端点）。幂等：每模块用固定幂等键，重跑只会重放不会重复写；
# 已有版式行的模块一律跳过（不覆盖人工定制）。

param(
    [string]$ApiUrl = 'http://localhost:5261',
    [switch]$Apply,
    [int[]]$ModuleIds = @(),
    [int]$Limit = 0,
    [switch]$Verbose
)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8
. (Join-Path $PSScriptRoot '..\EOS.API.Tests\AcceptanceCommon.ps1')

$session = New-EosSession -ApiUrl $ApiUrl

function Get-SnapshotModuleIds {
    $response = Invoke-EosApi -Method 'GET' -Path '/api/v1/workbench-definitions/staleness' -Session $session -ApiUrl $ApiUrl
    if ($response.Status -ne 200) { throw "取模块清单失败：HTTP $($response.Status)" }
    return @(@($response.Content) | ForEach-Object { [int]$_.moduleId } | Sort-Object -Unique)
}

<#
 * 把设计态转成保存载荷（**只搬运，不推导**）。
 * 返回 \(payload, rows)：rows 是用于回读复核的规范化行集合。
 #>
function ConvertTo-MaterializePayload([object]$state) {
    $master = @($state.master.layout | ForEach-Object {
            [ordered]@{
                key       = $_.key
                tabNo     = [int]$_.tabNo
                span      = [int]$_.span
                rowSpan   = [int]$_.rowSpan
                newLine   = [bool]$_.newLine
                sectionId = $_.sectionId
                cellGroup = $_.cellGroup
                cellRole  = [int]$_.cellRole
                hidden    = [bool]$_.hidden
            }
        })
    $detail = @($state.detail.layout | ForEach-Object {
            [ordered]@{ key = $_.key; hidden = [bool]$_.hidden }
        })
    $tabs = @($state.tabs | ForEach-Object { [ordered]@{ no = [int]$_.no; title = [string]$_.title } })
    # 既有数据里存在"字段指向的页签没在页签定义里"的情况（如 14997 的页签 2）：那些字段今天在界面上
    # 根本点不到。落库时把缺失页签补上（标题占位），否则保存会被"指向不存在的页签"拦下，
    # 且这些字段会以"不可达"的形态被永久固化。
    $known = @($tabs | ForEach-Object { $_.no })
    foreach ($row in $master) {
        if ($row.tabNo -gt 0 -and ($known -notcontains $row.tabNo)) {
            $tabs += [ordered]@{ no = [int]$row.tabNo; title = "页签 $($row.tabNo)" }
            $known += $row.tabNo
        }
    }
    $tabs = @($tabs | Sort-Object { $_.no })
    $payload = @{
        baseUpdatedAt  = $state.baseUpdatedAt
        idempotencyKey = "materialize-form-layout-$($state.moduleId)"
        tabs           = $tabs
        master         = $master
        detail         = $detail
    }
    return $payload
}

<# 回读复核用的规范化指纹：只看版式语义，不看标签与顺序号（顺序由服务端按提交次序重排）。 #>
function Get-LayoutFingerprint([object]$state) {
    $master = @($state.master.layout |
        ForEach-Object { '{0}|t{1}|s{2}|r{3}|n{4}|sec={5}|g={6}|c{7}|h{8}' -f `
                $_.key.ToUpperInvariant(), $_.tabNo, $_.span, $_.rowSpan, [int]$_.newLine,
            ($_.sectionId ?? ''), ($_.cellGroup ?? ''), $_.cellRole, [int]$_.hidden })
    $detail = @($state.detail.layout | ForEach-Object { '{0}|h{1}' -f $_.key.ToUpperInvariant(), [int]$_.hidden })
    # 常驻页签的空标题在保存期会被规范化成"默认"，指纹里按同一口径归一，避免假差异
    $tabs = @($state.tabs | ForEach-Object {
            $title = if ($_.no -eq 1 -and [string]::IsNullOrEmpty($_.title)) { '默认' } else { $_.title }
            '{0}={1}' -f $_.no, $title
        })
    return @($master + '--' + $detail + '--' + $tabs)
}

$ids = if ($ModuleIds.Count -gt 0) { @($ModuleIds | Sort-Object -Unique) } else { Get-SnapshotModuleIds }
if ($Limit -gt 0) { $ids = @($ids | Select-Object -First $Limit) }

Write-Output ("== 表单版式数据搬运 {0} ==" -f $(if ($Apply) { '（**落库模式**）' } else { '（演练模式，不写库）' }))
Write-Output ("  目标模块数：{0}" -f $ids.Count)

$skipped = @()
$planned = @()
$applied = 0
$verified = 0
$failures = @()
$totalMaster = 0
$totalDetail = 0

foreach ($moduleId in $ids) {
    $stateResponse = Invoke-EosApi -Method 'GET' -Path "/api/v1/admin/form-layout/$moduleId" -Session $session -ApiUrl $ApiUrl
    if ($stateResponse.Status -ne 200) {
        $failures += "模块 $moduleId：读取设计态 HTTP $($stateResponse.Status)"
        continue
    }
    $state = $stateResponse.Content
    if ($state.master.customized -or $state.detail.customized) {
        $skipped += [pscustomobject]@{ ModuleId = $moduleId; Title = $state.title; Reason = '已有版式行' }
        continue
    }

    $payload = ConvertTo-MaterializePayload $state
    # 转换自检：载荷里的行集合必须与读到的设计态逐项一致（key/tab/span/rowSpan/newLine/分节/组/角色/隐藏）
    $sent = Get-LayoutFingerprint ([pscustomobject]@{
            moduleId = $moduleId
            tabs     = $payload.tabs
            master   = @{ layout = $payload.master }
            detail   = @{ layout = $payload.detail }
        })
    # 行集合按设计态比；页签允许"补齐缺失页签"这一处**有意修正**（否则被"指向不存在的页签"拦下），
    # 最终存进去的页签由下面的回读复核逐项核对。
    $read = Get-LayoutFingerprint ([pscustomobject]@{
            moduleId = $moduleId
            tabs     = $payload.tabs
            master   = @{ layout = $state.master.layout }
            detail   = @{ layout = $state.detail.layout }
        })
    if (($sent -join ';') -ne ($read -join ';')) {
        $failures += "模块 $moduleId：载荷与设计态不一致（转换自检失败，已跳过）"
        continue
    }

    $totalMaster += @($payload.master).Count
    $totalDetail += @($payload.detail).Count
    $planned += [pscustomobject]@{ ModuleId = $moduleId; Title = $state.title; Master = @($payload.master).Count; Detail = @($payload.detail).Count }

    if (-not $Apply) {
        if ($Verbose) {
            Write-Output ("  [PLAN] {0} {1} 主 {2} 行 / 明细 {3} 行" -f $moduleId, $state.title, @($payload.master).Count, @($payload.detail).Count)
        }
        continue
    }

    $save = Invoke-EosApi -Method 'PUT' -Path "/api/v1/admin/form-layout/$moduleId" -Body $payload `
        -Session $session -ApiUrl $ApiUrl
    if ($save.Status -ne 200) {
        $failures += "模块 $moduleId：保存 HTTP $($save.Status) $($save.Raw)"
        continue
    }
    $applied++

    # 回读复核：落库后的版式必须与写进去的一致（这条是"零观感变化"的机器判据）
    $after = Invoke-EosApi -Method 'GET' -Path "/api/v1/admin/form-layout/$moduleId" -Session $session -ApiUrl $ApiUrl
    if ($after.Status -ne 200) {
        $failures += "模块 $moduleId：回读 HTTP $($after.Status)"
        continue
    }
    if (($sent -join ';') -ne ((Get-LayoutFingerprint $after.Content) -join ';')) {
        $failures += "模块 $moduleId：落库后版式与提交值不一致"
        continue
    }
    $verified++
    if ($Verbose) {
        Write-Output ("  [DONE] {0} {1} → v{2}" -f $moduleId, $state.title, $save.Content.definitionVersion)
    }
}

Write-Output ''
Write-Output ("  计划搬运：{0} 个模块（主表 {1} 行 / 明细 {2} 行）" -f $planned.Count, $totalMaster, $totalDetail)
Write-Output ("  已有版式行跳过：{0} 个" -f $skipped.Count)
if ($Apply) { Write-Output ("  已写入并复核通过：{0} 个（写入 {1} 个）" -f $verified, $applied) }
if ($skipped.Count -gt 0 -and $Verbose) {
    foreach ($item in $skipped) { Write-Output ("  [SKIP] {0} {1}（{2}）" -f $item.ModuleId, $item.Title, $item.Reason) }
}

if ($failures.Count -gt 0) {
    Write-Output ''
    Write-Output ("FAIL 表单版式数据搬运有 {0} 项未通过：" -f $failures.Count)
    foreach ($item in $failures) { Write-Output ("  - {0}" -f $item) }
    exit 1
}

if ($Apply) {
    Write-Output 'PASS 表单版式数据搬运完成（逐模块回读复核一致；快照随保存即时重发布）。'
}
else {
    Write-Output 'PASS 演练通过：转换自检逐模块一致，未写库（落库请加 -Apply）。'
}
exit 0
