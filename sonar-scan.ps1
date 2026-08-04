#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Single entry point for SonarQube analysis of SilentScan: scans, waits for
    background processing, then prints the actual result - no second script.

.DESCRIPTION
    Uses SonarScanner for .NET (dotnet-sonarscanner), which is the ONLY scanner
    that can analyze C#: it hooks into the MSBuild compilation so the Roslyn
    analyzers run. In the same pass it also picks up the non-.NET files
    (corpus manifest, docs, test fixture .sql), so this is the single entry
    point for the whole repo.

    Layers covered:
      - .NET (Core/Cli/Verify/Bench/Tests) .. C#
      - Test fixtures ........................ T-SQL (tests/**/fixtures/*.sql)
      - Infra & ops ........................... Docker Compose, shell
      - Secrets detection ..................... whole repo

    By default this is QUIET: routine scanner/build/test chatter is suppressed,
    and only the final result prints - a one-line summary when clean, full
    file:line/severity/rule/message detail for every issue and hotspot when
    not, always the quality gate status. Pass -Verbose (a native PowerShell
    common parameter) to see the full scan/build/test output as it runs.

.PARAMETER Password
    SonarQube admin password, used to mint a short-lived analysis token.
    A hardcoded default is acceptable only because this script is gitignored
    (see .gitignore) and never committed. Override with -Password if it changes.

.PARAMETER WithCoverage
    Also run the .NET test suite and import coverage. On by default per
    CLAUDE.md's 99% coverage target; pass -WithCoverage:$false to skip for a
    quick lint-only pass.

.EXAMPLE
    ./sonar-scan.ps1
    ./sonar-scan.ps1 -Verbose
    ./sonar-scan.ps1 -WithCoverage:$false
#>

[CmdletBinding()]
param(
    [string]$Password     = 'SonarPassword@1',
    [string]$HostUrl      = 'http://localhost:9010',
    [string]$ProjectKey   = 'silentscan',
    [switch]$WithCoverage = $true,
    # Per-step wall-clock ceilings (seconds). See Invoke-Step's remarks for why these exist at
    # all. Generous defaults: `test` in particular deploys and drops real Docker SQL Server
    # databases per Oracle-tagged fixture (docs/local-dev.md), which is legitimately slow, not
    # hung - the cap exists to catch an unresponsive dependency, not to rush a healthy run.
    [int]$BeginTimeoutSeconds = 180,
    [int]$BuildTimeoutSeconds = 600,
    [int]$TestTimeoutSeconds  = 1800,
    [int]$EndTimeoutSeconds   = 300,
    [int]$RestTimeoutSeconds  = 30
)

$ErrorActionPreference = 'Stop'
$RootDir  = $PSScriptRoot
$Solution = Join-Path $RootDir 'SilentScan.slnx'
$IsVerbose = $VerbosePreference -eq 'Continue'

# Runs an external command under a hard wall-clock cap, capturing its combined
# output. Streamed live only under -Verbose; otherwise captured silently and
# dumped in full ONLY if the command fails - so a failure's specific detail is
# never lost, but a clean run never has to scroll past four steps of routine
# build/scan noise to find out whether it passed.
#
# The timeout is not optional and not merely a nicety: this previously ran
# `dotnet sonarscanner begin/end`, `dotnet build`, and `dotnet test` as plain
# unbounded external calls. When the SonarQube container it was talking to
# restarted mid-run (observed directly - an unrelated container restart, not
# something this script did), `dotnet sonarscanner end`'s upload call hung
# forever waiting on a socket that would never respond, and nothing noticed
# for over 111 minutes until a human did. Every step here now has an explicit
# ceiling: it fails loudly with a clear timeout message instead of hanging
# silently. Implemented via the `timeout` coreutil rather than PowerShell job
# machinery - Start-Job/Stop-Job does not reliably tear down a job's own
# native child processes on Linux, `timeout` (with --kill-after as a SIGKILL
# backstop if SIGTERM is ignored) does.
#
# MSBUILDDISABLENODEREUSE=1 for the same reason scripts/dotnet-safe.sh sets it: `dotnet build`/
# `dotnet test` otherwise spawn detached MSBuild worker processes that outlive the command by
# design, which left thousands of orphaned /tmp/MSBuildTemp* directories across sessions that
# ran and killed dotnet invocations directly (see docs/local-dev.md). Exported here so every
# `dotnet` child this script launches inherits it.
$env:MSBUILDDISABLENODEREUSE = '1'

# Same root cause scripts/dotnet-safe.sh's own wait_for_stray_build_processes closes, reproduced
# directly in THIS script (2026-08-04): `dotnet build-server shutdown` only requests a shutdown,
# it does not block until the VBCSCompiler/MSBuild node-mode processes have actually exited - the
# [3/4] Build step below can start while a PRIOR invocation's server is still mid-teardown (this
# script's own earlier run, or scripts/dotnet-safe.sh's, or an editor's language server attached
# to the repo) and hit the documented 0x80131506 crash. Only ever targets those two process
# names by name, never a language server's own long-lived BuildHost process.
function Wait-ForStrayBuildProcesses {
    $pattern = 'VBCSCompiler|MSBuild\.dll.*nodemode'
    $maxWaitSeconds = 10
    $uid = (& id -u)
    for ($waited = 0; $waited -le $maxWaitSeconds; $waited++) {
        & pgrep -u $uid -f $pattern *> $null
        if ($LASTEXITCODE -ne 0) { return }
        if ($waited -eq $maxWaitSeconds) {
            & pkill -KILL -u $uid -f $pattern 2>$null
            return
        }
        Start-Sleep -Seconds 1
    }
}

& dotnet build-server shutdown *> $null
Wait-ForStrayBuildProcesses

function Invoke-Step {
    param(
        [Parameter(Mandatory)] [string]$Label,
        [Parameter(Mandatory)] [string]$FilePath,
        [Parameter(Mandatory)] [string[]]$ArgumentList,
        [Parameter(Mandatory)] [int]$TimeoutSeconds
    )
    Write-Verbose "=== $Label (timeout ${TimeoutSeconds}s) ==="
    $timeoutArgs = @('--kill-after=10', "${TimeoutSeconds}s", $FilePath) + $ArgumentList
    if ($IsVerbose) {
        & timeout @timeoutArgs
        $exit = $LASTEXITCODE
    } else {
        $output = & timeout @timeoutArgs 2>&1
        $exit = $LASTEXITCODE
        if ($exit -ne 0) {
            Write-Host ""
            Write-Host "--- $Label failed - output follows ---" -ForegroundColor Red
            $output | ForEach-Object { Write-Host $_ }
            Write-Host "--- end $Label output ---" -ForegroundColor Red
        }
    }
    # GNU coreutils `timeout` exit code 124 means the command was killed for exceeding the
    # deadline (137 if --kill-after's SIGKILL was the one that landed) - surface that distinctly
    # rather than letting it read as an ordinary tool failure.
    if ($exit -eq 124 -or $exit -eq 137) {
        throw "$Label timed out after ${TimeoutSeconds}s and was killed."
    }
    return $exit
}

# -- Preflight ---------------------------------------------------------------
if (-not (Get-Command dotnet-sonarscanner -ErrorAction SilentlyContinue)) {
    throw "dotnet-sonarscanner not found. Install it with:`n  dotnet tool install --global dotnet-sonarscanner"
}
if (-not (Get-Command java -ErrorAction SilentlyContinue)) {
    throw "java not found on PATH. SonarScanner for .NET requires a Java 17+ runtime."
}
if (-not (Get-Command timeout -ErrorAction SilentlyContinue)) {
    throw "timeout (GNU coreutils) not found on PATH. Required to bound every external dotnet/sonarscanner call - see Invoke-Step."
}
try {
    $status = Invoke-RestMethod "$HostUrl/api/system/status" -TimeoutSec 10
    if ($status.status -ne 'UP') { throw "SonarQube status is '$($status.status)', expected 'UP'." }
} catch {
    throw "Cannot reach SonarQube at $HostUrl. Is the container running? ($($_.Exception.Message))"
}

# -- Token -------------------------------------------------------------------
Write-Verbose "Generating a one-time analysis token..."
$credentials = [Convert]::ToBase64String([Text.Encoding]::ASCII.GetBytes("admin:$Password"))
$authHeader = @{ Authorization = "Basic $credentials" }
$tokenResponse = Invoke-RestMethod -Method Post `
    -Uri "$HostUrl/api/user_tokens/generate" `
    -Headers $authHeader `
    -Body @{ name = "$ProjectKey-scan-$([DateTimeOffset]::Now.ToUnixTimeSeconds())" } `
    -TimeoutSec $RestTimeoutSeconds
if (-not $tokenResponse.token) { throw "Failed to generate a SonarQube token." }
$Token = $tokenResponse.token

# -- sonar-project.properties ------------------------------------------------
# SonarScanner for .NET refuses to start when this file exists, because it
# derives sonar.sources from the MSBuild graph itself. Every setting the file
# held now lives in the begin step below. Stash it for the run, always restore.
$PropsFile    = Join-Path $RootDir 'sonar-project.properties'
$PropsBackup  = "$PropsFile.scanbak"
$PropsStashed = $false
if (Test-Path $PropsFile) {
    Move-Item -Force $PropsFile $PropsBackup
    $PropsStashed = $true
}

Write-Host "Scanning $ProjectKey..." -ForegroundColor Cyan
Write-Verbose "Host     : $HostUrl"
Write-Verbose "Layers   : .NET (Core/Cli/Verify/Bench/Tests) + SQL fixtures + IaC + secrets"
Write-Verbose "Coverage : $(if ($WithCoverage) { 'enabled' } else { 'skipped' })"

$dotnetTestFailed = $false
$buildFailed      = $false

# -- Build lock ---------------------------------------------------------------
# Two `dotnet build`/`dotnet test` invocations against the SAME checkout's obj/bin output can
# race each other - reproduced directly (concurrent `dotnet build` runs here hit real MSB3026/
# MSB3030 file-lock failures, and separately a Roslyn shared-compiler-server crash) - so this
# script never lets a second copy of itself run its own build/test steps concurrently. An
# OS-level exclusive file lock (FileShare.None), not a plain "does a lock file exist" check: the
# latter can't tell a stale lock from a live one after a crash, this can't be stale by
# construction (the OS releases it the instant the holding process exits, however it exits).
$LockPath = Join-Path $RootDir '.sonar-scan.lock'
$LockStream = $null
$lockWaitSeconds = 0
$lockTimeoutSeconds = 300
while ($true) {
    try {
        $LockStream = [System.IO.File]::Open($LockPath, [System.IO.FileMode]::OpenOrCreate, [System.IO.FileAccess]::ReadWrite, [System.IO.FileShare]::None)
        break
    } catch [System.IO.IOException] {
        if ($lockWaitSeconds -eq 0) {
            Write-Host "Another build/scan is already running against this checkout - waiting for it to finish..." -ForegroundColor Yellow
        }
        if ($lockWaitSeconds -ge $lockTimeoutSeconds) {
            throw "Timed out after ${lockTimeoutSeconds}s waiting for another dotnet build/test/sonar-scan run against this checkout to finish (lock: $LockPath)."
        }
        Start-Sleep -Seconds 2
        $lockWaitSeconds += 2
    }
}

Push-Location $RootDir
try {
    # -- [1/4] Clean ---------------------------------------------------------
    Remove-Item -Recurse -Force (Join-Path $RootDir '.sonarqube')   -ErrorAction SilentlyContinue
    Remove-Item -Recurse -Force (Join-Path $RootDir 'TestResults')  -ErrorAction SilentlyContinue

    # -- [2/4] Begin ---------------------------------------------------------
    # Notes:
    #  - sonar.sources / sonar.tests are NOT set: the .NET scanner derives them
    #    from the MSBuild graph, and setting them makes it fail. The xUnit
    #    project (SilentScan.Tests) is auto-detected as tests.
    #  - sonar.scanner.scanAll=true is what pulls non-MSBuild files (fixture
    #    .sql, docker-compose.yml, shell scripts) in alongside the compiled C#.
    $beginArgs = @(
        "/k:$ProjectKey"
        "/n:SilentScan"
        "/d:sonar.host.url=$HostUrl"
        "/d:sonar.token=$Token"
        "/d:sonar.scanner.scanAll=true"
        "/d:sonar.sourceEncoding=UTF-8"
        "/d:sonar.exclusions=**/bin/**,**/obj/**,**/corpus/**,**/.sonarqube/**,**/*.scanbak"
    )
    if ($WithCoverage) {
        $beginArgs += @(
            "/d:sonar.cs.opencover.reportsPaths=$RootDir/TestResults/**/*.opencover.xml"
            "/d:sonar.cs.vstest.reportsPaths=$RootDir/TestResults/**/*.trx"
        )
    }
    $exit = Invoke-Step "sonarscanner begin" "dotnet" (@('sonarscanner', 'begin') + $beginArgs) $BeginTimeoutSeconds
    if ($exit -ne 0) { throw "sonarscanner begin failed" }

    # -- [3/4] Build (this is what makes C# analysis happen) -----------------
    # --no-incremental is mandatory: the scanner only sees files that are
    # actually recompiled, so an up-to-date incremental build yields an empty
    # C# analysis. A failing project is not fatal - everything that did compile
    # is still analyzed - but it is called out loudly.
    #
    # Routed through scripts/dotnet-safe.sh, not a bare `dotnet build` - that script is the one
    # sanctioned entry point for building/testing this solution (docs/local-dev.md), and it
    # already owns the exact VBCSCompiler-crash retry/cleanup this script would otherwise have to
    # duplicate. DOTNET_SAFE_TIMEOUT sizes its own internal per-attempt timeout to match this
    # step's own budget rather than dotnet-safe.sh's unrelated 900s default.
    $env:DOTNET_SAFE_TIMEOUT = "$BuildTimeoutSeconds"
    $exit = Invoke-Step "build" (Join-Path $RootDir 'scripts/dotnet-safe.sh') @('build', $Solution, '--no-incremental', '-v', 'minimal', '--nologo') $BuildTimeoutSeconds
    if ($exit -ne 0) {
        $buildFailed = $true
        Write-Warning "Build reported errors. Projects that failed to compile are NOT analyzed - their C# results will be missing. Continuing so the rest of the scan still uploads."
    }

    if ($WithCoverage) {
        $env:DOTNET_SAFE_TIMEOUT = "$TestTimeoutSeconds"
        $exit = Invoke-Step "test" (Join-Path $RootDir 'scripts/dotnet-safe.sh') @(
            'test', $Solution, '--no-build',
            '--collect', 'XPlat Code Coverage;Format=opencover',
            '--results-directory', (Join-Path $RootDir 'TestResults'),
            '--logger', 'trx',
            '--verbosity', 'quiet'
        ) $TestTimeoutSeconds
        if ($exit -ne 0) {
            $dotnetTestFailed = $true
            Write-Warning ".NET tests had failures - coverage will still be uploaded"
        }
    }

    # -- [4/4] End / upload --------------------------------------------------
    # Captured separately from Invoke-Step (rather than reusing its plain exit-code return)
    # because the Compute-Engine task id below is parsed out of this step's own stdout.
    Write-Verbose "=== sonarscanner end (timeout ${EndTimeoutSeconds}s) ==="
    $endTimeoutArgs = @('--kill-after=10', "${EndTimeoutSeconds}s", 'dotnet', 'sonarscanner', 'end', "/d:sonar.token=$Token")
    $endOutput = & timeout @endTimeoutArgs 2>&1
    $endExit   = $LASTEXITCODE
    if ($IsVerbose) {
        $endOutput | ForEach-Object { Write-Host $_ }
    } elseif ($endExit -ne 0) {
        Write-Host ""
        Write-Host "--- sonarscanner end failed - output follows ---" -ForegroundColor Red
        $endOutput | ForEach-Object { Write-Host $_ }
        Write-Host "--- end sonarscanner end output ---" -ForegroundColor Red
    }
    if ($endExit -eq 124 -or $endExit -eq 137) { throw "sonarscanner end timed out after ${EndTimeoutSeconds}s and was killed." }
    if ($endExit -ne 0) { throw "sonarscanner end failed" }

    # -- Wait for background processing --------------------------------------
    # The upload above is async: SonarQube queues a Compute Engine task and
    # returns immediately. Issues aren't queryable against this run's data
    # until that task reports SUCCESS, so poll it rather than assuming the
    # scanner exiting means results are ready. A missing/null task id or a
    # null status from the API is treated as a hard failure, not skipped -
    # silently proceeding here is exactly what previously let stale/wrong
    # results get read after a "successful" scan.
    $taskIdLine = $endOutput | Select-String -Pattern 'api/ce/task\?id=([\w-]+)' | Select-Object -Last 1
    if (-not $taskIdLine -or -not $taskIdLine.Matches[0].Groups[1].Success) {
        throw "Could not find the Compute Engine task id in scanner output. Refusing to report success without confirming SonarQube processed this analysis - re-run with -Verbose if this persists."
    }
    $taskId = $taskIdLine.Matches[0].Groups[1].Value
    if ([string]::IsNullOrWhiteSpace($taskId)) {
        throw "Compute Engine task id extracted from scanner output was empty."
    }

    Write-Verbose "Waiting for SonarQube to process analysis (task $taskId)..."
    $ceStatus = 'PENDING'
    $elapsed = 0
    $pollIntervalSeconds = 2
    $timeoutSeconds = 120
    while ($ceStatus -in @('PENDING', 'IN_PROGRESS') -and $elapsed -lt $timeoutSeconds) {
        Start-Sleep -Seconds $pollIntervalSeconds
        $elapsed += $pollIntervalSeconds
        $task = Invoke-RestMethod -Uri "$HostUrl/api/ce/task?id=$taskId" -Headers $authHeader -TimeoutSec $RestTimeoutSeconds
        if (-not $task -or -not $task.task -or -not $task.task.status) {
            throw "SonarQube returned no task for id '$taskId' (host $HostUrl). This is the failure mode where a task id looks present but the API can't find it - check the token/host are pointed at the same server instance that received the upload."
        }
        $ceStatus = $task.task.status
        Write-Verbose "  [$($elapsed)s] status=$ceStatus"
    }
    if ($ceStatus -in @('PENDING', 'IN_PROGRESS')) {
        throw "Timed out after ${timeoutSeconds}s waiting for task $taskId to finish (last status: $ceStatus)."
    }
    if ($ceStatus -ne 'SUCCESS') { throw "SonarQube background processing did not succeed (status: $ceStatus)" }
}
finally {
    Pop-Location
    if ($PropsStashed -and (Test-Path $PropsBackup)) {
        Move-Item -Force $PropsBackup $PropsFile
    }
    if ($LockStream) {
        $LockStream.Close()
        Remove-Item -Force $LockPath -ErrorAction SilentlyContinue
    }
    & dotnet build-server shutdown *> $null
    Wait-ForStrayBuildProcesses
}

