#Requires -Version 7.0
<#
.SYNOPSIS
EOS 本地开发服务管理：EOS.API 一键启动、停止、重启、状态与日志。
.DESCRIPTION
日志统一写入仓库根 logs/ 目录（已 gitignore）。服务以隐藏窗口启动，
端口就绪即视为运行中（EOS.API 的 /health 返回登录页，因此统一用端口探测）。
.EXAMPLE
.\scripts\dev-services.ps1 start              # 启动 EOS.API（已在运行的跳过）
.\scripts\dev-services.ps1 stop               # 停止
.\scripts\dev-services.ps1 restart api        # 重启 EOS.API
.\scripts\dev-services.ps1 status
#>
param(
    [ValidateSet('start', 'stop', 'restart', 'status', 'logs', 'help')]
    [string]$Action = 'status',
    [ValidateSet('all', 'api')]
    [string]$Service = 'all',
    [int]$Tail = 50
)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$logDir = Join-Path $root 'logs'

$services = @(
    [pscustomobject]@{ Name = 'api'; Project = 'EOS.API'; Port = 5261; Description = 'EOS.API（业务 API）' }
)

function Get-SelectedServices {
    param([string]$Service)
    if ($Service -eq 'all') {
        return $services
    }

    return $services | Where-Object { $_.Name -eq $Service }
}

function Test-PortListening {
    param([int]$Port)
    return $null -ne (Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue)
}

function Get-PortProcessId {
    param([int]$Port)
    $conn = Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue
    if ($null -eq $conn) {
        return $null
    }

    return $conn | Select-Object -First 1 -ExpandProperty OwningProcess
}

function Start-ServiceProc {
    param($svc)
    if (Test-PortListening $svc.Port) {
        Write-Host "  [跳过] $($svc.Name) 已在运行（端口 $($svc.Port)）" -ForegroundColor Yellow
        return
    }

    if (-not (Test-Path -LiteralPath $logDir)) {
        New-Item -ItemType Directory -Path $logDir | Out-Null
    }

    $out = Join-Path $logDir "$($svc.Name).out.log"
    $err = Join-Path $logDir "$($svc.Name).err.log"
    # Launched via Win32_Process Create: the parent is the WMI service, outside the caller's
    # job object, so the caller does not block waiting for descendant processes.
    # cmd wrapper handles output redirection; ProcessStartupInformation.ShowWindow=0 hides the window.
    $cmdLine = "cmd.exe /c dotnet run --project `"$($svc.Project)`" > `"$out`" 2> `"$err`""
    # Win32_ProcessStartup 为嵌入类：手工构造 CimInstance + 显式 Uint16 属性
    $startup = New-Object Microsoft.Management.Infrastructure.CimInstance -ArgumentList 'Win32_ProcessStartup'
    $null = $startup.CimInstanceProperties.Add([Microsoft.Management.Infrastructure.CimProperty]::Create(
        'ShowWindow', [uint16]0, 'Uint16', [Microsoft.Management.Infrastructure.CimFlags]::None))
    $result = Invoke-CimMethod -ClassName Win32_Process -MethodName Create -Arguments @{
        CommandLine               = $cmdLine
        CurrentDirectory          = $root
        ProcessStartupInformation = $startup
    }
    if ($result.ReturnValue -ne 0) {
        Write-Host "  [失败] $($svc.Name) 进程创建失败（Win32_ReturnValue=$($result.ReturnValue)）" -ForegroundColor Red
        return
    }
    Write-Host "  [启动] $($svc.Name)（$($svc.Description)），PID $($result.ProcessId)，日志：logs\$($svc.Name).*.log" -ForegroundColor Green
}

function Stop-ServiceProc {
    param($svc)
    $procId = Get-PortProcessId $svc.Port
    if ($null -eq $procId) {
        Write-Host "  [停止] $($svc.Name) 未在运行（端口 $($svc.Port)）" -ForegroundColor Yellow
        return
    }

    Stop-Process -Id $procId -Force
    Write-Host "  [停止] $($svc.Name)（PID $procId）" -ForegroundColor Cyan
}

function Show-Status {
    param([string]$Service)
    Write-Host ''
    Write-Host ('{0,-9} {1,-7} {2,-7} {3,-7} {4}' -f '服务', '端口', '状态', 'PID', '说明')
    foreach ($svc in Get-SelectedServices $Service) {
        $listening = Test-PortListening $svc.Port
        $procId = Get-PortProcessId $svc.Port
        $statusText = if ($listening) { '运行中' } else { '已停止' }
        $color = if ($listening) { 'Green' } else { 'Red' }
        Write-Host ('{0,-9} {1,-7} {2,-7} {3,-7} {4}' -f $svc.Name, $svc.Port, $statusText, ($(if ($null -eq $procId) { '-' } else { $procId })), $svc.Description) -ForegroundColor $color
    }

    Write-Host ''
}

function Wait-ForPorts {
    param($svcList)
    $pending = @($svcList | Where-Object { -not (Test-PortListening $_.Port) })
    for ($i = 0; $i -lt 30 -and $pending.Count -gt 0; $i++) {
        Start-Sleep -Seconds 2
        $pending = @($pending | Where-Object { -not (Test-PortListening $_.Port) })
    }

    foreach ($svc in $pending) {
        Write-Host "  [警告] $($svc.Name) 30 秒内未就绪，请查看 logs\$($svc.Name).err.log" -ForegroundColor Red
    }
}

switch ($Action) {
    'start' {
        Write-Host '启动 EOS 服务…'
        $selected = @(Get-SelectedServices $Service)
        foreach ($svc in $selected) {
            Start-ServiceProc $svc
        }

        Write-Host '等待端口就绪…'
        Wait-ForPorts $selected
        Show-Status $Service
    }
    'stop' {
        Write-Host '停止 EOS 服务…'
        foreach ($svc in @(Get-SelectedServices $Service)) {
            Stop-ServiceProc $svc
        }

        Start-Sleep -Seconds 1
        Show-Status $Service
    }
    'restart' {
        Write-Host '重启 EOS 服务…'
        $selected = @(Get-SelectedServices $Service)
        foreach ($svc in $selected) {
            Stop-ServiceProc $svc
        }

        Start-Sleep -Seconds 1
        foreach ($svc in $selected) {
            Start-ServiceProc $svc
        }

        Write-Host '等待端口就绪…'
        Wait-ForPorts $selected
        Show-Status $Service
    }
    'status' {
        Show-Status $Service
    }
    'logs' {
        foreach ($svc in Get-SelectedServices $Service) {
            $out = Join-Path $logDir "$($svc.Name).out.log"
            $err = Join-Path $logDir "$($svc.Name).err.log"
            Write-Host "===== $($svc.Name) stdout（最后 $Tail 行）====="
            if (Test-Path -LiteralPath $out) {
                Get-Content -LiteralPath $out -Tail $Tail
            }
            else {
                Write-Host '（无日志）'
            }

            Write-Host "===== $($svc.Name) stderr ====="
            if (Test-Path -LiteralPath $err) {
                Get-Content -LiteralPath $err -Tail $Tail
            }
            else {
                Write-Host '（无）'
            }

            Write-Host ''
        }
    }
    default {
        Get-Help $PSCommandPath
    }
}
