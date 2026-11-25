<#
.SYNOPSIS
    Smoke test of the packages made by Package.ps1, run from the unzipped package folders only.

.DESCRIPTION
    - Agent package: ProcBridge.Agent serves a temporary share on 127.0.0.1 (plaintext, loopback only).
    - Client package: ProcBridge.Launcher runs cmd.exe (x64, then x86 from SysWOW64) through Spawn and the hook,
      and the process reads a file of the share by its virtual path; the output must hold the file content.
    - Relay package: ProcBridge.Relay.Api starts and answers /health/live.

.PARAMETER PackageRoot
    Folder holding the unzipped packages (src/artifacts/<version>). Default: the newest folder under src/artifacts.
#>
[CmdletBinding()]
param(
    [string] $PackageRoot
)

$ErrorActionPreference = 'Stop'
$srcRoot = Split-Path -Parent $PSScriptRoot
if (-not $PackageRoot) {
    $PackageRoot = Get-ChildItem (Join-Path $srcRoot 'artifacts') -Directory | Sort-Object LastWriteTime -Descending |
        Select-Object -First 1 -ExpandProperty FullName
}
$clientDir = Get-ChildItem $PackageRoot -Directory -Filter 'ProcBridge-Client-*' | Select-Object -First 1 -ExpandProperty FullName
$agentDir = Get-ChildItem $PackageRoot -Directory -Filter 'ProcBridge-Agent-*' | Select-Object -First 1 -ExpandProperty FullName
$relayDir = Get-ChildItem $PackageRoot -Directory -Filter 'ProcBridge-Relay-*' | Select-Object -First 1 -ExpandProperty FullName
if (-not ($clientDir -and $agentDir -and $relayDir)) { throw "Packages not found under '$PackageRoot'." }

function Get-FreePort {
    $listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 0)
    $listener.Start()
    try { return $listener.LocalEndpoint.Port } finally { $listener.Stop() }
}

function Wait-Port([int] $Port, [System.Diagnostics.Process] $Process) {
    $deadline = (Get-Date).AddSeconds(30)
    while ((Get-Date) -lt $deadline) {
        if ($Process.HasExited) { throw "$($Process.StartInfo.FileName) exited with code $($Process.ExitCode)." }
        $client = [System.Net.Sockets.TcpClient]::new()
        try { $client.Connect('127.0.0.1', $Port); return } catch { Start-Sleep -Milliseconds 200 } finally { $client.Dispose() }
    }
    throw "Nothing listens on port $Port."
}

$work = Join-Path ([System.IO.Path]::GetTempPath()) ("procbridge-smoke-" + [guid]::NewGuid().ToString('N'))
$shareRoot = Join-Path $work 'share'
New-Item -ItemType Directory $shareRoot | Out-Null
$marker = 'procbridge-smoke-' + [guid]::NewGuid().ToString('N')
Set-Content -Path (Join-Path $shareRoot 'hello.txt') -Value $marker -Encoding ASCII
$virtualRoot = Join-Path $work 'virtual-root'

$processes = @()
try {
    # Agent from its package.
    $agentPort = Get-FreePort
    $agent = Start-Process -FilePath (Join-Path $agentDir 'ProcBridge.Agent.exe') -PassThru -NoNewWindow `
        -RedirectStandardOutput (Join-Path $work 'agent.out.log') -RedirectStandardError (Join-Path $work 'agent.err.log') `
        -ArgumentList @('--share', "s=$shareRoot", '--port', $agentPort, '--bind', '127.0.0.1', '--plaintext', '--data', "`"$(Join-Path $work 'agent-data')`"")
    $processes += $agent
    Wait-Port $agentPort $agent
    Write-Host "Agent listens on $agentPort"

    # Launcher from the client package: Spawn and the hook are found next to it.
    $launcher = Join-Path $clientDir 'ProcBridge.Launcher.exe'
    $targets = @{ x64 = Join-Path $env:SystemRoot 'System32\cmd.exe'; x86 = Join-Path $env:SystemRoot 'SysWOW64\cmd.exe' }
    # Empty remote path (the share root): Windows PowerShell 5.1 drops a bare '' argument, PowerShell 7.3+ passes it.
    $shareRootArg = if ($PSVersionTable.PSVersion.Major -ge 7) { '' } else { '""' }
    foreach ($arch in 'x64', 'x86') {
        $output = & $launcher --plaintext --agent "127.0.0.1:$agentPort" --share s --map $virtualRoot $shareRootArg `
            --data (Join-Path $work "launcher-data-$arch") --cache (Join-Path $work "cache-$arch") --arch $arch `
            -- $targets[$arch] /c type "$virtualRoot\hello.txt" 2>&1 | Out-String
        $code = $LASTEXITCODE
        if ($code -ne 0 -or $output -notmatch $marker) {
            throw "Launcher $arch failed (exit $code):`n$output"
        }
        Write-Host "Launcher $arch read the share through the hook"
    }

    # Relay from its package.
    $relayPort = Get-FreePort
    $relay = Start-Process -FilePath 'dotnet' -WorkingDirectory $relayDir -PassThru -NoNewWindow `
        -RedirectStandardOutput (Join-Path $work 'relay.out.log') -RedirectStandardError (Join-Path $work 'relay.err.log') `
        -ArgumentList @('ProcBridge.Relay.Api.dll', '--urls', "http://127.0.0.1:$relayPort")
    $processes += $relay
    Wait-Port $relayPort $relay
    $health = Invoke-WebRequest -UseBasicParsing "http://127.0.0.1:$relayPort/health/live"
    if ($health.Content -ne 'ok') { throw "Relay health answered '$($health.Content)'." }
    Write-Host 'Relay answers /health/live'
    Write-Host 'Smoke test passed'
}
catch {
    Get-ChildItem $work -Filter '*.log' -ErrorAction SilentlyContinue | ForEach-Object {
        Write-Host "--- $($_.Name)"; Get-Content $_.FullName -ErrorAction SilentlyContinue | Select-Object -Last 40
    }
    throw
}
finally {
    foreach ($process in $processes) {
        if (-not $process.HasExited) { $process.Kill(); $process.WaitForExit(5000) | Out-Null }
    }
    Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
}
