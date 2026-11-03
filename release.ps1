# Builds a release zip on this computer (no hosted CI).
#   .\release.ps1            build dist\TutorialOverlay-<version>-win-x64.zip
#   .\release.ps1 -Tag       also create the git tag v<version> (pushing it is up to you)
# The version comes from <Version> in TutorialOverlay.csproj; bump it there (and in CHANGELOG.md) first.
param([switch]$Tag)
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

$version = ([xml](Get-Content TutorialOverlay.csproj)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
if (-not $version) { throw 'no <Version> in TutorialOverlay.csproj' }
if ((Select-String -Path CHANGELOG.md -Pattern "^## \[$([regex]::Escape($version))\]" -Quiet) -ne $true) {
    throw "CHANGELOG.md has no section '## [$version]'"
}
if ($Tag -and (git tag --list "v$version")) { throw "tag v$version already exists: bump <Version> first" }
if ($Tag -and (git status --porcelain)) { throw 'the working copy has uncommitted changes: commit them before tagging' }

Write-Host "== tests"
dotnet run --project tests
if ($LASTEXITCODE -ne 0) { throw "$LASTEXITCODE test(s) failed" }

$name = "TutorialOverlay-$version-win-x64"
$out = "dist\$name"
Write-Host "== publish $name"
if (Test-Path $out) { Remove-Item -Recurse -Force $out }
dotnet publish -c Release --self-contained true -p:EnableCompressionInSingleFile=true -p:DebugType=none -o $out
if ($LASTEXITCODE -ne 0) { throw 'publish failed' }

# the exe logs to stderr, which Windows PowerShell turns into errors under 'Stop': read stdout only
$ErrorActionPreference = 'Continue'
$reported = (& "$out\TutorialOverlay.exe" --version 2>$null | Out-String).Trim()
$ErrorActionPreference = 'Stop'
if ($reported -ne $version) { throw "the exe reports '$reported', expected '$version'" }

Copy-Item LICENSE, README.md, README.ru.md, CHANGELOG.md $out
$zip = "dist\$name.zip"
if (Test-Path $zip) { Remove-Item -Force $zip }
Compress-Archive -Path "$out\*" -DestinationPath $zip
$hash = (Get-FileHash $zip -Algorithm SHA256).Hash

if ($Tag) { git tag -a "v$version" -m "TutorialOverlay $version"; Write-Host "tagged v$version (push: git push origin v$version)" }

Write-Host ""
Write-Host "$zip  $([math]::Round((Get-Item $zip).Length / 1MB, 1)) MB"
Write-Host "SHA256 $hash"
