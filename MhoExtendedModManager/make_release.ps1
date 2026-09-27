# Builds a release of MHO Extended Mod Manager for GitHub (self-update) and Nexus:
#   releases\MHO_Ext_ModManager-<version>.zip         the app in a folder "MHO Extended Mod Manager" (no data\, no .pdb)
#   releases\MHO_Ext_ModManager-<version>.zip.sha256  its SHA-256 (the app refuses an update without a matching one)
# Then upload both to a GitHub release tagged extmm-v<version> on leeper48/MHO-UPK-Tools (see the steps printed at the end).
#
#   powershell -ExecutionPolicy Bypass -File make_release.ps1                 version from the csproj
#   ... -NoChecksums                                                           leave StockData\upk_checksums.json out
#   ... -Version 9.9.9 -Out <folder>                                           tests only (also stamps MPM with that version)
param([string]$Version, [string]$Out, [switch]$NoChecksums)
$ErrorActionPreference = 'Stop'
$proj = Join-Path $PSScriptRoot 'MhoExtendedModManager.csproj'
$stampVersion = [bool]$Version
if (-not $Version) { $Version = ([xml](Get-Content $proj)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1 }
if (-not $Out) { $Out = Join-Path $PSScriptRoot 'releases' }
$stageRoot = Join-Path $Out "stage-$Version"
$stage = Join-Path $stageRoot 'MHO Extended Mod Manager'
if (Test-Path $stageRoot) { [IO.Directory]::Delete($stageRoot, $true) }
New-Item -ItemType Directory -Force $stage | Out-Null

$publishArgs = @('publish', $proj, '-c', 'Release', '-r', 'win-x64', '--self-contained', 'false', '-p:PublishSingleFile=true', '-o', $stage)
if ($stampVersion) { $publishArgs += "-p:Version=$Version" }
& dotnet @publishArgs
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed' }

Get-ChildItem $stage -Recurse -Filter *.pdb | ForEach-Object { [IO.File]::Delete($_.FullName) }
if (Test-Path (Join-Path $stage 'data')) { [IO.Directory]::Delete((Join-Path $stage 'data'), $true) }
# MPM's launcher: its code is inside the single-file exe, the stub alone can't run (the manager never starts it).
foreach ($f in 'MHO_UPK_Mod.exe', 'MHO_UPK_Mod.runtimeconfig.json') { $p = Join-Path $stage $f; if (Test-Path $p) { [IO.File]::Delete($p) } }
if ($NoChecksums) { $c = Join-Path $stage 'StockData\upk_checksums.json'; if (Test-Path $c) { [IO.File]::Delete($c) } }
# The repo's MIT licence (root LICENSE) ships as LICENSE.txt.
$lic = Join-Path $PSScriptRoot '..\LICENSE'; if (Test-Path $lic) { Copy-Item $lic (Join-Path $stage 'LICENSE.txt') }
foreach ($f in 'README.txt', 'LICENSE.txt', 'THIRD-PARTY-NOTICES.txt') { $p = Join-Path $PSScriptRoot $f; if (Test-Path $p) { Copy-Item $p $stage } }

$exeVersion = (Get-Item (Join-Path $stage 'MHO_Ext_ModManager.exe')).VersionInfo.ProductVersion.Split('+')[0]
if ($exeVersion -ne $Version) { throw "the built exe says $exeVersion, not $Version" }

$name = "MHO_Ext_ModManager-$Version.zip"
$zip = Join-Path $Out $name
if (Test-Path $zip) { [IO.File]::Delete($zip) }
Add-Type -AssemblyName System.IO.Compression.FileSystem
[IO.Compression.ZipFile]::CreateFromDirectory($stageRoot, $zip, [IO.Compression.CompressionLevel]::Optimal, $false)
$hash = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText("$zip.sha256", "$hash  $name`n")
[IO.Directory]::Delete($stageRoot, $true)

Write-Host ''
Write-Host "Built $zip"
Write-Host "SHA-256 $hash"
Write-Host ''
Write-Host 'To publish (the app finds it by the tag):'
Write-Host "  1. https://github.com/leeper48/MHO-UPK-Tools/releases/new"
Write-Host "  2. Tag: extmm-v$Version   Title: MHO Extended Mod Manager $Version"
Write-Host "  3. Notes: what changed (the app shows them in its update window)"
Write-Host "  4. Attach both files: $name and $name.sha256, then Publish release"
Write-Host "  5. Nexus: upload the same zip as a new main file version $Version"
