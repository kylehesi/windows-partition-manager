<#
.SYNOPSIS
    Builds the release artifacts of Windows Partition Manager.

.DESCRIPTION
    1. Runs the unit tests.
    2. Publishes the app and the CLI as self-contained win-x64 builds (no .NET install needed):
         artifacts\install\    the folder the installer installs (app + CLI share one runtime)
         artifacts\portable\   WindowsPartitionManager.exe and wpm.exe as single-file executables
    3. Signs our own binaries when a code-signing certificate is available.
    4. Builds artifacts\WindowsPartitionManager-<version>-x64.msi with WiX Toolset 5.
    5. Zips the portable build, writes SHA256SUMS.txt and scans everything with Microsoft Defender.

    Choices made to avoid antivirus false positives:
      - no packers, obfuscators or compressed single-file bundles;
      - single-file executables do not extract native DLLs to %TEMP% at start-up
        (IncludeNativeLibrariesForSelfExtract is off; the few native DLLs ship next to the exe);
      - full version and publisher information in every executable;
      - a standard Windows Installer package instead of a custom setup program.

.PARAMETER CertificateThumbprint
    Thumbprint of a code-signing certificate in the current user's or local machine's store.
    Defaults to $env:WPM_SIGN_THUMBPRINT. Local unsigned builds require -AllowUnsigned.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File build\package.ps1
#>
[CmdletBinding()]
param(
    [string]$CertificateThumbprint = $env:WPM_SIGN_THUMBPRINT,
    [string]$TimestampUrl = 'http://timestamp.digicert.com',
    [switch]$AllowUnsigned,
    [switch]$SkipTests,
    [switch]$SkipScan
)

$ErrorActionPreference = 'Stop'
if (-not $CertificateThumbprint -and -not $AllowUnsigned) {
    throw 'Release packages require a code-signing certificate. For local testing, pass -AllowUnsigned.'
}
$root = Split-Path -Parent $PSScriptRoot
$artifacts = Join-Path $root 'artifacts'
$runtime = 'win-x64'

function Step($text) { Write-Host "`n=== $text" -ForegroundColor Cyan }

function Find-Tool($name, [string[]]$candidates) {
    $cmd = Get-Command $name -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    foreach ($c in $candidates) { if ($c -and (Test-Path $c)) { return $c } }
    return $null
}

function Invoke-Checked($exe, [string[]]$arguments) {
    & $exe @arguments
    if ($LASTEXITCODE -ne 0) { throw "$([IO.Path]::GetFileName($exe)) failed with exit code $LASTEXITCODE" }
}

$dotnet = Find-Tool 'dotnet' @("$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe", "$env:ProgramFiles\dotnet\dotnet.exe")
if (-not $dotnet) { throw 'The .NET 10 SDK was not found.' }
$env:DOTNET_ROOT = Split-Path -Parent $dotnet
$env:DOTNET_NOLOGO = '1'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'

$wix = Find-Tool 'wix' @("$env:USERPROFILE\.dotnet\tools\wix.exe")
if (-not $wix) { throw 'WiX Toolset 5 was not found. Install it with: dotnet tool install --global wix --version 5.0.2; wix extension add -g WixToolset.UI.wixext/5.0.2' }

[xml]$props = Get-Content (Join-Path $root 'Directory.Build.props')
$version = ($props.Project.PropertyGroup | Where-Object { $_.Version } | Select-Object -First 1).Version
if (-not $version) { throw 'Version not found in Directory.Build.props.' }
Write-Host "Windows Partition Manager $version ($runtime)"

if (Test-Path $artifacts) { Remove-Item $artifacts -Recurse -Force }
$install = Join-Path $artifacts 'install'
$portable = Join-Path $artifacts 'portable'
New-Item -ItemType Directory -Force $install, $portable | Out-Null

$app = Join-Path $root 'src\WindowsPartitionManager.App\WindowsPartitionManager.App.csproj'
$cli = Join-Path $root 'src\WindowsPartitionManager.Cli\WindowsPartitionManager.Cli.csproj'
$common = @('-c', 'Release', '-r', $runtime, '--self-contained', 'true', '-p:DebugType=none', '-p:DebugSymbols=false', '--nologo')

if (-not $SkipTests) {
    Step 'Unit tests'
    Invoke-Checked $dotnet @('test', (Join-Path $root 'tests\WindowsPartitionManager.Core.Tests'), '-c', 'Release', '--nologo')
}

Step 'Publish: installer folder'
Invoke-Checked $dotnet (@('publish', $app, '-o', $install) + $common)
Invoke-Checked $dotnet (@('publish', $cli, '-o', $install) + $common)

# Reject SDKs that would ship the vulnerable runtime in a self-contained installer.
$runtimeConfig = Get-Content (Join-Path $install 'WindowsPartitionManager.runtimeconfig.json') -Raw | ConvertFrom-Json
$coreRuntime = $runtimeConfig.runtimeOptions.includedFrameworks | Where-Object name -eq 'Microsoft.NETCore.App'
if (-not $coreRuntime -or [version]$coreRuntime.version -lt [version]'10.0.15') {
    throw 'Install an updated .NET 10 SDK. Release packages require runtime 10.0.15 or later.'
}

