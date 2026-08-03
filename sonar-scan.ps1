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
    [switch]$WithCoverage = $true
)

$ErrorActionPreference = 'Stop'
$RootDir  = $PSScriptRoot
$Solution = Join-Path $RootDir 'SilentScan.slnx'
$IsVerbose = $VerbosePreference -eq 'Continue'

# Runs an external command, capturing its combined output. Streamed live only
# under -Verbose; otherwise captured silently and dumped in full ONLY if the
# command fails - so a failure's specific detail is never lost, but a clean
# run never has to scroll past four steps of routine build/scan noise to find
# out whether it passed.
function Invoke-Step {
    param(
        [Parameter(Mandatory)] [string]$Label,
        [Parameter(Mandatory)] [scriptblock]$Command
    )
    Write-Verbose "=== $Label ==="
    if ($IsVerbose) {
        & $Command
        $exit = $LASTEXITCODE
    } else {
        $output = & $Command 2>&1
        $exit = $LASTEXITCODE
        if ($exit -ne 0) {
            Write-Host ""
            Write-Host "--- $Label failed - output follows ---" -ForegroundColor Red
            $output | ForEach-Object { Write-Host $_ }
            Write-Host "--- end $Label output ---" -ForegroundColor Red
        }
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
    -Body @{ name = "$ProjectKey-scan-$([DateTimeOffset]::Now.ToUnixTimeSeconds())" }
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
    $exit = Invoke-Step "sonarscanner begin" { dotnet sonarscanner begin @beginArgs }
    if ($exit -ne 0) { throw "sonarscanner begin failed" }

    # -- [3/4] Build (this is what makes C# analysis happen) -----------------
    # --no-incremental is mandatory: the scanner only sees files that are
    # actually recompiled, so an up-to-date incremental build yields an empty
    # C# analysis. A failing project is not fatal - everything that did compile
    # is still analyzed - but it is called out loudly.
    $exit = Invoke-Step "build" { dotnet build $Solution --no-incremental -v minimal --nologo }
    if ($exit -ne 0) {
        $buildFailed = $true
        Write-Warning "Build reported errors. Projects that failed to compile are NOT analyzed - their C# results will be missing. Continuing so the rest of the scan still uploads."
    }

    if ($WithCoverage) {
        $exit = Invoke-Step "test" {
            dotnet test $Solution --no-build `
                --collect "XPlat Code Coverage;Format=opencover" `
                --results-directory (Join-Path $RootDir 'TestResults') `
                --logger trx `
                --verbosity quiet
        }
        if ($exit -ne 0) {
            $dotnetTestFailed = $true
            Write-Warning ".NET tests had failures - coverage will still be uploaded"
        }
    }

    # -- [4/4] End / upload --------------------------------------------------
    Write-Verbose "=== sonarscanner end ==="
    $endOutput = dotnet sonarscanner end /d:sonar.token="$Token" 2>&1
    $endExit   = $LASTEXITCODE
    if ($IsVerbose) {
        $endOutput | ForEach-Object { Write-Host $_ }
    } elseif ($endExit -ne 0) {
        Write-Host ""
        Write-Host "--- sonarscanner end failed - output follows ---" -ForegroundColor Red
        $endOutput | ForEach-Object { Write-Host $_ }
        Write-Host "--- end sonarscanner end output ---" -ForegroundColor Red
    }
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
        $task = Invoke-RestMethod -Uri "$HostUrl/api/ce/task?id=$taskId" -Headers $authHeader
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
}

if ($buildFailed)      { Write-Warning "Build did not fully succeed - some C# files were not analyzed." }
if ($dotnetTestFailed) { Write-Warning ".NET tests had failures." }

# -- Final result --------------------------------------------------------
# What sonar-check-issues.sh used to be a required second step for - folded in
# here so a scan's actual result (not just "the upload succeeded") is always
# what this script ends on.
$issues = (Invoke-RestMethod -Uri "$HostUrl/api/issues/search?componentKeys=$ProjectKey&resolved=false&ps=200" -Headers $authHeader).issues
$hotspots = (Invoke-RestMethod -Uri "$HostUrl/api/hotspots/search?projectKey=$ProjectKey&status=TO_REVIEW" -Headers $authHeader).hotspots
$gateStatus = (Invoke-RestMethod -Uri "$HostUrl/api/qualitygates/project_status?projectKey=$ProjectKey" -Headers $authHeader).projectStatus.status

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
