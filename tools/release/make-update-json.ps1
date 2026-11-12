# SPDX-License-Identifier: GPL-3.0-or-later
# Writes the update manifest (update.json, read by the app's in-app update check) for one release: the version, the
# GitHub release download URLs for tag v<version>, the size and SHA-256 of the installer and the zip, the release page and
# the short notes from tools\release\notes.json ({ "tr": "...", "en": "..." }, optional; empty when missing).
# tools\publish.ps1 calls it; it can also be run on its own:
#
#   powershell -ExecutionPolicy Bypass -File tools\release\make-update-json.ps1 -Version 0.1.6 `
#     -Setup artifacts\DS5-Untethered-0.1.6-Setup.exe -Zip artifacts\DS5-Untethered-0.1.6-win-x64.zip -Out artifacts\update.json

[CmdletBinding()]
param(
	[Parameter(Mandatory = $true)][string]$Version,
	[Parameter(Mandatory = $true)][string]$Setup,
	[Parameter(Mandatory = $true)][string]$Zip,
	[Parameter(Mandatory = $true)][string]$Out,
	[string]$Notes = (Join-Path $PSScriptRoot 'notes.json'),
	[string]$Published = ([DateTime]::UtcNow.ToString('yyyy-MM-dd', [System.Globalization.CultureInfo]::InvariantCulture))
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

$repository = 'Midzer00/ds5-untethered'
if ($Version -notmatch '^\d+\.\d+\.\d+(\.\d+)?$') {
	throw "Unexpected version: '$Version'."
}

# A JSON string literal (quotes, backslashes and control characters escaped; other characters kept as they are).
function JsonString([string]$text) {
	$sb = New-Object System.Text.StringBuilder
	[void]$sb.Append('"')
	foreach ($c in $text.ToCharArray()) {
		switch ($c) {
			'"' { [void]$sb.Append('\"') }
			'\' { [void]$sb.Append('\\') }
			"`n" { [void]$sb.Append('\n') }
			"`r" { }
			"`t" { [void]$sb.Append(' ') }
			default {
				if ([int]$c -lt 32) { } else { [void]$sb.Append($c) }
			}
		}
	}
	[void]$sb.Append('"')
	return $sb.ToString()
}

function Asset([string]$path) {
	if (-not (Test-Path $path)) {
		throw "Missing: $path"
	}
	$item = Get-Item $path
	$url = "https://github.com/$repository/releases/download/v$Version/$($item.Name)"
	$hash = (Get-FileHash -Algorithm SHA256 $item.FullName).Hash.ToUpperInvariant()
	return "{ ""url"": $(JsonString $url), ""sha256"": ""$hash"", ""size"": $($item.Length) }"
}

$tr = ''
$en = ''
if (Test-Path $Notes) {
	$parsed = [System.IO.File]::ReadAllText((Resolve-Path $Notes).Path, [System.Text.Encoding]::UTF8) | ConvertFrom-Json
	if ($parsed.PSObject.Properties.Name -contains 'tr' -and $parsed.tr) { $tr = [string]$parsed.tr }
	if ($parsed.PSObject.Properties.Name -contains 'en' -and $parsed.en) { $en = [string]$parsed.en }
}

$json = @"
{
  "version": "$Version",
  "published": "$Published",
  "setup": $(Asset $Setup),
  "zip": $(Asset $Zip),
  "page": "https://github.com/$repository/releases/tag/v$Version",
  "notes": { "tr": $(JsonString $tr), "en": $(JsonString $en) }
}
"@
$folder = Split-Path -Parent ([System.IO.Path]::GetFullPath($Out))
if (-not (Test-Path $folder)) {
	New-Item -ItemType Directory -Force $folder | Out-Null
}
[System.IO.File]::WriteAllText([System.IO.Path]::GetFullPath($Out), ($json -replace "`r`n", "`n") + "`n", (New-Object System.Text.UTF8Encoding($false)))
Write-Host "Update manifest: $([System.IO.Path]::GetFullPath($Out))"
