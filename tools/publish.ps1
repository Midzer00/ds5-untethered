# SPDX-License-Identifier: GPL-3.0-or-later
# Builds the user download: artifacts\DS5-Untethered-<version>-win-x64.zip, plus
# artifacts\DS5-Untethered-<version>-Setup.exe when Inno Setup 6 is installed (tools\installer\DS5Untethered.iss).
#
#   powershell -ExecutionPolicy Bypass -File tools\publish.ps1
#
# Self-contained win-x64 folder build (no .NET runtime needed on the user's PC), no PDBs, deterministic, with the
# repository path mapped away. Before zipping, every file is scanned for this machine's user name, profile path and
# computer name; the script fails if any of them is found.

[CmdletBinding()]
param(
	[string]$Configuration = 'Release',
	[string]$Runtime = 'win-x64',
	# The folder the app is published into before zipping (default artifacts\staging\<name>); emptied first.
	[string]$StagingDir = ''
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path.TrimEnd('\')
$project = Join-Path $repo 'src\Kopru.App\Kopru.App.csproj'
$artifacts = Join-Path $repo 'artifacts'
$releaseFiles = Join-Path $PSScriptRoot 'release'

function Fail([string]$message) {
	Write-Host "ERROR: $message" -ForegroundColor Red
	exit 1
}

# --- Version (Directory.Build.props) ---
$props = (& dotnet msbuild $project -nologo -getProperty:Version -getProperty:InformationalVersion | Out-String)
if ($LASTEXITCODE -ne 0) {
	Fail 'Could not read the version from the project.'
}
$props = $props | ConvertFrom-Json
$version = [string]$props.Properties.InformationalVersion
$numericVersion = [string]$props.Properties.Version
if ([string]::IsNullOrWhiteSpace($version) -or $numericVersion -notmatch '^\d+\.\d+\.\d+(\.\d+)?$') {
	Fail "Unexpected version: '$version' / '$numericVersion'."
}
$name = "DS5-Untethered-$version-$Runtime"
$staging = Join-Path $artifacts "staging\$name"
if (-not [string]::IsNullOrWhiteSpace($StagingDir)) {
	$staging = [System.IO.Path]::GetFullPath($StagingDir).TrimEnd('\')
	if (-not $staging.StartsWith($artifacts + '\', [System.StringComparison]::OrdinalIgnoreCase)) {
		Fail 'StagingDir must be inside the artifacts folder (it is emptied first).'
	}
}
$zip = Join-Path $artifacts "$name.zip"
$iss = Join-Path $PSScriptRoot 'installer\DS5Untethered.iss'
$setup = Join-Path $artifacts "DS5-Untethered-$version-Setup.exe"
Write-Host "Version: $version"

if (Test-Path $staging) {
	# A copy started from the staging folder (to try the build) would lose files to a half-done delete: stop first.
	$running = @(Get-Process -ErrorAction SilentlyContinue | Where-Object {
		$path = $null
		try { $path = $_.Path } catch { }
		$path -and $path.StartsWith($staging + '\', [System.StringComparison]::OrdinalIgnoreCase)
	})
	if ($running.Count -gt 0) {
		Fail ("A program is running from the staging folder (" + (($running | ForEach-Object { "$($_.Name) $($_.Id)" }) -join ', ') +
			"). Exit it, or pass -StagingDir with another folder under artifacts.")
	}
	Remove-Item -Recurse -Force $staging
}
foreach ($old in @($zip, $setup)) {
	if (Test-Path $old) {
		Remove-Item -Force $old
	}
}
New-Item -ItemType Directory -Force $staging | Out-Null

# --- Publish ---
# Folder (not single-file): Avalonia's native libraries (Skia, HarfBuzz, ANGLE) would have to be self-extracted to a temp
# folder at every start, and HidHide allows the app by its exe path, which stays simple and stable with a plain folder.
# No trimming: Avalonia and the compiled bindings rely on reflection.
$publishArgs = @(
	'publish', $project,
	'-c', $Configuration,
	'-r', $Runtime,
	'--self-contained', 'true',
	'-o', $staging,
	'-nologo',
	'-p:PublishSingleFile=false',
	'-p:PublishTrimmed=false',
	'-p:DebugType=none',
	'-p:DebugSymbols=false',
	'-p:GenerateDocumentationFile=false',
	'-p:Deterministic=true',
	'-p:ContinuousIntegrationBuild=true',
	"-p:PathMap=$repo=C:\src",
	'-p:SatelliteResourceLanguages=en'
)
& dotnet @publishArgs
if ($LASTEXITCODE -ne 0) {
	Fail "dotnet publish failed (exit $LASTEXITCODE)."
}

# --- Files next to the app ---
Copy-Item (Join-Path $repo 'LICENSE') $staging
Copy-Item (Join-Path $repo 'THIRD-PARTY-NOTICES.md') $staging
Copy-Item (Join-Path $releaseFiles 'README.txt') $staging

# --- Licence files of shipped components (THIRD-PARTY-NOTICES.md points here), taken from the packages used ---
$nuget = $env:NUGET_PACKAGES
if ([string]::IsNullOrWhiteSpace($nuget)) {
	$nuget = Join-Path $env:USERPROFILE '.nuget\packages'
}
$deps = Get-Content (Join-Path $staging 'Kopru.App.deps.json') -Raw
function PackageVersion([string]$id) {
	$m = [regex]::Match($deps, '"' + [regex]::Escape($id) + '/([^"]+)"')
	if (-not $m.Success) {
		Fail "$id is not in Kopru.App.deps.json."
	}
	return $m.Groups[1].Value
}
$runtimeConfig = Get-Content (Join-Path $staging 'Kopru.App.runtimeconfig.json') -Raw | ConvertFrom-Json
$runtimeVersion = [string]($runtimeConfig.runtimeOptions.includedFrameworks | Where-Object { $_.name -eq 'Microsoft.NETCore.App' } | Select-Object -First 1).version
if ([string]::IsNullOrWhiteSpace($runtimeVersion)) {
	Fail 'Could not read the .NET runtime version from Kopru.App.runtimeconfig.json.'
}
$licenses = Join-Path $staging 'licenses'
New-Item -ItemType Directory -Force $licenses | Out-Null
$licenseFiles = @(
	@("microsoft.netcore.app.runtime.$Runtime", $runtimeVersion, 'LICENSE.TXT', 'dotnet-runtime-LICENSE.TXT'),
	@("microsoft.netcore.app.runtime.$Runtime", $runtimeVersion, 'THIRD-PARTY-NOTICES.TXT', 'dotnet-runtime-THIRD-PARTY-NOTICES.TXT'),
	@('skiasharp.nativeassets.win32', (PackageVersion 'SkiaSharp.NativeAssets.Win32'), 'THIRD-PARTY-NOTICES.txt', 'SkiaSharp-THIRD-PARTY-NOTICES.txt'),
	@('harfbuzzsharp.nativeassets.win32', (PackageVersion 'HarfBuzzSharp.NativeAssets.Win32'), 'THIRD-PARTY-NOTICES.txt', 'HarfBuzzSharp-THIRD-PARTY-NOTICES.txt'),
	@('avalonia.angle.windows.natives', (PackageVersion 'Avalonia.Angle.Windows.Natives'), 'LICENSE', 'ANGLE-LICENSE.txt')
)
foreach ($l in $licenseFiles) {
	$source = Join-Path $nuget ('{0}\{1}\{2}' -f $l[0], $l[1].ToLowerInvariant(), $l[2])
	if (-not (Test-Path $source)) {
		Fail "Licence file not found: $($l[0]) $($l[1]) $($l[2])"
	}
	Copy-Item $source (Join-Path $licenses $l[3])
}

# --- Contents check ---
$exe = Join-Path $staging 'Kopru.App.exe'
if (-not (Test-Path $exe)) {
	Fail 'Kopru.App.exe is missing from the publish output.'
}
foreach ($required in @('libSkiaSharp.dll', 'libHarfBuzzSharp.dll', 'Kopru.Core.dll', 'Kopru.App.dll')) {
	if (-not (Test-Path (Join-Path $staging $required))) {
		Fail "$required is missing from the publish output."
	}
}
$files = @(Get-ChildItem -Path $staging -Recurse -File)
$unwanted = @($files | Where-Object {
	$_.Extension -eq '.pdb' -or $_.Name -like '*Tests*' -or $_.Name -like 'Kopru.Probe*' -or $_.Name -like 'Kopru.GameList*' -or
	$_.FullName -like '*\captures\*' -or $_.Name -eq 'settings.json'
})
if ($unwanted.Count -gt 0) {
	Fail ("Unexpected files in the output:`n  " + (($unwanted | ForEach-Object { $_.FullName.Substring($staging.Length + 1) }) -join "`n  "))
}

# --- Privacy scan ---
# Byte search, case-insensitive: each file is read as Latin-1 (one char per byte), each needle is encoded as ASCII/UTF-8
# and as UTF-16LE and decoded the same way.
$latin1 = [System.Text.Encoding]::GetEncoding(28591)
$terms = New-Object System.Collections.Generic.List[string]
foreach ($t in @($env:USERPROFILE, $env:USERNAME, $env:COMPUTERNAME)) {
	if (-not [string]::IsNullOrWhiteSpace($t)) {
		$terms.Add($t)
	}
}
if (-not [string]::IsNullOrWhiteSpace($env:USERPROFILE)) {
	$terms.Add($env:USERPROFILE.Replace('\', '/'))
	$terms.Add($env:USERPROFILE.Replace('\', '\\'))
}
if ($terms.Count -eq 0) {
	Fail 'USERPROFILE, USERNAME and COMPUTERNAME are all empty; cannot run the privacy scan.'
}
# A match must not be glued to other letters or digits (a short user name like "omer" would otherwise also match inside
# ordinary words in compression dictionaries and string tables), so each needle gets word boundaries for its encoding.
$regexOptions = [System.Text.RegularExpressions.RegexOptions]::IgnoreCase -bor [System.Text.RegularExpressions.RegexOptions]::CultureInvariant
$needles = New-Object System.Collections.Generic.List[object]
foreach ($t in $terms) {
	$utf8 = [regex]::Escape($latin1.GetString([System.Text.Encoding]::UTF8.GetBytes($t)))
	$utf16 = [regex]::Escape($latin1.GetString([System.Text.Encoding]::Unicode.GetBytes($t)))
	$needles.Add(@($t, 'UTF-8', (New-Object regex ('(?<![A-Za-z0-9])' + $utf8 + '(?![A-Za-z0-9])'), $regexOptions)))
	$needles.Add(@($t, 'UTF-16LE', (New-Object regex ('(?<![A-Za-z0-9]\x00)' + $utf16 + '(?![A-Za-z0-9]\x00)'), $regexOptions)))
}
# The installer inputs are scanned too (the script and its icon); the staging folder already holds LICENSE and the rest.
$scan = @($files | ForEach-Object { @{ Path = $_.FullName; Label = $_.FullName.Substring($staging.Length + 1) } })
foreach ($extra in @($iss, (Join-Path $repo 'src\Kopru.App\Assets\icon.ico'))) {
	$scan += @{ Path = $extra; Label = $extra.Substring($repo.Length + 1) }
}
function Find-Leaks($items) {
	$found = New-Object System.Collections.Generic.List[string]
	foreach ($s in $items) {
		$text = $latin1.GetString([System.IO.File]::ReadAllBytes($s.Path))
		foreach ($n in $needles) {
			if ($n[2].IsMatch($text)) {
				$found.Add(('{0}  [{1}: {2}]' -f $s.Label, $n[1], $(if ($n[0] -eq $env:COMPUTERNAME) { 'computer name' } elseif ($n[0] -eq $env:USERNAME) { 'user name' } else { 'profile path' })))
			}
		}
	}
	return ,$found
}
$hits = Find-Leaks $scan
if ($hits.Count -gt 0) {
	Fail ("Privacy scan: the build machine's user or computer name was found in:`n  " + (($hits | Sort-Object -Unique) -join "`n  "))
}
Write-Host "Privacy scan passed ($($scan.Count) files, $($needles.Count) patterns)."

# --- Zip ---
Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::CreateFromDirectory($staging, $zip, [System.IO.Compression.CompressionLevel]::Optimal, $false)

$zipInfo = Get-Item $zip
$hash = (Get-FileHash -Algorithm SHA256 $zip).Hash
$total = ($files | Measure-Object -Property Length -Sum).Sum
Write-Host ''
Write-Host "Zip:     $($zipInfo.FullName)"
Write-Host ("Size:    {0:N1} MB (unpacked {1:N1} MB)" -f ($zipInfo.Length / 1MB), ($total / 1MB))
Write-Host "Files:   $($files.Count)"
Write-Host "SHA-256: $hash"

# --- Installer (Inno Setup 6, only when it is installed) ---
$iscc = $null
$onPath = Get-Command 'ISCC.exe' -ErrorAction SilentlyContinue
if ($onPath) {
	$iscc = $onPath.Source
}
else {
	$roots = @(${env:ProgramFiles(x86)}, $env:ProgramFiles, (Join-Path $env:LOCALAPPDATA 'Programs'))
	foreach ($root in $roots) {
		if ([string]::IsNullOrWhiteSpace($root)) {
			continue
		}
		$candidate = Join-Path $root 'Inno Setup 6\ISCC.exe'
		if (Test-Path $candidate) {
			$iscc = $candidate
			break
		}
	}
}
Write-Host ''
if (-not $iscc) {
	Write-Host 'Installer skipped: Inno Setup 6 (ISCC.exe) was not found; only the zip was made.' -ForegroundColor Yellow
	exit 0
}
& $iscc '/Q' "/DAppVersion=$version" "/DAppVersionNumeric=$numericVersion" "/DStagingDir=$staging" "/DOutputDir=$artifacts" $iss
if ($LASTEXITCODE -ne 0 -or -not (Test-Path $setup)) {
	Fail "Inno Setup failed (exit $LASTEXITCODE)."
}
# Setup.exe's payload is LZMA2-compressed, so this only covers its uncompressed parts (version resources, setup header);
# the scan of the staging folder above is the real check of what gets installed.
$setupHits = Find-Leaks @(@{ Path = $setup; Label = (Split-Path $setup -Leaf) })
if ($setupHits.Count -gt 0) {
	Remove-Item -Force $setup
	Fail ("Privacy scan: the build machine's user or computer name was found in:`n  " + (($setupHits | Sort-Object -Unique) -join "`n  "))
}
Write-Host 'Privacy scan of the Setup.exe passed (uncompressed parts only).'
$setupInfo = Get-Item $setup
Write-Host "Setup:   $($setupInfo.FullName)"
Write-Host ("Size:    {0:N1} MB" -f ($setupInfo.Length / 1MB))
Write-Host "SHA-256: $((Get-FileHash -Algorithm SHA256 $setup).Hash)"
