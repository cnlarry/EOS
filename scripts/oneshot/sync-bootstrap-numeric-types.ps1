# 一次性｜锚点：ADR-038 §5.3（bootstrap 与迁移双写）
<#
.SYNOPSIS
    一次性脚本：把建库基线里的数值列类型同步为语义族的 decimal。

.DESCRIPTION
    db/bootstrap 决定"从零建库"的结构，必须与迁移后的存量库一致（ADR-038 §5.3）。
    本脚本按 docs/plans/numeric-role-full.csv 的语义族，把
      - 10_schema.sql 中 [COL] [float] 改为 [COL] decimal(p,s)
      - 20_metadata.sql 中对应字段的 F_TYPE 由 float 改为 decimal
    NON_NUMERIC 的 7 列保持 float（改 bit/nvarchar/int 属另案）。

.PARAMETER DryRun
    只统计将改动多少处，不写文件。
#>
param([switch] $DryRun)

$ErrorActionPreference = 'Stop'
try {
    [Console]::OutputEncoding = [Text.Encoding]::UTF8
    $root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
    $schemaPath = Join-Path $root 'db\bootstrap\10_schema.sql'
    $metaPath = Join-Path $root 'db\bootstrap\20_metadata.sql'

    $specs = @{
        'AMOUNT'  = 'decimal(18,4)'
        'QTY'     = 'decimal(18,6)'
        'PRICE'   = 'decimal(18,6)'
        'RATE'    = 'decimal(18,10)'
        'PERCENT' = 'decimal(9,6)'
    }

    $roleMap = @{}
    foreach ($r in (Import-Csv (Join-Path $root 'docs\plans\numeric-role-full.csv') -Encoding utf8)) {
        $t = $specs[$r.Role]
        if ($t) { $roleMap["$($r.Table)|$($r.Column)"] = $t }
    }
    "MAPPED_COLUMNS=$($roleMap.Count)"

    # ---- 1. 10_schema.sql：按 CREATE TABLE 分块替换列类型 ----
    $schema = Get-Content $schemaPath -Raw -Encoding utf8
    $blockPattern = '(?s)CREATE TABLE \[dbo\]\.\[([A-Z0-9_]+)\]\((.*?)\r?\n\)\r?\n'
    $sb = New-Object System.Text.StringBuilder
    $last = 0
    $schemaChanges = 0
    $skipped = New-Object System.Collections.ArrayList

    foreach ($m in [regex]::Matches($schema, $blockPattern)) {
        [void]$sb.Append($schema.Substring($last, $m.Index - $last))
        $table = $m.Groups[1].Value
        $body = $m.Groups[2].Value
        $newBody = $body
        foreach ($cm in [regex]::Matches($body, '\[([A-Za-z0-9_]+)\] \[float\]')) {
            $col = $cm.Groups[1].Value
            $target = $roleMap["$table|$col"]
            if ($target) {
                $newBody = $newBody.Replace($cm.Value, "[$col] $target")
                $schemaChanges++
            }
            else {
                [void]$skipped.Add("$table.$col")
            }
        }
        [void]$sb.Append("CREATE TABLE [dbo].[$table]($newBody`r`n)`r`n")
        $last = $m.Index + $m.Length
    }
    [void]$sb.Append($schema.Substring($last))
    $newSchema = $sb.ToString()

    # ---- 2. 20_metadata.sql：F_TYPE float -> decimal ----
    $metaLines = Get-Content $metaPath -Encoding utf8
    $metaChanges = 0
    for ($i = 0; $i -lt $metaLines.Count; $i++) {
        $line = $metaLines[$i]
        if ($line -match "^\s*\(N'([^']*)',\s*N'([A-Za-z0-9_]*)',\s*N'(?:[^']|'')*',\s*N'float'") {
            $key = "$($Matches[1])|$($Matches[2])"
            if ($roleMap.ContainsKey($key)) {
                $metaLines[$i] = $line -replace "N'float'", "N'decimal'"
                $metaChanges++
            }
        }
    }

    "SCHEMA_CHANGES=$schemaChanges"
    "METADATA_CHANGES=$metaChanges"
    "KEPT_FLOAT=$($skipped.Count)"
    if ($skipped.Count -gt 0) { "  保持 float：" + ($skipped -join ', ') }

    if ($DryRun) { "（DryRun，未写文件）"; exit 0 }

    Set-Content -Path $schemaPath -Value $newSchema -Encoding utf8 -NoNewline
    $metaLines -join "`r`n" | Set-Content -Path $metaPath -Encoding utf8
    "WRITTEN"
    exit 0
}
catch {
    Write-Error $_.Exception.Message
    exit 1
}
