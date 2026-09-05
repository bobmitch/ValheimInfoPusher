#Requires -Version 5.1
<#
.SYNOPSIS
    Builds the ValheimRelay BepInEx plugin on a PC with Valheim installed.

.DESCRIPTION
    Core and its tests build anywhere (`dotnet test ValheimRelay.sln`). The
    plugin is different: it references the game's own assemblies, so it needs a
    real install. This script finds that install, checks the toolchain, builds,
    and optionally copies the result into BepInEx/plugins.

    Deliberately ASCII-only: Windows PowerShell 5.1 reads unsigned scripts as
    ANSI unless they carry a BOM, and a stray non-ASCII character in a string
    turns into mojibake on someone else's code page.

.PARAMETER ValheimInstall
    The Valheim directory (the one containing valheim_Data). Defaults to
    $env:VALHEIM_INSTALL, then to whatever Steam's registry key and library
    folders turn up.

.PARAMETER Configuration
    Debug or Release. Defaults to Release.

.PARAMETER Deploy
    Copy the built assemblies into BepInEx/plugins/ValheimRelay after building.

.PARAMETER PluginsDir
    Where -Deploy copies to. Defaults to <install>/BepInEx/plugins/ValheimRelay.
    Point this at a profile directory if you manage mods with r2modman.

.PARAMETER SkipTests
    Skip the Core test run. The tests need no game and catch a broken toolchain
    before the game references can confuse the diagnosis, so prefer leaving
    them on.

.PARAMETER Package
    Assemble a Thunderstore-ready zip in dist/ from the built assemblies and
    the files in packaging/. Implies a build; does not need -Deploy.

.PARAMETER Clean
    Delete bin/ and obj/ under the plugin first. Do this after a game update:
    the publicized copy of assembly_valheim is cached in obj/ and will
    otherwise be reused against the new game version.

.EXAMPLE
    .\build.ps1

.EXAMPLE
    .\build.ps1 -Deploy

.EXAMPLE
    .\build.ps1 -Clean -Deploy -ValheimInstall 'D:\Steam\steamapps\common\Valheim'

.EXAMPLE
    .\build.ps1 -Package
#>
[CmdletBinding()]
param(
    [string] $ValheimInstall,
    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Release',
    [switch] $Deploy,
    [string] $PluginsDir,
    [switch] $SkipTests,
    [switch] $Package,
    [switch] $Clean
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$RepoRoot    = $PSScriptRoot
$PluginProj  = Join-Path $RepoRoot 'src\ValheimRelay.Plugin'
$Solution    = Join-Path $RepoRoot 'ValheimRelay.sln'
$PackageDir  = Join-Path $RepoRoot 'packaging'
$DistDir     = Join-Path $RepoRoot 'dist'

function Write-Step { param([string] $Text) Write-Host "`n==> $Text" -ForegroundColor Cyan }
function Write-Note { param([string] $Text) Write-Host "    $Text" -ForegroundColor DarkGray }

function Invoke-Dotnet {
    # dotnet reports failure through the exit code, not through a terminating
    # error, so $ErrorActionPreference alone will not stop a broken build from
    # marching on to the deploy step.
    param([Parameter(ValueFromRemainingArguments = $true)] [string[]] $Arguments)
    Write-Note "dotnet $($Arguments -join ' ')"
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet $($Arguments -join ' ') failed with exit code $LASTEXITCODE."
    }
}

