<#
.SYNOPSIS
    Build & package the Projectionist plugin for Jellyfin.

.DESCRIPTION
    Cleans, builds in Release, and stages the DLL into ./build-output/.
    Also produces a versioned zip ready for direct copy into a Jellyfin
    plugins folder.

.PARAMETER Version
    Override the version stamped on the output filename. Defaults to the
    version in the csproj.
#>
param(
    [string]$Version
)

$ErrorActionPreference = 'Stop'
$repoRoot = Resolve-Path (Join-Path $PSScriptRoot '..')
$proj     = Join-Path $repoRoot 'src/Projectionist/Projectionist.csproj'
$out      = Join-Path $repoRoot 'build-output'

if (Test-Path $out) { Remove-Item -Recurse -Force $out }
New-Item -ItemType Directory -Path $out | Out-Null

Write-Host "==> dotnet restore" -ForegroundColor Cyan
& dotnet restore $proj | Out-Host

Write-Host "==> dotnet build (Release)" -ForegroundColor Cyan
& dotnet build $proj -c Release --no-restore | Out-Host

$dllSource = Join-Path $repoRoot 'src/Projectionist/bin/Release/net8.0/Jellyfin.Plugin.Projectionist.dll'
if (-not (Test-Path $dllSource)) {
    throw "Build did not produce expected DLL at $dllSource"
}

# Resolve version
if (-not $Version) {
    $csproj = [xml](Get-Content $proj)
    $Version = $csproj.Project.PropertyGroup.Version
    if ($Version -is [System.Array]) { $Version = ($Version | Where-Object { $_ } | Select-Object -First 1) }
    if (-not $Version) { $Version = '0.0.0' }
}

$stagingDir = Join-Path $out "Projectionist_$Version"
New-Item -ItemType Directory -Path $stagingDir | Out-Null
Copy-Item $dllSource (Join-Path $stagingDir 'Jellyfin.Plugin.Projectionist.dll')

# Zip up the staging dir contents (so unzip drops a Projectionist folder)
$zipPath = Join-Path $out "Projectionist_$Version.zip"
Compress-Archive -Path $stagingDir -DestinationPath $zipPath -Force

Write-Host ""
Write-Host "Done. Outputs:" -ForegroundColor Green
Write-Host "  DLL : $((Join-Path $stagingDir 'Jellyfin.Plugin.Projectionist.dll'))"
Write-Host "  ZIP : $zipPath"
