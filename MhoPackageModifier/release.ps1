# Builds a release of MHO Package Modifier: releases\MHO_Package_Modifier_v<version>.zip and its .sha256.
# Upload both to a GitHub release tagged v<version> (the app's updater looks there), and the zip to Nexus Mods.
# Self-contained (users need no .NET install); assimp.dll ships next to the exe. Run it through release.bat.
$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $here
[xml]$proj = Get-Content (Join-Path $here 'MhoPackageModifier.csproj')
$version = ($proj.Project.PropertyGroup | Where-Object { $_.Version } | Select-Object -First 1).Version
if (-not $version) { throw 'No <Version> in MhoPackageModifier.csproj' }
$name = "MHO_Package_Modifier_v$version"
$out = Join-Path $here 'releases'
$stage = Join-Path $out 'stage'
$app = Join-Path $stage 'MHO Package Modifier'
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Force $app | Out-Null

Write-Host "Building $name ..."
dotnet publish MhoPackageModifier.csproj -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=false `
    -p:DebugType=none -o $app
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed' }

Copy-Item (Join-Path $here 'Dist\*') $app -Recurse -Force
Copy-Item (Join-Path $here '..\LICENSE') (Join-Path $app 'LICENSE.txt') -Force
foreach ($f in @('MHO_UPK_Mod.exe', 'assimp.dll', 'Help\manual.html', 'ZoneData', 'README.txt', 'THIRD-PARTY-NOTICES.txt', 'LICENSE.txt')) {
    if (-not (Test-Path (Join-Path $app $f))) { throw "The release is missing $f" }
}

$zip = Join-Path $out "$name.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
# Entries written one by one with forward slashes: Windows PowerShell 5's Compress-Archive (and its .NET zip writer)
# store paths with backslashes, which some unzip tools and upload scanners don't read as folders.
Add-Type -AssemblyName System.IO.Compression.FileSystem
Add-Type -AssemblyName System.IO.Compression
$archive = [System.IO.Compression.ZipFile]::Open($zip, [System.IO.Compression.ZipArchiveMode]::Create)
try {
    foreach ($file in Get-ChildItem $app -Recurse -File) {
        $entry = 'MHO Package Modifier/' + $file.FullName.Substring($app.Length + 1).Replace([char]92, [char]47)
        [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $file.FullName, $entry, [System.IO.Compression.CompressionLevel]::Optimal) | Out-Null
    }
} finally { $archive.Dispose() }
$hash = (Get-FileHash $zip -Algorithm SHA256).Hash
Set-Content -Path "$zip.sha256" -Value "$hash  $name.zip" -Encoding ascii
Remove-Item $stage -Recurse -Force

$size = '{0:N1} MB' -f ((Get-Item $zip).Length / 1MB)
Write-Host ''
Write-Host "Release ready: $zip ($size)"
Write-Host "Checksum:      $zip.sha256"
Write-Host ''
Write-Host 'Next:'
Write-Host "  1. GitHub: Releases > Draft a new release, tag v$version, title 'MHO Package Modifier $version',"
Write-Host '     what changed in the description, attach BOTH files, Publish. The app finds it within a day.'
Write-Host '  2. Nexus Mods: upload the same zip as a new file version.'
