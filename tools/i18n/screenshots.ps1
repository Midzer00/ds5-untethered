# Renders headless PNG screenshots of every page and dialog per language for translators.
# Output is never committed (defaults to TestResults\i18n-screenshots, which is git-ignored).
#
# Usage:
#   powershell -ExecutionPolicy Bypass -File tools\i18n\screenshots.ps1
#   powershell -ExecutionPolicy Bypass -File tools\i18n\screenshots.ps1 -OutDir C:\temp\shots -Languages en,tr,qps
[CmdletBinding()]
param(
	[string]$OutDir = '',
	[string[]]$Languages = @(),
	[int]$Width = 1152,
	[int]$Height = 700,
	[switch]$NoBuild
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$project = Join-Path $root 'tests\Kopru.App.Tests\Kopru.App.Tests.csproj'
if (-not $OutDir) {
	$OutDir = Join-Path $root 'TestResults\i18n-screenshots'
} elseif (-not [IO.Path]::IsPathRooted($OutDir)) {
	$OutDir = [IO.Path]::GetFullPath((Join-Path (Get-Location) $OutDir))
}

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

$savedDir = $env:KOPRU_I18N_SCREENSHOTS_DIR
$savedLangs = $env:KOPRU_I18N_SCREENSHOTS_LANGS
$savedW = $env:KOPRU_I18N_SCREENSHOTS_WIDTH
$savedH = $env:KOPRU_I18N_SCREENSHOTS_HEIGHT

try {
	$env:KOPRU_I18N_SCREENSHOTS_DIR = $OutDir
	$env:KOPRU_I18N_SCREENSHOTS_LANGS = ($Languages -join ',')
	$env:KOPRU_I18N_SCREENSHOTS_WIDTH = "$Width"
	$env:KOPRU_I18N_SCREENSHOTS_HEIGHT = "$Height"

	$args = @('test', $project, '--filter', 'FullyQualifiedName~TranslatorScreenshotTests.RenderTranslatorScreenshots')
	if ($NoBuild) { $args += '--no-build' }
	& dotnet @args
	if ($LASTEXITCODE -ne 0) {
		exit $LASTEXITCODE
	}
	Write-Host "Translator screenshots written to: $OutDir"
} finally {
	$env:KOPRU_I18N_SCREENSHOTS_DIR = $savedDir
	$env:KOPRU_I18N_SCREENSHOTS_LANGS = $savedLangs
	$env:KOPRU_I18N_SCREENSHOTS_WIDTH = $savedW
	$env:KOPRU_I18N_SCREENSHOTS_HEIGHT = $savedH
}
