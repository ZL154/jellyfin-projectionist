<#
.SYNOPSIS
    Build & package the Projectionist plugin for Jellyfin.

.DESCRIPTION
    Builds Release for both target frameworks and produces one zip per
    Jellyfin line in ./build-output/:

      projectionist_X.Y.Z.0.zip  net9.0   targetAbi 10.11.0.0  (Jellyfin 10.11)
      projectionist_X.Y.Z.1.zip  net10.0  targetAbi 12.0.0.0   (Jellyfin 12)

    Each zip holds the DLL, a meta.json stamped with that package's version
    and targetAbi, LICENSE and CHANGELOG.md. MD5s (for manifest.json) are
    printed at the end.

.PARAMETER Version
    Override the X.Y.Z version. Defaults to <Version> in the csproj.
#>
param(
    [string]$Version
)

$ErrorActionPreference = 'Stop'
$repoRoot = Resolve-Path (Join-Path $PSScriptRoot '..')
$proj     = Join-Path $repoRoot 'src/Projectionist/Projectionist.csproj'
$out      = Join-Path $repoRoot 'build-output'

if (-not $Version) {
    $csproj = [xml](Get-Content $proj)
    $Version = @($csproj.Project.PropertyGroup.Version | Where-Object { $_ })[0]
}
if ($Version -notmatch '^\d+\.\d+\.\d+$') {
    throw "Version '$Version' must be X.Y.Z; each package adds its own fourth part."
}

$packages = @(
    @{ Tfm = 'net9.0';  Suffix = '0'; TargetAbi = '10.11.0.0' },
    @{ Tfm = 'net10.0'; Suffix = '1'; TargetAbi = '12.0.0.0' }
)

if (Test-Path $out) { Remove-Item -Recurse -Force $out }
New-Item -ItemType Directory -Path $out | Out-Null

Write-Host "==> dotnet restore" -ForegroundColor Cyan
& dotnet restore $proj | Out-Host
if ($LASTEXITCODE -ne 0) { throw 'restore failed' }

$metaTemplate = Get-Content (Join-Path $repoRoot 'src/Projectionist/meta.json') -Raw | ConvertFrom-Json
$results = @()

foreach ($pkg in $packages) {
    $full = "$Version.$($pkg.Suffix)"
    Write-Host "==> dotnet build $($pkg.Tfm) -> $full" -ForegroundColor Cyan
    & dotnet build $proj -c Release -f $pkg.Tfm --no-restore -p:Version=$Version | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "build failed for $($pkg.Tfm)" }

    $dll = Join-Path $repoRoot "src/Projectionist/bin/Release/$($pkg.Tfm)/Jellyfin.Plugin.Projectionist.dll"
    $stamped = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($dll).FileVersion
    if ($stamped -ne $full) { throw "DLL for $($pkg.Tfm) is stamped $stamped, expected $full" }

    $staging = Join-Path $out "Projectionist_$full"
    New-Item -ItemType Directory -Path $staging | Out-Null
    Copy-Item $dll $staging
    Copy-Item (Join-Path $repoRoot 'LICENSE') $staging
    Copy-Item (Join-Path $repoRoot 'CHANGELOG.md') $staging

    $meta = $metaTemplate | Select-Object *
    $meta.version = $full
    $meta.targetAbi = $pkg.TargetAbi
    $meta.timestamp = (Get-Date).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ")
    $meta | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $staging 'meta.json') -Encoding utf8

    $zip = Join-Path $out "projectionist_$full.zip"
    Compress-Archive -Path $staging -DestinationPath $zip -Force
    $md5 = (Get-FileHash $zip -Algorithm MD5).Hash.ToLowerInvariant()
    Set-Content -Path "$zip.md5" -Value $md5 -NoNewline
    $results += [pscustomobject]@{ Package = $full; TargetAbi = $pkg.TargetAbi; Zip = (Split-Path $zip -Leaf); MD5 = $md5 }
}

Write-Host ""
Write-Host "Done:" -ForegroundColor Green
$results | Format-Table -AutoSize | Out-Host