function Get-SteamLibraryRoots {
    $roots = @()
    # Only Windows has the registry key; on pwsh elsewhere fall through and let
    # -ValheimInstall or $env:VALHEIM_INSTALL do the work.
    if ($env:OS -eq 'Windows_NT') {
        foreach ($key in 'HKCU:\Software\Valve\Steam', 'HKLM:\SOFTWARE\WOW6432Node\Valve\Steam') {
            $prop = Get-ItemProperty -Path $key -ErrorAction SilentlyContinue
            if ($prop -and $prop.PSObject.Properties['SteamPath'])        { $roots += $prop.SteamPath }
            if ($prop -and $prop.PSObject.Properties['InstallPath'])      { $roots += $prop.InstallPath }
        }
        $roots += 'C:\Program Files (x86)\Steam'
    }

    # Steam keeps additional drives in libraryfolders.vdf, with backslashes
    # escaped. A second library is the common case once the C: drive fills up.
    foreach ($root in @($roots)) {
        $vdf = Join-Path $root 'steamapps\libraryfolders.vdf'
        if (Test-Path -LiteralPath $vdf) {
            $matched = Select-String -Path $vdf -Pattern '"path"\s+"(.+?)"' -AllMatches
            if ($matched) {
                foreach ($m in $matched.Matches) {
                    $roots += ($m.Groups[1].Value -replace '\\\\', '\')
                }
            }
        }
    }

    $roots | Where-Object { $_ } | Select-Object -Unique
}

function Resolve-ValheimInstall {
    param([string] $Explicit)

    $candidates = @()
    if ($Explicit)              { $candidates += $Explicit }
    if ($env:VALHEIM_INSTALL)   { $candidates += $env:VALHEIM_INSTALL }
    $candidates += (Get-SteamLibraryRoots | ForEach-Object { Join-Path $_ 'steamapps\common\Valheim' })

    foreach ($candidate in ($candidates | Where-Object { $_ })) {
        # assembly_valheim.dll, not just the folder: an uninstall leaves the
        # directory behind, and a hit on an empty one produces a build failure
        # that reads like a compiler problem.
        $probe = Join-Path $candidate 'valheim_Data\Managed\assembly_valheim.dll'
        if (Test-Path -LiteralPath $probe) {
            return (Resolve-Path -LiteralPath $candidate).ProviderPath
        }
    }

    if ($Explicit) {
        throw "No Valheim install at '$Explicit' (looked for valheim_Data\Managed\assembly_valheim.dll)."
    }
    throw @"
Could not find Valheim. Pass the directory explicitly:

    .\build.ps1 -ValheimInstall 'D:\Steam\steamapps\common\Valheim'

It is the folder containing valheim_Data. Core and its tests build without it:

    dotnet test ValheimRelay.sln
"@
}

function Assert-DotnetSdk {
    $dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
    if (-not $dotnet) {
        throw @"
The .NET SDK is not on PATH. Install it, then open a new shell:

    winget install --id Microsoft.DotNet.SDK.8 -e
"@
    }

    # The tests target net8.0; the plugin needs an SDK new enough to target
    # net472 with the reference-assemblies package. An 8.0 SDK covers both.
    $majors = & dotnet --list-sdks |
        ForEach-Object { if ($_ -match '^(\d+)\.') { [int] $Matches[1] } }
    if (-not $majors -or (($majors | Measure-Object -Maximum).Maximum -lt 8)) {
        throw "Need .NET SDK 8.0 or newer. Found: $((& dotnet --list-sdks) -join '; ')"
    }
    Write-Note "dotnet SDK $((& dotnet --version))"
}

# --- build ------------------------------------------------------------------

Write-Step 'Checking the toolchain'
Assert-DotnetSdk

Write-Step 'Locating Valheim'
$install = Resolve-ValheimInstall -Explicit $ValheimInstall
Write-Note $install
# Directory.Build.props reads this; setting it here means the caller does not
# have to, and a -ValheimInstall argument beats a stale persisted variable.
$env:VALHEIM_INSTALL = $install

if ($Clean) {
    Write-Step 'Cleaning'
    foreach ($dir in 'bin', 'obj') {
        $path = Join-Path $PluginProj $dir
        if (Test-Path -LiteralPath $path) {
            Write-Note "removing $path"
            Remove-Item -LiteralPath $path -Recurse -Force
        }
    }
}

if (-not $SkipTests) {
    Write-Step 'Testing Core (no game required)'
    Invoke-Dotnet test $Solution -c $Configuration
}

Write-Step "Building the plugin ($Configuration)"
Invoke-Dotnet build $PluginProj -c $Configuration

# AppendTargetFrameworkToOutputPath is off in the csproj, so the output is flat.
$outDir = Join-Path $PluginProj "bin\$Configuration"
$artifacts = @(
    (Join-Path $outDir 'ValheimRelay.dll'),
    (Join-Path $outDir 'ValheimRelay.Core.dll')
)
foreach ($artifact in $artifacts) {
    if (-not (Test-Path -LiteralPath $artifact)) {
        throw "Build reported success but '$artifact' is missing."
    }
}

Write-Step 'Built'
foreach ($artifact in $artifacts) { Write-Host "    $artifact" }

# --- package ----------------------------------------------------------------

if ($Package) {
    Write-Step 'Packaging for Thunderstore'

    $manifestPath  = Join-Path $PackageDir 'manifest.json'
    $iconPath      = Join-Path $PackageDir 'icon.png'
    $readmePath    = Join-Path $PackageDir 'README.md'
    $changelogPath = Join-Path $PackageDir 'CHANGELOG.md'

    # Thunderstore rejects the upload if any of these is missing from the zip
    # root, which is a slow and irritating way to find out. Check up front.
    foreach ($required in $manifestPath, $iconPath, $readmePath) {
        if (-not (Test-Path -LiteralPath $required)) {
            throw "Thunderstore requires $(Split-Path -Leaf $required) at the zip root, but '$required' does not exist."
        }
    }

    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    if (-not $manifest.PSObject.Properties['version_number']) {
        throw "packaging/manifest.json has no version_number."
    }
    $version = $manifest.version_number

    # The manifest version and the assembly version drift apart easily, and
    # nothing downstream notices: the mod manager shows one, the BepInEx log
    # prints the other, and the bug report cites whichever the reporter saw.
    $assemblyVersion = [System.Reflection.AssemblyName]::GetAssemblyName($artifacts[0]).Version
    $assemblyShort = '{0}.{1}.{2}' -f $assemblyVersion.Major, $assemblyVersion.Minor, $assemblyVersion.Build
    if ($assemblyShort -ne $version) {
        throw @"
Version mismatch: packaging/manifest.json says $version, ValheimRelay.dll says $assemblyShort.
These are bumped together, in three places:

    packaging/manifest.json                            version_number
    src/ValheimRelay.Plugin/ValheimRelay.Plugin.csproj  <Version>
    src/ValheimRelay.Plugin/Plugin.cs                   PluginVersion
"@
    }

    # icon.png must be exactly 256x256. The dimensions live in the PNG's IHDR
    # chunk at a fixed offset, big-endian -- cheaper than decoding the image,
    # and this is the other thing Thunderstore rejects on upload.
    $png = [System.IO.File]::ReadAllBytes($iconPath)
    if ($png.Length -lt 24) { throw "'$iconPath' is too short to be a PNG." }

    # Confirm the signature and the IHDR tag before trusting the offsets below.
    # A file that is not a PNG -- or one some tool has mangled on the way to
    # disk -- otherwise reports whatever those bytes happen to hold, which
    # sends you off regenerating an icon that was never the problem.
    $pngHeader = [byte[]] (
        0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A,   # PNG signature
        0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52    # IHDR length + tag
    )
    for ($i = 0; $i -lt $pngHeader.Length; $i++) {
        if ($png[$i] -ne $pngHeader[$i]) {
            throw "'$iconPath' is not a PNG: expected a signature followed by an IHDR chunk. Regenerate it with packaging/make-icon.py."
        }
    }

    # Widen each byte to int before shifting. -shl returns the type of its left
    # operand, so shifting a [byte] drops everything past bit 7: every term but
    # the last is 0 and a 256x256 icon reads back as 0x0, failing the check
    # below for every image ever passed to it.
    $iconW = ((([int] $png[16]) -shl 24) -bor (([int] $png[17]) -shl 16) -bor (([int] $png[18]) -shl 8) -bor ([int] $png[19]))
    $iconH = ((([int] $png[20]) -shl 24) -bor (([int] $png[21]) -shl 16) -bor (([int] $png[22]) -shl 8) -bor ([int] $png[23]))
    if ($iconW -ne 256 -or $iconH -ne 256) {
        throw "icon.png must be 256x256; '$iconPath' is ${iconW}x${iconH}. Regenerate it with packaging/make-icon.py."
    }

    $staging = Join-Path $DistDir 'staging'
    if (Test-Path -LiteralPath $staging) { Remove-Item -LiteralPath $staging -Recurse -Force }
    New-Item -ItemType Directory -Force -Path (Join-Path $staging 'plugins') | Out-Null

    Copy-Item -LiteralPath $manifestPath, $iconPath, $readmePath -Destination $staging
    if (Test-Path -LiteralPath $changelogPath) {
        Copy-Item -LiteralPath $changelogPath -Destination $staging
    }
    foreach ($artifact in $artifacts) {
        Copy-Item -LiteralPath $artifact -Destination (Join-Path $staging 'plugins')
    }

    if (-not ('System.IO.Compression.ZipFile' -as [type])) {
        Add-Type -AssemblyName System.IO.Compression.FileSystem
    }

    $zipPath = Join-Path $DistDir "ValheimRelay-$version.zip"
    if (Test-Path -LiteralPath $zipPath) { Remove-Item -LiteralPath $zipPath -Force }

    # Entry names are written by hand with forward slashes rather than through
    # CreateFromDirectory: Windows PowerShell has shipped zip writers that emit
    # backslashes for nested directories, and an extractor that takes them
    # literally produces a single file named 'plugins\ValheimRelay.dll' -- which
    # installs cleanly and then never loads.
    $zip = [System.IO.Compression.ZipFile]::Open($zipPath, 'Create')
    try {
        foreach ($file in (Get-ChildItem -LiteralPath $staging -Recurse -File | Sort-Object FullName)) {
            $entry = $file.FullName.Substring($staging.Length + 1).Replace('\', '/')
            [void] [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $file.FullName, $entry)
            Write-Note $entry
        }
    }
    finally {
        $zip.Dispose()
    }

    Remove-Item -LiteralPath $staging -Recurse -Force

    Write-Step 'Packaged'
    Write-Host "    $zipPath"
    Write-Note 'Upload it at https://thunderstore.io/c/valheim/create/'
}

# --- deploy -----------------------------------------------------------------

if (-not $Deploy) {
    Write-Host "`nRe-run with -Deploy to copy these into BepInEx/plugins, or -Package for a Thunderstore zip." -ForegroundColor DarkGray
    return
}

if (-not $PluginsDir) {
    $PluginsDir = Join-Path $install 'BepInEx\plugins\ValheimRelay'
}

Write-Step "Deploying to $PluginsDir"

# A missing BepInEx is the single most common reason a correctly built plugin
# does nothing at all, and it is silent: the game just starts normally.
$bepInExRoot = Join-Path $install 'BepInEx'
if (-not (Test-Path -LiteralPath $bepInExRoot)) {
    Write-Warning @"
No BepInEx directory in the game folder. Install the BepInEx pack for Valheim
(Thunderstore: denikson-BepInExPack_Valheim) and launch the game once, or the
plugin will never be loaded. Copying anyway.
"@
}

New-Item -ItemType Directory -Force -Path $PluginsDir | Out-Null

$toCopy = $artifacts
if ($Configuration -eq 'Debug') {
    $toCopy += @(Get-ChildItem -Path $outDir -Filter 'ValheimRelay*.pdb' -ErrorAction SilentlyContinue |
                 ForEach-Object { $_.FullName })
}

foreach ($file in $toCopy) {
    Write-Note "copy $(Split-Path -Leaf $file)"
    Copy-Item -LiteralPath $file -Destination $PluginsDir -Force
}

Write-Step 'Done'
Write-Host "    Launch Valheim, then check $bepInExRoot\LogOutput.log for a ValheimRelay line."
