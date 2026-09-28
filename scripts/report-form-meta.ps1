#Requires -Version 7.0
<#
.SYNOPSIS
统一表单切片模块字段矩阵报告：按模块输出主/明细表字段的
必填 / 默认值 / FORM_OPTIONS / 选择器 / 隐藏必填 / 只读联动 清单，供实施顾问逐表确认。
.DESCRIPTION
数据源为运行中 EOS.API 的 form-definition（mode=new），反映 FIELDS 元数据经
FormFieldSelector 过滤后的实际表单定义。输出 Markdown 到指定目录。
.EXAMPLE
.\scripts\report-form-meta.ps1 -ModuleIds @(1209,1305,1414,1501,1901,1905,180202)
#>
param(
    [int[]]$ModuleIds = @(1209,1305,1414,1501,1901,1905,180202),
    [string]$ApiUrl = 'http://localhost:5261',
    [string]$OutDir = ''
)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
if (-not $OutDir) { $OutDir = Join-Path $root 'logs\goal\phase1' }
New-Item -ItemType Directory -Path $OutDir -Force | Out-Null
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'

$session = New-Object Microsoft.PowerShell.Commands.WebRequestSession
$login = Invoke-RestMethod -Uri "$ApiUrl/api/v1/auth/login" -Method Post -ContentType 'application/json' `
    -Body (@{ userId = 'admin'; password = 'admin'; rememberMe = $false } | ConvertTo-Json) -WebSession $session

function Get-Form {
    param([string]$ModuleId)
    $r = Invoke-WebRequest -Uri "$ApiUrl/api/v1/document-workbench/$ModuleId/form-definition?mode=new" `
        -Method GET -WebSession $session -SkipHttpErrorCheck
    if ([int]$r.StatusCode -ne 200) {
        return @{ Status = [int]$r.StatusCode; Content = $null }
    }
    return @{ Status = 200; Content = ($r.Content | ConvertFrom-Json) }
}

function Get-FieldRow {
    param($F, [string]$Table)
    $choosers = @($F.choosers | Where-Object { $_.active }) | ForEach-Object {
        "$($_.table)$(if ($_.moduleId) { "[$($_.moduleId)]" })"
    }
    $opts = @($F.options) | ForEach-Object { "$($_.value)=$($_.label)" }
    $flags = @()
    if ($F.isPrimaryKey) { $flags += 'PK' }
    if ($F.isAutoIncrement) { $flags += 'AUTO' }
    if ($F.isVirtual) { $flags += 'VIRTUAL' }
    if ($F.serverFilled) { $flags += 'SERVER' }
    if ($F.displayOnly) { $flags += 'DISPLAY' }
    if ($F.isCost) { $flags += 'COST' }
    if ($F.isSecrecy) { $flags += 'SECRECY' }
    return [pscustomobject]@{
        表         = $Table
        字段       = $F.key
        标题       = $F.label
        类型       = $F.dataType
        必填       = if ($F.isRequired) { '是' } else { '' }
        可见       = if ($F.isVisible) { '是' } else { '否' }
        只读       = if ($F.isReadonly) { '是' } else { '' }
        默认值     = [string]$F.defaultValue
        FORM_OPTIONS = ($opts -join '; ')
        选择器     = ($choosers -join '; ')
        正则       = [string]$F.regex
        页签       = $F.tabNo
        顺序       = $F.formOrder
        标志       = ($flags -join ',')
    }
}

$lines = [System.Collections.Generic.List[string]]::new()
$lines.Add("# 统一表单切片模块字段矩阵（$stamp）")
$lines.Add('')
$lines.Add("数据源：EOS.API form-definition（mode=new），API=$ApiUrl")
$lines.Add('')
$summary = [System.Collections.Generic.List[object]]::new()

