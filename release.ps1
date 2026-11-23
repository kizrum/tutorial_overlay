# Builds a release: dist\TutorialOverlay-<version>-win-x64.zip
#   .\release.ps1            tests, then build the zip here (a check: the published zip is built by GitHub Actions)
#   .\release.ps1 -Tag       the same, then create the tag v<version>; pushing it starts the release build
#                            (.github\workflows\release.yml: build, sign when SignPath is set up, GitHub release)
#   .\release.ps1 -Winget    after that build: the winget manifest from the published zip (dist\winget\<version>)
#   .\release.ps1 -Ci        used by the workflow: publish the folder dist\<name> only (no tests, no zip, no tag)
# The version comes from <Version> in TutorialOverlay.csproj; bump it there (and in CHANGELOG.md) first.
param([switch]$Tag, [switch]$Ci, [switch]$Winget)
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

$version = ([xml](Get-Content TutorialOverlay.csproj)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
if (-not $version) { throw 'no <Version> in TutorialOverlay.csproj' }
if ((Select-String -Path CHANGELOG.md -Pattern "^## \[$([regex]::Escape($version))\]" -Quiet) -ne $true) {
    throw "CHANGELOG.md has no section '## [$version]'"
}
$name = "TutorialOverlay-$version-win-x64"

if ($Winget) {
    # the manifest pins the hash of the zip users download: take it from the GitHub release, not from a local build
    $zip = "dist\$name.zip"
    New-Item -ItemType Directory -Force dist | Out-Null
    $url = "https://github.com/kizrum/tutorial_overlay/releases/download/v$version/$name.zip"
    Write-Host "== download $url"
    Invoke-WebRequest -Uri $url -OutFile $zip -UseBasicParsing
    $hash = (Get-FileHash $zip -Algorithm SHA256).Hash
    $wingetDir = "dist\winget\$version"
    New-Item -ItemType Directory -Force $wingetDir | Out-Null
    foreach ($t in Get-ChildItem winget\*.yaml) {
        $text = (Get-Content $t.FullName -Raw).Replace('{VERSION}', $version).Replace('{SHA256}', $hash).Replace('{DATE}', (Get-Date -Format 'yyyy-MM-dd'))
        [IO.File]::WriteAllText((Join-Path $PSScriptRoot "$wingetDir\$($t.Name)"), $text, (New-Object Text.UTF8Encoding $false))
    }
    winget validate --manifest $wingetDir
    if ($LASTEXITCODE -ne 0) { throw 'winget manifest is not valid' }
    Write-Host "$wingetDir  SHA256 $($hash.ToLowerInvariant())"
    exit 0
}

if ($Tag) {
    if (git tag --list "v$version") { throw "tag v$version already exists: bump <Version> first" }
    if (git status --porcelain) { throw 'the working copy has uncommitted changes: commit them before tagging' }
}

if (-not $Ci) {
    Write-Host "== tests"
    dotnet run --project tests
    if ($LASTEXITCODE -ne 0) { throw "$LASTEXITCODE test(s) failed" }
}

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
if ($Ci) {
    # the workflow signs $out\TutorialOverlay.exe (when SignPath is set up) and zips the folder itself
    if ($env:GITHUB_OUTPUT) { "name=$name" | Out-File -Append -Encoding utf8 $env:GITHUB_OUTPUT }
    Write-Host "published $out"
    exit 0
}

$zip = "dist\$name.zip"
if (Test-Path $zip) { Remove-Item -Force $zip }
Compress-Archive -Path "$out\*" -DestinationPath $zip

if ($Tag) {
    git tag -a "v$version" -m "TutorialOverlay $version"
    Write-Host "tagged v$version (push: git push origin main v$version; the release build starts on the tag)"
}

Write-Host ""
Write-Host ("{0}  {1} MB  SHA256 {2}" -f $zip, [math]::Round((Get-Item $zip).Length / 1MB, 1), (Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant())
Write-Host "a local check only: the GitHub release gets the zip built by the workflow; then .\release.ps1 -Winget"
