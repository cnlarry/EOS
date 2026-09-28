# Sync agent skills: create Cursor mirror junctions from the canonical .agents/skills/.
# Idempotent. Run from repo root:  pwsh scripts/sync-agent-skills.ps1
$ErrorActionPreference = 'Stop'
try {
    [Console]::OutputEncoding = [Text.Encoding]::UTF8
    $root = Split-Path -Parent $PSScriptRoot
    $src = Join-Path $root '.agents\skills'
    $dst = Join-Path $root '.cursor\skills'
    if (-not (Test-Path -LiteralPath $src)) { Write-Output "SKIP: $src missing"; exit 0 }
    if (-not (Test-Path -LiteralPath $dst)) { New-Item -ItemType Directory -Path $dst | Out-Null }

    $made = 0; $skipped = 0
    Get-ChildItem -LiteralPath $src -Directory | ForEach-Object {
        $target = Join-Path $dst $_.Name
        if (Test-Path -LiteralPath $target) { $skipped++; Write-Output "SKIP(exists): $($_.Name)"; return }
        New-Item -ItemType Junction -Path $target -Target $_.FullName | Out-Null
        $made++; Write-Output "LINKED: $($_.Name)"
    }
    Write-Output "PASS: linked=$made skipped=$skipped"
    exit 0
} catch {
    Write-Output "FAIL: $($_.Exception.Message)"
    exit 1
}