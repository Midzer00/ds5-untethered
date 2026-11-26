# Renders the translation context for the translate page (ds5untethered.com/translate/): an English WebP picture of every
# page, tab and dialog, manifest.json with the box of every catalogue key on each picture, and coverage.json (keys no
# picture shows, texts without a key). Headless; output is never committed (defaults to TestResults\translate-context,
# which is git-ignored).
#
# Usage:
#   powershell -ExecutionPolicy Bypass -File tools\i18n\context.ps1
#   powershell -ExecutionPolicy Bypass -File tools\i18n\context.ps1 -OutDir C:\temp\context -Scale 1
[CmdletBinding()]
param(
	[string]$OutDir = '',
	[double]$Scale = 2,
	[switch]$NoBuild
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$project = Join-Path $root 'tests\Kopru.App.Tests\Kopru.App.Tests.csproj'
if (-not $OutDir) {
	$OutDir = Join-Path $root 'TestResults\translate-context'
} elseif (-not [IO.Path]::IsPathRooted($OutDir)) {
	$OutDir = [IO.Path]::GetFullPath((Join-Path (Get-Location) $OutDir))
}

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

$savedDir = $env:KOPRU_I18N_CONTEXT_DIR
$savedScale = $env:KOPRU_I18N_CONTEXT_SCALE

try {
	$env:KOPRU_I18N_CONTEXT_DIR = $OutDir
	$env:KOPRU_I18N_CONTEXT_SCALE = $Scale.ToString([Globalization.CultureInfo]::InvariantCulture)

	$args = @('test', $project, '--filter', 'FullyQualifiedName~TranslateContextTests.RenderTranslateContext')
	if ($NoBuild) { $args += '--no-build' }
	& dotnet @args
	if ($LASTEXITCODE -ne 0) {
		exit $LASTEXITCODE
	}
	$files = Get-ChildItem -Path $OutDir -File
	$mb = [Math]::Round(($files | Measure-Object -Property Length -Sum).Sum / 1MB, 2)
	Write-Host "Translation context written to: $OutDir ($($files.Count) files, $mb MB)"
} finally {
	$env:KOPRU_I18N_CONTEXT_DIR = $savedDir
	$env:KOPRU_I18N_CONTEXT_SCALE = $savedScale
}
