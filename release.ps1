# Builds a release on this computer (no hosted CI): dist\TutorialOverlay-<version>-win-x64.zip
#   .\release.ps1            build
#   .\release.ps1 -Tag       build and create the tag v<version> (pushing and the GitHub release are up to you)
# The version comes from <Version> in TutorialOverlay.csproj; bump it there (and in CHANGELOG.md) first.
param([switch]$Tag)
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

$version = ([xml](Get-Content TutorialOverlay.csproj)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
if (-not $version) { throw 'no <Version> in TutorialOverlay.csproj' }
if ((Select-String -Path CHANGELOG.md -Pattern "^## \[$([regex]::Escape($version))\]" -Quiet) -ne $true) {
    throw "CHANGELOG.md has no section '## [$version]'"
}
if ($Tag) {
    if (git tag --list "v$version") { throw "tag v$version already exists: bump <Version> first" }
    if (git status --porcelain) { throw 'the working copy has uncommitted changes: commit them before tagging' }
}

Write-Host "== tests"
dotnet run --project tests
if ($LASTEXITCODE -ne 0) { throw "$LASTEXITCODE test(s) failed" }

$name = "TutorialOverlay-$version-win-x64"
$out = "dist\$name"
Write-Host "== publish $name"
if (Test-Path $out) { Remove-Item -Recurse -Force $out }
# no compression inside the exe: a compressed bundle is unpacked into memory on every start (bridge 110 MB → 44 MB,
# server 164 MB → 83 MB); the zip compresses it anyway, so the download stays the same size
dotnet publish -c Release --self-contained true -p:DebugType=none -o $out
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

# the winget manifest of this version (submitted as a pull request to microsoft/winget-pkgs): templates in winget\
$hash = (Get-FileHash $zip -Algorithm SHA256).Hash
$wingetDir = "dist\winget\$version"
New-Item -ItemType Directory -Force $wingetDir | Out-Null
foreach ($t in Get-ChildItem winget\*.yaml) {
    $text = (Get-Content $t.FullName -Raw).Replace('{VERSION}', $version).Replace('{SHA256}', $hash).Replace('{DATE}', (Get-Date -Format 'yyyy-MM-dd'))
    [IO.File]::WriteAllText((Join-Path $PSScriptRoot "$wingetDir\$($t.Name)"), $text, (New-Object Text.UTF8Encoding $false))
}
winget validate --manifest $wingetDir
if ($LASTEXITCODE -ne 0) { throw 'winget manifest is not valid' }

if ($Tag) {
    git tag -a "v$version" -m "TutorialOverlay $version"
    Write-Host "tagged v$version (push: git push origin main v$version)"
}

Write-Host ""
foreach ($f in @($zip)) {
    Write-Host ("{0}  {1} MB  SHA256 {2}" -f $f, [math]::Round((Get-Item $f).Length / 1MB, 1), (Get-FileHash $f -Algorithm SHA256).Hash.ToLowerInvariant())
}
Write-Host "upload it to the GitHub release v$version"
