# Builds a release of MHO Extended Mod Manager for GitHub (self-update) and Nexus:
#   releases\MHO_Ext_ModManager-<version>.zip               the app in a folder "MHO Extended Mod Manager" (no data\, no .pdb)
#   releases\MHO_Ext_ModManager-<version>.zip.sha256        its SHA-256 (the app refuses an update without a matching one)
#   releases\MHO_Ext_ModManager-<version>-Setup.exe (+ .sha256)  the installer (installer.iss), when Inno Setup 6 is installed
#
#   powershell -ExecutionPolicy Bypass -File make_release.ps1                 build everything, version from the csproj
#   ... -NoChecksums                                                           leave StockData\upk_checksums.json out
#   ... -StageOnly                                                             only build the app folder (<Out>\stage\...)
#   ... -FromStage <folder>                                                    package an existing app folder
#   ... -NoInstaller                                                           zip only
#   ... -Version 9.9.9 -Out <folder>                                           tests only (also stamps MPM with that version)
#
# The GitHub workflow .github\workflows\extmm-release.yml runs this for a pushed tag extmm-v<version>. Builds are not code-signed.
param([string]$Version, [string]$Out, [switch]$NoChecksums, [switch]$StageOnly, [string]$FromStage, [switch]$NoInstaller)
$ErrorActionPreference = 'Stop'
$proj = Join-Path $PSScriptRoot 'MhoExtendedModManager.csproj'
$stampVersion = [bool]$Version
if (-not $Version) { $Version = ([xml](Get-Content $proj)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1 }
if (-not $Out) { $Out = Join-Path $PSScriptRoot 'releases' }
New-Item -ItemType Directory -Force $Out | Out-Null
$Out = (Resolve-Path $Out).Path

if ($FromStage) {
    $stage = (Resolve-Path $FromStage).Path
} else {
    $stage = Join-Path $Out 'stage\MHO Extended Mod Manager'
    if (Test-Path (Split-Path $stage)) { [IO.Directory]::Delete((Split-Path $stage), $true) }
    New-Item -ItemType Directory -Force $stage | Out-Null
    $publishArgs = @('publish', $proj, '-c', 'Release', '-r', 'win-x64', '--self-contained', 'false', '-p:PublishSingleFile=true', '-o', $stage)
    if ($stampVersion) { $publishArgs += "-p:Version=$Version" }
    & dotnet @publishArgs
    if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed' }

    Get-ChildItem $stage -Recurse -Filter *.pdb | ForEach-Object { [IO.File]::Delete($_.FullName) }
    if (Test-Path (Join-Path $stage 'data')) { [IO.Directory]::Delete((Join-Path $stage 'data'), $true) }
    # MPM's launcher: its code is inside the single-file exe, the stub alone can't run (the manager never starts it).
    # (AnimExportCli, the skeletal mesh reader for the 3D preview, is referenced the same way.)
    foreach ($f in 'MHO_UPK_Mod.exe', 'MHO_UPK_Mod.runtimeconfig.json', 'AnimExportCli.exe', 'AnimExportCli.runtimeconfig.json', 'AppIcon.ico') { $p = Join-Path $stage $f; if (Test-Path $p) { [IO.File]::Delete($p) } }
    if ($NoChecksums) { $c = Join-Path $stage 'StockData\upk_checksums.json'; if (Test-Path $c) { [IO.File]::Delete($c) } }
    # The repo's MIT licence (root LICENSE) ships as LICENSE.txt; README and third-party notices from this folder.
    $lic = Join-Path $PSScriptRoot '..\LICENSE'; if (Test-Path $lic) { Copy-Item $lic (Join-Path $stage 'LICENSE.txt') }
    foreach ($f in 'README.txt', 'THIRD-PARTY-NOTICES.txt') { $p = Join-Path $PSScriptRoot $f; if (Test-Path $p) { Copy-Item $p $stage } }
}

$exeVersion = (Get-Item (Join-Path $stage 'MHO_Ext_ModManager.exe')).VersionInfo.ProductVersion.Split('+')[0]
if ($exeVersion -ne $Version) { throw "the app folder's exe says $exeVersion, not $Version" }
if ($StageOnly) { Write-Host "Staged $stage"; return }

# The zip holds one folder, "MHO Extended Mod Manager".
$name = "MHO_Ext_ModManager-$Version.zip"
$zip = Join-Path $Out $name
if (Test-Path $zip) { [IO.File]::Delete($zip) }
$zipRoot = Join-Path $Out 'zipstage'
if (Test-Path $zipRoot) { [IO.Directory]::Delete($zipRoot, $true) }
New-Item -ItemType Directory -Force $zipRoot | Out-Null
Copy-Item $stage (Join-Path $zipRoot 'MHO Extended Mod Manager') -Recurse
Add-Type -AssemblyName System.IO.Compression.FileSystem
# Entries written one by one with "/" (Windows PowerShell's CreateFromDirectory writes "\", which other unzip tools mishandle).
Add-Type -AssemblyName System.IO.Compression
$zs = [IO.File]::Create($zip)
$za = New-Object IO.Compression.ZipArchive($zs, [IO.Compression.ZipArchiveMode]::Create)
Get-ChildItem $zipRoot -Recurse -File | ForEach-Object {
    $rel = $_.FullName.Substring($zipRoot.Length + 1).Replace('\', '/')
    [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($za, $_.FullName, $rel, [IO.Compression.CompressionLevel]::Optimal) | Out-Null
}
$za.Dispose(); $zs.Dispose()
[IO.Directory]::Delete($zipRoot, $true)
$hash = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText("$zip.sha256", "$hash  $name`n")

# The installer (installer.iss), when Inno Setup 6 is installed: same files, per-user install.
$setup = $null
$iscc = @("C:\Tools\Inno Setup 6\ISCC.exe", "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:ProgramFiles\Inno Setup 6\ISCC.exe", "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe") | Where-Object { Test-Path $_ } | Select-Object -First 1
if ($NoInstaller) { }
elseif ($iscc) {
    & $iscc /Q "/DAppVersion=$Version" "/DSourceDir=$stage" "/O$Out" (Join-Path $PSScriptRoot 'installer.iss')
    if ($LASTEXITCODE -ne 0) { throw 'Inno Setup failed' }
    $setup = Join-Path $Out "MHO_Ext_ModManager-$Version-Setup.exe"
    $sh = (Get-FileHash $setup -Algorithm SHA256).Hash.ToLowerInvariant()
    [IO.File]::WriteAllText("$setup.sha256", "$sh  $(Split-Path $setup -Leaf)`n")
} else { Write-Host 'Inno Setup 6 not found: no installer built (the zip is).' }
if (-not $FromStage) { [IO.Directory]::Delete((Split-Path $stage), $true) }

Write-Host ''
Write-Host "Built $zip"
Write-Host "SHA-256 $hash"
if ($setup) { Write-Host "Installer: $setup" }
Write-Host ''
Write-Host 'To publish by hand (or push a tag extmm-v<version>: the GitHub workflow drafts the release):'
Write-Host "  1. https://github.com/leeper48/MHO-UPK-Tools/releases/new"
Write-Host "  2. Tag: extmm-v$Version   Title: MHO Extended Mod Manager $Version"
Write-Host "  3. Notes: what changed (the app shows them in its update window)"
Write-Host "  4. Attach the zip, the Setup.exe and their .sha256 files, then Publish release"
Write-Host "  5. Nexus: upload the Setup.exe (and / or the zip) as main file version $Version"