if ($buildFailed)      { Write-Warning "Build did not fully succeed - some C# files were not analyzed." }
if ($dotnetTestFailed) { Write-Warning ".NET tests had failures." }

# -- Final result --------------------------------------------------------
# What sonar-check-issues.sh used to be a required second step for - folded in
# here so a scan's actual result (not just "the upload succeeded") is always
# what this script ends on.
$issues = (Invoke-RestMethod -Uri "$HostUrl/api/issues/search?componentKeys=$ProjectKey&resolved=false&ps=200" -Headers $authHeader -TimeoutSec $RestTimeoutSeconds).issues
$hotspots = (Invoke-RestMethod -Uri "$HostUrl/api/hotspots/search?projectKey=$ProjectKey&status=TO_REVIEW" -Headers $authHeader -TimeoutSec $RestTimeoutSeconds).hotspots
$gateStatus = (Invoke-RestMethod -Uri "$HostUrl/api/qualitygates/project_status?projectKey=$ProjectKey" -Headers $authHeader -TimeoutSec $RestTimeoutSeconds).projectStatus.status

Write-Host ""
if ($issues.Count -eq 0 -and $hotspots.Count -eq 0 -and $gateStatus -eq 'OK') {
    Write-Host "Quality gate: OK  -  0 issues, 0 hotspots to review" -ForegroundColor Green
} else {
    # One issue/hotspot per block of plain lines, not Format-Table: -AutoSize measures the
    # console width, which a piped/redirected/CI invocation reports as narrow or absent - that
    # silently truncated or dropped whole columns (the Message text, the most important part of
    # "show specific details of the failure") the first time this ran non-interactively.
    if ($issues.Count -gt 0) {
        Write-Host "Issues ($($issues.Count)):" -ForegroundColor Yellow
        $issues | Sort-Object severity | ForEach-Object {
            $loc = "$($_.component -replace '^[^:]+:', ''):$($_.line)"
            Write-Host "  [$($_.severity)] $loc  ($($_.rule))" -ForegroundColor Yellow
            Write-Host "    $($_.message)"
        }
        Write-Host ""
    }

    if ($hotspots.Count -gt 0) {
        Write-Host "Security hotspots to review ($($hotspots.Count)):" -ForegroundColor Yellow
        $hotspots | ForEach-Object {
            $loc = "$($_.component -replace '^[^:]+:', ''):$($_.line)"
            Write-Host "  [$($_.vulnerabilityProbability)] $loc" -ForegroundColor Yellow
            Write-Host "    $($_.message)"
        }
        Write-Host ""
    }

    $color = if ($gateStatus -eq 'OK') { 'Green' } else { 'Red' }
    Write-Host "Quality gate: $gateStatus" -ForegroundColor $color
}

Write-Host "Dashboard: $HostUrl/dashboard?id=$ProjectKey" -ForegroundColor DarkGray
if (-not $IsVerbose) {
    Write-Host "(use -Verbose to see full scan/build/test output)" -ForegroundColor DarkGray
}

if ($gateStatus -ne 'OK') { exit 1 }
