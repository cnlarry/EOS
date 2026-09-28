<#
.SYNOPSIS
    ADR §15.4 quality gate: the effect-engine runtime must stay data-driven.

.DESCRIPTION
    The effect engine's business semantics come from closed configuration
    (MODULE_BUSINESS_ACTION / MODULE_VALIDATION_RULE) and must never branch on a
    hard-coded module id in code. This script scans the effect runtime
    (EOS.API/Data/Effects) and fails when a per-module branch is found.

    Known legitimate non-effect special cases (print layout "_card" fallback for
    1401/1601 in DocumentPdfService/ReportFormatRepository) live outside this
    directory and are intentionally not covered here.

.EXAMPLE
    pwsh scripts/check-effect-module-branches.ps1   # exit 0 = clean
#>
[CmdletBinding()]
param(
    [string] $Root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path,
    [string] $ScanDir = (Join-Path $PSScriptRoot '..\EOS.API\Data\Effects')
)

$ErrorActionPreference = 'Stop'
$patterns = @(
    'switch\s*\(\s*moduleId\s*\)',
    '\bmoduleId\s*==\s*\d+',
    '\bmoduleId\s*is\s*\d+',
    '\bModuleId\s*==\s*\d+',
    '\bModuleId\s*is\s*\d+',
    '\.ModuleId\s*==\s*\d+',
    '\.ModuleId\s*is\s*\d+'
)

$hits = @()
Get-ChildItem -Path $ScanDir -Recurse -Filter '*.cs' | ForEach-Object {
    $file = $_.FullName
    $lineNo = 0
    foreach ($line in [System.IO.File]::ReadLines($file)) {
        $lineNo++
        foreach ($pattern in $patterns) {
            if ($line -match $pattern) {
                $hits += "{0}:{1}: {2}" -f ($file.Substring($Root.Length + 1)), $lineNo, $line.Trim()
            }
        }
    }
}

if ($hits.Count -gt 0) {
    Write-Output "FAIL effect runtime contains per-module branches (ADR 15.4):"
    $hits | Sort-Object -Unique | ForEach-Object { Write-Output "  $_" }
    exit 1
}

Write-Output "PASS effect runtime is data-driven (no per-module branches under EOS.API/Data/Effects)."
exit 0
