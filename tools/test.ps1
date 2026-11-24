# Runs the whole test suite fast: builds once, then runs Kopru.Core.Tests and Kopru.App.Tests split over several dotnet
# test processes at the same time. The App tests can't run in parallel inside one process (the Avalonia headless app,
# the UI language and other static state are shared), but separate processes don't share any of that.
#
#   powershell -ExecutionPolicy Bypass -File tools\test.ps1 [-Shards 8] [-NoBuild] [-Configuration Debug]
#
# Phase 1: Core (without the real-time tests) and the App shards, all at once. Each App shard gets a disjoint set of
# test classes, balanced by the class durations of the previous run (TestResults\test.ps1\durations.json; without it,
# by test count). Phase 2: the Core tests that measure real timer pacing (Category=RealTime), alone on an idle machine.
# Every run has its own results folder and its own TEMP/TMP (some tests use fixed file names under the temp folder).
# The script checks that every listed test ran exactly once, prints one line per run and the totals, lists failing
# tests and exits 1 on any failure.
[CmdletBinding()]
param(
	[int]$Shards = 0,
	[switch]$NoBuild,
	[string]$Configuration = 'Debug',
	[int]$HangTimeoutSeconds = 180
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$appProject = Join-Path $root 'tests\Kopru.App.Tests\Kopru.App.Tests.csproj'
$coreProject = Join-Path $root 'tests\Kopru.Core.Tests\Kopru.Core.Tests.csproj'
$resultsRoot = Join-Path $root 'TestResults\test.ps1'
$durationsFile = Join-Path $resultsRoot 'durations.json'
# Outside the repository: a temp folder under Desktop or Documents gets indexed/scanned, and a rename can then fail
# with "access denied".
$tempRoot = Join-Path ([IO.Path]::GetTempPath()) ("kopru-test-" + $PID)
$realTimeFilter = 'Category=RealTime'
$wall = [Diagnostics.Stopwatch]::StartNew()
# The code under test is what HEAD was when the run started (commits made during the run don't count).
$startCommit = "$(& git -C $root rev-parse HEAD 2>$null)".Trim()
$startBranch = "$(& git -C $root rev-parse --abbrev-ref HEAD 2>$null)".Trim()
$startDirty = @(& git -C $root status --porcelain --untracked-files=no 2>$null).Count -gt 0

# Machine-readable result of the run (TestResults\summary.json), written on success and on failure.
function Write-Summary([int]$coreFailed, [int]$corePassed, [int]$appFailed, [int]$appPassed, [string[]]$failedTests) {
	try {
		$flakyFile = Join-Path $PSScriptRoot 'test-flaky.txt'
		$flaky = @()
		if (Test-Path $flakyFile) {
			$flaky = @(Get-Content $flakyFile | ForEach-Object { $_.Trim() } | Where-Object { $_ -and -not $_.StartsWith('#') })
		}
		$names = @($failedTests | Where-Object { $_ })
		$summary = [ordered]@{
			commit = $startCommit
			branch = $startBranch
			uncommittedChanges = $startDirty
			finishedAt = (Get-Date).ToString('o')
			coreFailed = $coreFailed
			corePassed = $corePassed
			appFailed = $appFailed
			appPassed = $appPassed
			failedTests = $names
			knownFlaky = @($names | Where-Object { $flaky -contains $_ })
		}
		$dir = Join-Path $root 'TestResults'
		New-Item -ItemType Directory -Force $dir | Out-Null
		($summary | ConvertTo-Json -Depth 4) | Set-Content (Join-Path $dir 'summary.json') -Encoding UTF8
	} catch {
		Write-Host "summary.json yazılamadı: $($_.Exception.Message)"
	}
}

if ($Shards -le 0) {
	$Shards = [Math]::Max(1, [Math]::Min(8, [int][Math]::Floor([Environment]::ProcessorCount / 2)))
}

if (-not $NoBuild) {
	& dotnet build (Join-Path $root 'Kopru.sln') --no-incremental -c $Configuration -nologo -v q
	if ($LASTEXITCODE -ne 0) {
		Write-Host 'Derleme başarısız.'
		Write-Summary 0 0 0 0 @()
		exit 1
	}
}

# The tests of a project, as "Namespace.Class.Method(args)" display names.
function Get-TestList([string]$project) {
	$listing = & dotnet test $project --no-build -c $Configuration --list-tests 2>&1
	$tests = @($listing | Where-Object { $_ -match '^    \S' } | ForEach-Object { $_.Trim() })
	if ($LASTEXITCODE -ne 0 -or $tests.Count -eq 0) {
		$listing | Write-Host
		Write-Host "Testler listelenemedi: $project"
		Write-Summary 0 0 0 0 @()
		exit 1
	}
	return , $tests
}

function Get-ClassName([string]$testName) {
	$name = $testName -replace '\(.*$', ''
	return $name.Substring(0, $name.LastIndexOf('.'))
}

$appTests = Get-TestList $appProject
$coreTests = Get-TestList $coreProject

$classes = @{}
foreach ($test in $appTests) {
	$class = Get-ClassName $test
	if (-not $classes.ContainsKey($class)) { $classes[$class] = 0 }
	$classes[$class]++
}

# Weight per class: seconds from the previous run, else the test count (about a second per test).
$durations = @{}
if (Test-Path $durationsFile) {
	try {
		(Get-Content $durationsFile -Raw | ConvertFrom-Json).PSObject.Properties | ForEach-Object { $durations[$_.Name] = [double]$_.Value }
	} catch {
		$durations = @{}
	}
}
$weights = @{}
foreach ($class in $classes.Keys) {
	$weights[$class] = if ($durations.ContainsKey($class)) { $durations[$class] } else { [double]$classes[$class] }
}

# Longest class first onto the lightest shard.
$Shards = [Math]::Min($Shards, $classes.Count)
$shardClasses = @(for ($i = 0; $i -lt $Shards; $i++) { , (New-Object System.Collections.Generic.List[string]) })
$shardLoad = New-Object double[] $Shards
foreach ($class in ($weights.Keys | Sort-Object -Property @{ Expression = { $weights[$_] }; Descending = $true }, @{ Expression = { $_ } })) {
	$lightest = 0
	for ($i = 1; $i -lt $Shards; $i++) { if ($shardLoad[$i] -lt $shardLoad[$lightest]) { $lightest = $i } }
	$shardClasses[$lightest].Add($class)
	$shardLoad[$lightest] += $weights[$class]
}

if (Test-Path $resultsRoot) {
	Get-ChildItem $resultsRoot -Directory | Remove-Item -Recurse -Force
} else {
	New-Item -ItemType Directory -Force $resultsRoot | Out-Null
}

function Start-TestRun([string]$name, [string]$label, [string]$project, [string]$filter) {
	$dir = Join-Path $resultsRoot $name
	$temp = Join-Path $tempRoot $name
	New-Item -ItemType Directory -Force $dir, $temp | Out-Null
	$arguments = @('test', "`"$project`"", '--no-build', '-c', $Configuration, '--blame-hang', '--blame-hang-timeout', "$($HangTimeoutSeconds)s",
		'--results-directory', "`"$dir`"", '--logger', '"trx;LogFileName=results.trx"')
	if ($filter) { $arguments += @('--filter', "`"$filter`"") }
	$savedTemp = $env:TEMP
	$savedTmp = $env:TMP
	try {
		$env:TEMP = $temp
		$env:TMP = $temp
		$process = Start-Process dotnet -ArgumentList $arguments -NoNewWindow -PassThru `
			-RedirectStandardOutput (Join-Path $dir 'output.log') -RedirectStandardError (Join-Path $dir 'error.log')
	} finally {
		$env:TEMP = $savedTemp
		$env:TMP = $savedTmp
	}
	$null = $process.Handle # keeps the exit code readable after the process ends
	return [pscustomobject]@{ Name = $name; Label = $label; Dir = $dir; Process = $process; IsApp = $project -eq $appProject
		Assembly = [IO.Path]::GetFileNameWithoutExtension($project) + '.dll' }
}

$runs = New-Object System.Collections.Generic.List[object]
try {
	# Phase 1: Core without the real-time tests, and the App shards.
	$runs.Add((Start-TestRun 'core' 'Core' $coreProject "Category!=RealTime"))
	for ($i = 0; $i -lt $Shards; $i++) {
		$filter = ($shardClasses[$i] | ForEach-Object { "FullyQualifiedName~$_." }) -join '|'
		$runs.Add((Start-TestRun ("app-{0}" -f ($i + 1)) ("App {0}/{1}" -f ($i + 1), $Shards) $appProject $filter))
	}
	foreach ($run in $runs) { $run.Process.WaitForExit() }

	# Phase 2: the real-time Core tests (one class at a time, collection "RealTime") with nothing else running: under
	# load their pacing checks fail although the code is right.
	$realTime = Start-TestRun 'core-realtime' 'Core gerçek zaman' $coreProject $realTimeFilter
	$runs.Add($realTime)
	$realTime.Process.WaitForExit()
} finally {
	if (Test-Path $tempRoot) { Remove-Item -Recurse -Force $tempRoot -ErrorAction SilentlyContinue }
}

function Format-Duration([TimeSpan]$t) {
	if ($t.TotalSeconds -lt 1) { return "$([int]$t.TotalMilliseconds) ms" }
	if ($t.TotalMinutes -lt 1) { return "$([int][Math]::Floor($t.TotalSeconds)) s" }
	return "$([int][Math]::Floor($t.TotalMinutes)) m $($t.Seconds) s"
}

$failed = $false
$failures = New-Object System.Collections.Generic.List[string]
$failedNames = New-Object System.Collections.Generic.List[string]
$ran = @{ App = (New-Object System.Collections.Generic.List[string]); Core = (New-Object System.Collections.Generic.List[string]) }
$totals = @{ App = @{ Failed = 0; Passed = 0; Total = 0 }; Core = @{ Failed = 0; Passed = 0; Total = 0 } }
$appClassSeconds = @{}
foreach ($run in $runs) {
	$trx = Join-Path $run.Dir 'results.trx'
	$kind = if ($run.IsApp) { 'App' } else { 'Core' }
	if (-not (Test-Path $trx)) {
		Write-Host "Başarısız!  - $($run.Label): sonuç dosyası yok (çıkış kodu $($run.Process.ExitCode)), çıktı: $(Join-Path $run.Dir 'output.log')"
		$failed = $true
		continue
	}
	[xml]$xml = Get-Content $trx -Raw -Encoding UTF8
	$counters = $xml.TestRun.ResultSummary.Counters
	$total = [int]$counters.total
	$passed = [int]$counters.passed
	$failedCount = [int]$counters.failed
	$skipped = $total - $passed - $failedCount
	$times = $xml.TestRun.Times
	$elapsed = [DateTimeOffset]::Parse($times.finish) - [DateTimeOffset]::Parse($times.start)
	$ok = $failedCount -eq 0 -and $run.Process.ExitCode -eq 0 -and $total -gt 0
	if (-not $ok) { $failed = $true }
	Write-Host ("{0}  - Başarısız: {1,5}, Başarılı: {2,5}, Atlanan: {3,5}, Toplam: {4,5}, Süre: {5} - {6} ({7})" -f `
		$(if ($ok) { 'Başarılı!' } else { 'Başarısız!' }), $failedCount, $passed, $skipped, $total, (Format-Duration $elapsed), $run.Assembly, $run.Label)
	foreach ($result in @($xml.TestRun.Results.UnitTestResult)) {
		if ($null -eq $result) { continue }
		if ($result.outcome -ne 'Passed' -and $result.outcome -ne 'NotExecuted') {
			$message = "$($result.Output.ErrorInfo.Message)".Trim() -split "`r?`n" | Select-Object -First 1
			$failedNames.Add([string]$result.testName)
			$failures.Add("$($run.Label)  $($result.testName)$(if ($message) { "  ($message)" })")
		}
		$ran[$kind].Add($result.testName)
		if ($run.IsApp) {
			$class = Get-ClassName $result.testName
			if (-not $appClassSeconds.ContainsKey($class)) { $appClassSeconds[$class] = 0.0 }
			$appClassSeconds[$class] += [TimeSpan]::Parse($result.duration).TotalSeconds
		}
	}
	$totals[$kind].Failed += $failedCount
	$totals[$kind].Passed += $passed
	$totals[$kind].Total += $total
	if ($run.Process.ExitCode -ne 0 -and $failedCount -eq 0) {
		Write-Host "  $($run.Label): çıkış kodu $($run.Process.ExitCode), çıktı: $(Join-Path $run.Dir 'output.log')"
	}
}

foreach ($kind in 'Core', 'App') {
	Write-Host ("{0} toplam: Başarısız {1}, Başarılı {2}, Toplam {3}" -f $kind, $totals[$kind].Failed, $totals[$kind].Passed, $totals[$kind].Total)
}

# Every listed test ran exactly once.
foreach ($check in @(@{ Kind = 'App'; Expected = $appTests }, @{ Kind = 'Core'; Expected = $coreTests })) {
	$expected = @($check.Expected | Sort-Object)
	$actual = @($ran[$check.Kind] | Sort-Object)
	if ($expected.Count -ne $actual.Count -or (Compare-Object $expected $actual -SyncWindow 0)) {
		$failed = $true
		$missing = @(Compare-Object $expected $actual | Where-Object SideIndicator -eq '<=' | ForEach-Object InputObject)
		$extra = @(Compare-Object $expected $actual | Where-Object SideIndicator -eq '=>' | ForEach-Object InputObject)
		Write-Host "$($check.Kind) test listesi tutmuyor: listede $($expected.Count), çalışan $($actual.Count)."
		$missing | Select-Object -First 20 | ForEach-Object { Write-Host "  çalışmadı: $_" }
		$extra | Select-Object -First 20 | ForEach-Object { Write-Host "  fazladan: $_" }
		if ($missing.Count -eq 0 -and $extra.Count -eq 0) { Write-Host '  bazı testler birden fazla çalıştı' }
	}
}

if ($failures.Count -gt 0) {
	Write-Host 'Başarısız testler:'
	$failures | ForEach-Object { Write-Host "  $_" }
}

if (-not $failed -and $appClassSeconds.Count -gt 0) {
	$ordered = [ordered]@{}
	foreach ($class in ($appClassSeconds.Keys | Sort-Object)) { $ordered[$class] = [Math]::Round($appClassSeconds[$class], 2) }
	[pscustomobject]$ordered | ConvertTo-Json | Set-Content $durationsFile -Encoding UTF8
}

Write-Host ("Süre: {0} ({1} App parçası), sonuçlar: {2}" -f (Format-Duration $wall.Elapsed), $Shards, $resultsRoot)
Write-Summary $totals.Core.Failed $totals.Core.Passed $totals.App.Failed $totals.App.Passed $failedNames.ToArray()
if ($failed) { exit 1 }
exit 0
