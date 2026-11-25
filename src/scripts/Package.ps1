<#
.SYNOPSIS
    Builds the release packages of ProcBridge into src/artifacts/<version>.

.DESCRIPTION
    Three zip files, all framework-dependent (.NET 10 runtime required, see docs/Release-vi.md):
      ProcBridge-Client-<version>-win.zip  machine A: ProcBridge.Launcher (CLI), ProcBridge.Launcher.Wpf,
                                           ProcBridge.Spawn.x64/x86.exe and ProcBridge.Hook.x64/x86.dll side by side
      ProcBridge-Agent-<version>-win.zip   machine B: ProcBridge.Agent (CLI) and ProcBridge.Agent.Wpf
      ProcBridge-Relay-<version>.zip       relay server (ASP.NET Core, portable: Windows or Linux)
    The unzipped folders stay next to the zip files (smoke test, Test-Package.ps1).

.PARAMETER Version
    Version of the build (M.N.P or M.N.P-suffix). Default: from the last vM.N.P tag
    (M.N.P on the tagged commit, M.N.P-dev.<commits since tag> after it, 0.0.0-dev.<commits> without a tag).

.PARAMETER SkipNative
    Use the native binaries already built under src/native/out/<Platform>/Release.
#>
[CmdletBinding()]
param(
    [string] $Version,
    [switch] $SkipNative
)

$ErrorActionPreference = 'Stop'
$srcRoot = Split-Path -Parent $PSScriptRoot

function Invoke-Native {
    param([string] $FilePath, [string[]] $Arguments)
    & $FilePath @Arguments
    if ($LASTEXITCODE -ne 0) { throw "$FilePath failed with exit code $LASTEXITCODE." }
}

function Get-TagVersion {
    # No tag yet is not an error; Windows PowerShell 5.1 turns git's stderr into one under 'Stop'.
    $ErrorActionPreference = 'Continue'
    $describe = git -C $srcRoot describe --tags --long --match 'v[0-9]*.[0-9]*.[0-9]*' 2>$null
    if ($LASTEXITCODE -eq 0 -and $describe -match '^v(\d+\.\d+\.\d+)-(\d+)-g[0-9a-f]+$') {
        if ($Matches[2] -eq '0') { return $Matches[1] }
        return "$($Matches[1])-dev.$($Matches[2])"
    }
    $count = git -C $srcRoot rev-list --count HEAD
    if ($LASTEXITCODE -ne 0) { throw 'git rev-list failed.' }
    return "0.0.0-dev.$count"
}

function Find-MSBuild {
    $command = Get-Command msbuild.exe -ErrorAction SilentlyContinue
    if ($command) { return $command.Source }
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (Test-Path $vswhere) {
        $path = & $vswhere -latest -prerelease -products * -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\MSBuild.exe' | Select-Object -First 1
        if ($path) { return $path }
    }
    throw 'MSBuild.exe not found (install Visual Studio with the C++ workload).'
}

if (-not $Version) { $Version = Get-TagVersion }
if ($Version -notmatch '^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$') { throw "Invalid version '$Version'." }
Write-Host "ProcBridge $Version"

$outRoot = Join-Path $srcRoot "artifacts\$Version"
if (Test-Path $outRoot) { Remove-Item $outRoot -Recurse -Force }
New-Item -ItemType Directory $outRoot | Out-Null

# Native: Hook and Spawn for both platforms (the hook of the target's bitness is injected by Spawn).
$nativeOut = Join-Path $srcRoot 'native\out'
if (-not $SkipNative) {
    $msbuild = Find-MSBuild
    foreach ($platform in 'x64', 'x86') {
        Invoke-Native $msbuild @((Join-Path $srcRoot 'ProcBridge.Native.sln'), '-m', '-nologo', '-v:minimal',
            '-p:Configuration=Release', "-p:Platform=$platform")
    }
}
$nativeFiles = @(
    'x64\Release\ProcBridge.Hook.x64.dll', 'x64\Release\ProcBridge.Spawn.x64.exe',
    'Win32\Release\ProcBridge.Hook.x86.dll', 'Win32\Release\ProcBridge.Spawn.x86.exe'
) | ForEach-Object { Join-Path $nativeOut $_ }
foreach ($file in $nativeFiles) {
    if (-not (Test-Path $file)) { throw "Native binary missing: $file" }
}

function Publish-Project {
    param([string] $Project, [string] $Destination)
    Invoke-Native dotnet @('publish', (Join-Path $srcRoot $Project), '-c', 'Release', '-nologo', '-v:minimal',
        '-o', $Destination, "-p:Version=$Version", '-p:SelfContained=false', '-p:DebugType=none')
}

$packages = [ordered]@{
    "ProcBridge-Client-$Version-win" = @('client\ProcBridge.Launcher\ProcBridge.Launcher.csproj', 'client\ProcBridge.Launcher.Wpf\ProcBridge.Launcher.Wpf.csproj')
    "ProcBridge-Agent-$Version-win"  = @('agent\ProcBridge.Agent\ProcBridge.Agent.csproj', 'agent\ProcBridge.Agent.Wpf\ProcBridge.Agent.Wpf.csproj')
    "ProcBridge-Relay-$Version"      = @('relay\ProcBridge.Relay.Api\ProcBridge.Relay.Api.csproj')
}
foreach ($name in $packages.Keys) {
    $folder = Join-Path $outRoot $name
    foreach ($project in $packages[$name]) { Publish-Project $project $folder }
    if ($name -like 'ProcBridge-Client-*') {
        # Launcher (CLI and WPF) looks for ProcBridge.Spawn.<arch>.exe in its own folder, Spawn for the hooks next to itself.
        Copy-Item $nativeFiles $folder
    }
    Compress-Archive -Path (Join-Path $folder '*') -DestinationPath "$folder.zip"
    Write-Host "  $name.zip"
}
Write-Host "Packages in $outRoot"