foreach ($id in $ModuleIds) {
    $f = Get-Form -ModuleId ([string]$id)
    if ($f.Status -ne 200) {
        $lines.Add("## $id —— form-definition HTTP $($f.Status)")
        $lines.Add('')
        $summary.Add([pscustomobject]@{ ModuleId = $id; Title = ''; Master = ''; Detail = ''; Status = "HTTP$($f.Status)"; MasterFields = 0; DetailFields = 0 })
        continue
    }
    $c = $f.Content
    $rows = @()
    foreach ($mf in $c.masterFields) { $rows += Get-FieldRow -F $mf -Table $c.masterTable }
    foreach ($df in $c.detailFields) { $rows += Get-FieldRow -F $df -Table $c.detailTable }

    $reqMaster = @($rows | Where-Object { $_.表 -eq $c.masterTable -and $_.必填 -eq '是' -and $_.可见 -eq '是' -and $_.标志 -notmatch 'SERVER' } | ForEach-Object { $_.字段 })
    $reqDetail = @($rows | Where-Object { $_.表 -ne $c.masterTable -and $_.必填 -eq '是' -and $_.可见 -eq '是' -and $_.标志 -notmatch 'SERVER' } | ForEach-Object { $_.字段 })
    $hiddenReq = @($rows | Where-Object { $_.必填 -eq '是' -and $_.可见 -eq '否' } | ForEach-Object { "$($_.表).$($_.字段)" })
    $withDefault = @($rows | Where-Object { $_.默认值 -ne '' } | ForEach-Object { "$($_.表).$($_.字段)=$($_.默认值)" })
    $withOptions = @($rows | Where-Object { $_.FORM_OPTIONS -ne '' } | ForEach-Object { "$($_.表).$($_.字段)[$($_.FORM_OPTIONS)]" })
    $withChooser = @($rows | Where-Object { $_.选择器 -ne '' } | ForEach-Object { "$($_.表).$($_.字段)->$($_.选择器)" })
    $readonlyVisible = @($rows | Where-Object { $_.只读 -eq '是' -and $_.可见 -eq '是' -and $_.标志 -notmatch 'SERVER' -and $_.必填 -ne '是' } | ForEach-Object { "$($_.表).$($_.字段)" })

    $lines.Add("## $id $($c.title)")
    $lines.Add('')
    $tabs = @($c.tabs) | ForEach-Object { "$($_.no):$($_.title)" }
    $lines.Add("- 主表：$($c.masterTable)｜明细表：$($c.detailTable)｜hasAdd：$($c.hasAdd)｜hasEdit：$($c.hasEdit)｜detailNoFields：$($c.detailNoFields)｜hasWorkflow：$($c.hasWorkflow)｜ifCopy：$($c.ifCopy)")
    $lines.Add("- 页签（$(@($c.tabs).Count) 个）：$($tabs -join '；')｜表单列数：$($c.columns)")
    $lines.Add("- 主表必填可见字段（$($reqMaster.Count)）：$($reqMaster -join '、')")
    $lines.Add("- 明细必填可见字段（$($reqDetail.Count)）：$($reqDetail -join '、')")
    $lines.Add("- 隐藏必填字段（$($hiddenReq.Count)）：$($hiddenReq -join '、')")
    $lines.Add("- 带默认值字段（$($withDefault.Count)）：$($withDefault -join '；')")
    $lines.Add("- 带 FORM_OPTIONS 字段（$($withOptions.Count)）：$($withOptions -join '；')")
    $lines.Add("- 带选择器字段（$($withChooser.Count)）：$($withChooser -join '；')")
    $lines.Add("- 可见只读非必填字段（$($readonlyVisible.Count)）：$($readonlyVisible -join '、')")
    $lines.Add('')
    $lines.Add('| 表 | 字段 | 标题 | 类型 | 必填 | 可见 | 只读 | 默认值 | FORM_OPTIONS | 选择器 | 正则 | 页签 | 顺序 | 标志 |')
    $lines.Add('|---|---|---|---|---|---|---|---|---|---|---|---|---|---|')
    foreach ($r in $rows) {
        $esc = { param($v) ([string]$v) -replace '\|', '／' -replace "`r?`n", ' ' }
        $lines.Add("| $($r.表) | $($r.字段) | $($esc.Invoke($r.标题)) | $($r.类型) | $($r.必填) | $($r.可见) | $($r.只读) | $($esc.Invoke($r.默认值)) | $($esc.Invoke($r.FORM_OPTIONS)) | $($esc.Invoke($r.选择器)) | $($esc.Invoke($r.正则)) | $($r.页签) | $($r.顺序) | $($r.标志) |")
    }
    $lines.Add('')
    $summary.Add([pscustomobject]@{
        ModuleId = $id; Title = $c.title; Master = $c.masterTable; Detail = $c.detailTable
        Status = 'OK'; MasterFields = @($c.masterFields).Count; DetailFields = @($c.detailFields).Count
        ReqMaster = $reqMaster.Count; ReqDetail = $reqDetail.Count; HiddenReq = $hiddenReq.Count
        Defaults = $withDefault.Count; Options = $withOptions.Count; Choosers = $withChooser.Count
    })
}

$mdPath = Join-Path $OutDir "slice-form-meta-$stamp.md"
$lines -join "`n" | Set-Content -LiteralPath $mdPath -Encoding utf8
$summary | Export-Csv -LiteralPath (Join-Path $OutDir "slice-form-meta-$stamp.csv") -NoTypeInformation -Encoding UTF8
$summary | Format-Table ModuleId, Title, Master, Detail, MasterFields, DetailFields, ReqMaster, ReqDetail, HiddenReq, Defaults, Options, Choosers -AutoSize
Write-Host "报告：$mdPath" -ForegroundColor Cyan
