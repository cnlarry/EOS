<#
.SYNOPSIS
    E2E 脚本卫生门禁：错误体解码必须齐、硬编码日期只能减少不能增加。

.DESCRIPTION
    两类"跑的时候看不出来、过一阵才炸"的脚本缺陷：

    ① **错误体未解码**：`application/problem+json`（4xx/5xx）在 PowerShell 里是 byte[]，
       直接管道给 `ConvertFrom-Json` 会逐字节变成数字数组 ⇒ 错误的 code/message 全丢，
       断言退化成"没拦住"，排查时看不到任何原因。要求：**零容忍**。
    ② **硬编码日期**：脚本里写死 `'2026-08-09'` 这类字面量，遇上日期窗口/有效期/排序规则
       会在写完之后某一天集体失效（1406「送货日期不能小于建立日期 30 天」就是这么炸的）。
       存量已登记为技术债，本门禁按**棘轮**执行：只许减不许增，防止边清边长。

.OUTPUTS
    PASS/FAIL + 计数。任一硬指标超标即 exit 1。

.EXAMPLE
    pwsh scripts/check-e2e-hygiene.ps1
#>
[CmdletBinding()]
param(
    [string] $TestsDir,
    # 硬编码日期字面量的存量基线（已清零：存量 15 个脚本 104 处全部改为相对运行当天）。新增即失败。
    [int] $DateLiteralBaseline = 0
)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8

if (-not $TestsDir) {
    $TestsDir = Join-Path (Split-Path -Parent $PSScriptRoot) 'EOS.API.Tests'
}
$TestsDir = (Resolve-Path -LiteralPath $TestsDir).Path

$scripts = Get-ChildItem -LiteralPath $TestsDir -Filter 'E2e*.ps1' -File |
    Where-Object { $_.Name -notin @('E2eSelfContained.ps1') }

# ① 错误体解码：有本地 Call-Api 的脚本必须出现 UTF8 解码
$undecoded = @()
foreach ($file in $scripts) {
    $text = Get-Content -LiteralPath $file.FullName -Raw -Encoding UTF8
    if ($text -match 'function Call-Api' -and $text -notmatch 'UTF8\.GetString') {
        $undecoded += $file.Name
    }
}

# ② 硬编码日期：统计 'YYYY-MM-DD 字面量
$dateHits = 0
$dateFiles = @{}
foreach ($file in $scripts) {
    $matches = Select-String -LiteralPath $file.FullName -Pattern "'20\d\d-\d\d-\d\d" -AllMatches
    if ($matches) {
        $count = ($matches | ForEach-Object { $_.Matches.Count } | Measure-Object -Sum).Sum
        $dateFiles[$file.Name] = $count
        $dateHits += $count
    }
}

$fail = $false

if ($undecoded.Count -gt 0) {
    Write-Output ("FAIL 未解码错误体的脚本 {0} 个：{1}" -f $undecoded.Count, ($undecoded -join ', '))
    $fail = $true
}
else {
    Write-Output ("PASS 错误体解码：{0} 个含 Call-Api 的脚本全部解码 byte[] 响应体" -f $scripts.Count)
}

if ($dateHits -gt $DateLiteralBaseline) {
    Write-Output ("FAIL 硬编码日期 {0} 处，超过基线 {1} 处（只许减不许增）：{2}" -f
        $dateHits, $DateLiteralBaseline,
        (($dateFiles.GetEnumerator() | Sort-Object Value -Descending | ForEach-Object { "$($_.Key)=$($_.Value)" }) -join ', '))
    $fail = $true
}
elseif ($dateHits -eq 0) {
    Write-Output 'PASS 硬编码日期：0 处（存量已清零；新增任何日期字面量即失败）'
}
else {
    Write-Output ("PASS 硬编码日期：{0} 处 / {1} 个脚本（基线 {2}，未增长）" -f
        $dateHits, $dateFiles.Count, $DateLiteralBaseline)
    if ($dateHits -lt $DateLiteralBaseline) {
        Write-Output ("NOTE 存量已低于基线，请把 -DateLiteralBaseline 下调到 {0}" -f $dateHits)
    }
}

exit ($fail ? 1 : 0)
