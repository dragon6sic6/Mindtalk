<#
.SYNOPSIS
  Builds a Windows release of Mindtalk: the installer (Setup.exe) and the update packages.

.DESCRIPTION
  Publishes the app (Release, self-contained), packs it with Velopack, and — given a GitHub
  token — uploads it as a release to github.com/dragon6sic6/Mindtalk, where installed copies
  of Mindtalk look for updates (Settings → Updates).

  Needs the .NET 9 SDK and Velopack's command-line tool:  dotnet tool install -g vpk

  Sign the release by passing -SignParams (e.g. for signtool with your code-signing
  certificate); unsigned installers make Windows SmartScreen warn on first run.

.EXAMPLE
  ./scripts/release.ps1 -Version 1.0.0
  ./scripts/release.ps1 -Version 1.0.0 -Token $env:GITHUB_TOKEN -SignParams "/a /fd sha256 /tr http://timestamp.digicert.com /td sha256"
#>
param(
    [Parameter(Mandatory)][string]$Version,
    [string]$Token,
    [string]$SignParams,
    [ValidateSet("win-x64", "win-arm64")][string]$Runtime = "win-x64"
)
$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$publish = Join-Path $root "artifacts\publish\$Runtime"
$releases = Join-Path $root "artifacts\releases\$Runtime"

Write-Host "Publishing Mindtalk $Version ($Runtime)…"
if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }
dotnet publish (Join-Path $root "src\Mindtalk.App") -c Release -r $Runtime --self-contained `
    -p:Version=$Version -p:Platform=$($Runtime -replace 'win-', '') -o $publish
if ($LASTEXITCODE) { throw "dotnet publish failed" }

Write-Host "Packing…"
$pack = @(
    "pack", "--packId", "Mindtalk", "--packVersion", $Version, "--packDir", $publish,
    "--mainExe", "Mindtalk.exe", "--packTitle", "Mindtalk", "--packAuthors", "Mindact Solutions AB",
    "--icon", (Join-Path $root "src\Mindtalk.App\Assets\Mindtalk.ico"), "--runtime", $Runtime, "--outputDir", $releases
)
if ($SignParams) { $pack += @("--signParams", $SignParams) }
vpk @pack
if ($LASTEXITCODE) { throw "vpk pack failed" }

if ($Token) {
    Write-Host "Uploading to GitHub…"
    vpk upload github --repoUrl https://github.com/dragon6sic6/Mindtalk --token $Token --outputDir $releases `
        --releaseName "Mindtalk $Version for Windows" --tag "windows-v$Version" --publish
    if ($LASTEXITCODE) { throw "vpk upload failed" }
}
Write-Host "Done: $releases"
