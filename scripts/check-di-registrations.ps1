<#
.SYNOPSIS
    DI quality gate: every controller dependency must be registered in Program.cs.

.DESCRIPTION
    A missing builder.Services.AddScoped<...> registration does not fail the build
    nor unit tests (tests construct objects directly), it only surfaces at runtime
    as HTTP 500 "Unable to resolve service for type ... while attempting to
    activate <Controller>". This script parses the primary constructors of all
    EOS.API/Controllers/*.cs and checks each parameter type against the
    registrations found in Program.cs, so the gap is caught before a deploy.

    The same failure mode applies to the effect handlers: they are resolved from
    the container on every document approve / deapprove, and an unresolvable
    dependency breaks that document path outright. Handlers are therefore scanned
    with the same rule.

.EXAMPLE
    pwsh scripts/check-di-registrations.ps1   # exit 0 = clean
#>
[CmdletBinding()]
param(
    [string] $Root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
)

$ErrorActionPreference = 'Stop'

$programPath = Join-Path $Root 'EOS.API\Program.cs'
$controllerDir = Join-Path $Root 'EOS.API\Controllers'

$program = Get-Content -Raw -Encoding UTF8 $programPath
$registered = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
foreach ($match in [regex]::Matches($program, 'Add(?:Scoped|Singleton|Transient)(?:<([^>]+)>)?\(')) {
    if (-not $match.Groups[1].Success) { continue }
    foreach ($part in ($match.Groups[1].Value -split ',')) {
        $name = ($part -replace '^.*\.', '').Trim()
        if ($name) { $null = $registered.Add($name) }
    }
}
# Container/framework-provided types a controller may take directly.
# IOptions<T> comes from Configure<T>/AddOptions, not from an explicit AddScoped.
foreach ($name in 'ILogger', 'ILoggerFactory', 'IConfiguration', 'IWebHostEnvironment', 'IHostEnvironment',
                  'IServiceProvider', 'IHttpContextAccessor', 'IMemoryCache', 'IHttpClientFactory',
                  'IOptions', 'IOptionsSnapshot', 'IOptionsMonitor') {
    $null = $registered.Add($name)
}

$missing = New-Object System.Collections.Generic.List[string]
foreach ($file in Get-ChildItem -Path $controllerDir -File -Filter '*.cs') {
    $text = Get-Content -Raw -Encoding UTF8 $file.FullName
    $ctor = [regex]::Match($text, '(?s)class\s+\w+Controller\s*\((.*?)\)\s*:\s*ControllerBase')
    if (-not $ctor.Success) { continue }
    foreach ($param in [regex]::Split($ctor.Groups[1].Value, ',\s*(?=[A-Za-z_])')) {
        $param = $param.Trim() -replace '\s+', ' '
        if ($param -eq '') { continue }
        if ($param -notmatch '^([\w\.<>,\[\]\?]+)\s+\w+') { continue }
        $type = $Matches[1]
        if ($type -match '^(string|int|long|bool|IReadOnlyList|IEnumerable|List|Func|Action)') { continue }
        $short = ($type -replace '<.*$', '') -replace '^.*\.', ''
        if (-not $registered.Contains($short)) { $missing.Add("$($file.Name): $type") }
    }
}

# Effect handlers: same rule, dependencies come from the container on every approve.
$handlerDir = Join-Path $Root 'EOS.API\Data\Effects'
foreach ($file in Get-ChildItem -Path $handlerDir -Recurse -File -Filter '*.cs') {
    $text = Get-Content -Raw -Encoding UTF8 $file.FullName
    foreach ($impl in [regex]::Matches($text, '(?s)class\s+(\w+)\s*(?:\(([^)]*)\))?\s*:\s*IEffectServiceHandler')) {
        $class = $impl.Groups[1].Value
        $ctor = if ($impl.Groups[2].Success) { $impl.Groups[2].Value } else {
            $explicit = [regex]::Match($text, "(?s)public\s+$class\s*\(([^)]*)\)")
            if (-not $explicit.Success) { continue }
            $explicit.Groups[1].Value
        }
        foreach ($param in [regex]::Split($ctor, ',\s*(?=[A-Za-z_])')) {
            $param = $param.Trim() -replace '\s+', ' '
            if ($param -eq '') { continue }
            if ($param -notmatch '^([\w\.<>,\[\]\?]+)\s+\w+') { continue }
            $type = $Matches[1]
            if ($type -match '^(string|int|long|bool|IReadOnlyList|IEnumerable|List|Func|Action)') { continue }
            $short = ($type -replace '<.*$', '') -replace '^.*\.', ''
            if (-not $registered.Contains($short)) { $missing.Add("$($file.Name): $class($type)") }
        }
    }
}

if ($missing.Count -gt 0) {
    Write-Output "FAIL dependencies missing from Program.cs DI registrations (controllers + effect handlers):"
    $missing | Sort-Object -Unique | ForEach-Object { Write-Output "  $_" }
    exit 1
}

Write-Output "PASS all controller and effect-handler dependencies are registered in Program.cs ($($registered.Count) registered types scanned)."
exit 0
