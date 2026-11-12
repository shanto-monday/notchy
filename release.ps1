# Publish a new Notchy version to GitHub. Installed copies update themselves within ~6 hours.
#   powershell -ExecutionPolicy Bypass -File release.ps1 -Version 1.1.0 -Notes "What changed"
param([Parameter(Mandatory=$true)][string]$Version, [string]$Notes = "Improvements and fixes.")
$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$gh = "C:\Program Files\GitHub CLI\gh.exe"
Set-Location $here
Set-Content -NoNewline -Encoding ASCII "$here\VERSION" $Version
& "C:\Program Files\dotnet\dotnet.exe" publish -c Release -o dist
if ($LASTEXITCODE -ne 0) { throw "build failed" }
Remove-Item "$here\dist\*.xml" -ErrorAction SilentlyContinue
git add -A
git commit -m "Notchy $Version" -m $Notes
git tag "v$Version"
git push origin HEAD --tags
& $gh release create "v$Version" "$here\dist\Notchy.exe" --title "Notchy $Version" --notes $Notes
Write-Host "Released v$Version"
