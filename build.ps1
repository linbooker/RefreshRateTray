# Build RefreshRateTray with the .NET Framework compiler shipped with Windows.
# (No SDK required. Script text is ASCII-only on purpose: Windows PowerShell 5.1
#  reads BOM-less files using the ANSI codepage, which would garble Chinese text.)
$ErrorActionPreference = 'Stop'

$root   = Split-Path -Parent $PSCommandPath
$src    = Join-Path $root 'src\RefreshRateTray.cs'
$dist   = Join-Path $root 'dist'
$pocket = Join-Path $root 'Pocket'

$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) {
    $csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe'
}
if (-not (Test-Path $csc)) { throw 'csc.exe (.NET Framework 4.x) not found' }

New-Item -ItemType Directory -Force -Path $dist   | Out-Null
New-Item -ItemType Directory -Force -Path $pocket | Out-Null

$common = @(
    '/nologo',
    '/optimize+',
    '/platform:anycpu',
    '/codepage:65001',
    '/r:System.dll',
    '/r:System.Drawing.dll',
    '/r:System.Windows.Forms.dll'
)

# ---------------------------------------------------------------- registry build
Write-Host 'Building tray app RefreshRateTray.exe ...'
& $csc $common '/target:winexe' "/out:$dist\RefreshRateTray.exe" $src
if ($LASTEXITCODE -ne 0) { throw "tray build failed ($LASTEXITCODE)" }

Write-Host 'Building CLI RefreshRateTray.cli.exe ...'
& $csc $common '/target:exe' '/define:CLI_BUILD' "/out:$dist\RefreshRateTray.cli.exe" $src
if ($LASTEXITCODE -ne 0) { throw "cli build failed ($LASTEXITCODE)" }

# ---------------------------------------------------------------- pocket build
# Portable: settings + log live next to the exe (settings.ini / app.log), nothing
# is written to the registry except the optional Run key used by "start with Windows".
Write-Host 'Building pocket tray app Pocket\RefreshRateTray.exe ...'
& $csc $common '/define:POCKET_BUILD' '/target:winexe' "/out:$pocket\RefreshRateTray.exe" $src
if ($LASTEXITCODE -ne 0) { throw "pocket tray build failed ($LASTEXITCODE)" }

Write-Host 'Building pocket CLI Pocket\RefreshRateTray.cli.exe ...'
& $csc $common '/define:CLI_BUILD;POCKET_BUILD' '/target:exe' "/out:$pocket\RefreshRateTray.cli.exe" $src
if ($LASTEXITCODE -ne 0) { throw "pocket cli build failed ($LASTEXITCODE)" }

# Default settings file (copied from the UTF-8 template; never overwrite user edits)
$iniTemplate = Join-Path $root 'src\settings.ini.template'
$ini = Join-Path $pocket 'settings.ini'
if ((Test-Path $iniTemplate) -and -not (Test-Path $ini)) {
    Copy-Item $iniTemplate $ini
}

$pocketReadme = Join-Path $root 'README-Pocket.md'
if (Test-Path $pocketReadme) {
    Copy-Item $pocketReadme (Join-Path $pocket 'README.md') -Force
}

Write-Host ''
Get-ChildItem $dist -Filter *.exe | Select-Object @{n='Folder';e={'dist'}}, Name, Length, LastWriteTime | Format-Table -AutoSize
Get-ChildItem $pocket -File | Select-Object @{n='Folder';e={'Pocket'}}, Name, Length, LastWriteTime | Format-Table -AutoSize
Write-Host 'Build finished.'