Step 'Publish: portable single-file executables'
$single = @('-p:PublishSingleFile=true', '-p:IncludeNativeLibrariesForSelfExtract=false', '-p:EnableCompressionInSingleFile=false')
Invoke-Checked $dotnet (@('publish', $app, '-o', $portable) + $common + $single)
Invoke-Checked $dotnet (@('publish', $cli, '-o', $portable) + $common + $single)
# The license and the disclaimer travel with every copy, installed or portable.
foreach ($target in $portable, $install) {
    Copy-Item (Join-Path $root 'LICENSE') (Join-Path $target 'LICENSE.txt')
    Copy-Item (Join-Path $root 'DISCLAIMER.txt') (Join-Path $target 'DISCLAIMER.txt')
}

# Our own binaries; the .NET runtime files are already signed by Microsoft.
$ownFiles = @(
    Get-ChildItem $install, $portable -File -Include 'WindowsPartitionManager*.exe', 'WindowsPartitionManager*.dll', 'wpm.exe', 'wpm.dll' -Recurse
)

$signtool = $null
if ($CertificateThumbprint) {
    $kits = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin" -Directory -ErrorAction SilentlyContinue | Sort-Object Name -Descending
    $signtool = Find-Tool 'signtool' @($kits | ForEach-Object { Join-Path $_.FullName 'x64\signtool.exe' })
    if (-not $signtool) { throw 'A certificate thumbprint was given but signtool.exe (Windows SDK) was not found.' }
}

function Sign-Files([string[]]$paths) {
    if (-not $signtool) { return }
    Invoke-Checked $signtool (@('sign', '/sha1', $CertificateThumbprint, '/fd', 'SHA256', '/tr', $TimestampUrl, '/td', 'SHA256', '/d', 'Windows Partition Manager') + $paths)
}

if ($signtool) {
    Step 'Sign binaries'
    Sign-Files ($ownFiles | ForEach-Object FullName)
}
else {
    Write-Warning 'No code-signing certificate: binaries and installer are unsigned. SmartScreen will show "Windows protected your PC" for downloads until the publisher is signed or gains reputation.'
}

Step 'Build MSI'
$msi = Join-Path $artifacts "WindowsPartitionManager-$version-x64.msi"
Push-Location (Join-Path $root 'installer')
try {
    Invoke-Checked $wix @('build', 'Package.wxs', '-arch', 'x64', '-ext', 'WixToolset.UI.wixext', '-ext', 'WixToolset.Util.wixext', '-d', "Version=$version", '-d', "PublishDir=$install", '-o', $msi)
}
finally {
    Pop-Location
}
Remove-Item ([IO.Path]::ChangeExtension($msi, '.wixpdb')) -ErrorAction SilentlyContinue
Sign-Files @($msi)

# The installer also lives in the repository root under a fixed name, so it is the first thing
# people see; the versioned copy stays in artifacts\ for releases.
$setup = Join-Path $root 'WindowsPartitionManager-Setup.msi'
Copy-Item $msi $setup -Force

Step 'Portable zip'
$zip = Join-Path $artifacts "WindowsPartitionManager-$version-portable-x64.zip"
Compress-Archive -Path (Join-Path $portable '*') -DestinationPath $zip

Step 'Checksums'
$sums = Join-Path $artifacts 'SHA256SUMS.txt'
@(Get-ChildItem $artifacts -File -Include *.msi, *.zip -Recurse) + @(Get-Item $setup) | ForEach-Object {
    '{0}  {1}' -f (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant(), $_.Name
} | Set-Content $sums -Encoding ascii
Get-Content $sums

if (-not $SkipScan) {
    Step 'Microsoft Defender scan'
    $mpcmd = Join-Path $env:ProgramFiles 'Windows Defender\MpCmdRun.exe'
    if (Test-Path $mpcmd) {
        foreach ($target in $artifacts, $setup) {
            & $mpcmd -Scan -ScanType 3 -File $target -DisableRemediation | Out-Host
            if ($LASTEXITCODE -eq 0) { Write-Host "Defender: no threats found in $target." -ForegroundColor Green }
            elseif ($LASTEXITCODE -eq 2) { throw "Defender reported a threat in $target. Do not publish it; see README (antivirus false positives)." }
            else { Write-Warning "Defender scan exit code $LASTEXITCODE for $target" }
        }
    }
    else {
        Write-Warning 'MpCmdRun.exe not found; scan skipped.'
    }
}

Step 'Done'
@(Get-Item $setup) + @(Get-ChildItem $artifacts -File) | Select-Object FullName, @{ n = 'MB'; e = { [Math]::Round($_.Length / 1MB, 1) } } | Format-Table -AutoSize | Out-Host
