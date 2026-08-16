#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Count lines of code by file type, excluding markdown and build artifacts.
.DESCRIPTION
    Analyzes src/, tests/, and scripts/ directories to report line counts
    broken down by file extension.
#>

param(
    [switch]$Detailed
)

$scriptRoot = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent (Resolve-Path $MyInvocation.MyCommand.Path) }
$projectRoot = Split-Path -Parent $scriptRoot

$fileTypes = @(
    @{ Extension = '*.cs'; Name = 'C# (.cs)' }
    @{ Extension = '*.csproj'; Name = 'Projects (.csproj)' }
    @{ Extension = '*.json'; Name = 'JSON (.json)' }
    @{ Extension = '*.ps1'; Name = 'PowerShell (.ps1)' }
    @{ Extension = '*.sh'; Name = 'Shell (.sh)' }
)

$totals = @{}
$allFiles = @()

foreach ($type in $fileTypes) {
    $files = @()

    foreach ($dir in @('src', 'tests', 'scripts')) {
        $path = Join-Path $projectRoot $dir
        if (Test-Path $path) {
            $files += @(Get-ChildItem -Path $path -Filter $type.Extension -Recurse -File -ErrorAction SilentlyContinue |
                Where-Object { $_.FullName -notmatch '(\\|/)?(bin|obj)(\\|/)' })
        }
    }

    $lineCount = 0
    $fileCount = $files.Count

    if ($fileCount -gt 0) {
        foreach ($file in $files) {
            $lines = @(Get-Content $file.FullName -ErrorAction SilentlyContinue)
            if ($lines -is [array]) {
                $lineCount += $lines.Count
            } else {
                $lineCount += 1
            }
        }
    }

    $totals[$type.Name] = @{
        Count = $lineCount
        Files = $fileCount
    }
    $allFiles += $files
}

# Display results
Write-Host ""
Write-Host "Line Count by File Type" -ForegroundColor Cyan
Write-Host "─────────────────────────────────────" -ForegroundColor Cyan

$grandTotal = 0
$grandFileCount = 0

foreach ($type in $fileTypes) {
    $name = $type.Name
    $count = $totals[$name].Count
    $fileCount = $totals[$name].Files
    $grandTotal += $count
    $grandFileCount += $fileCount

    if ($Detailed) {
        Write-Host ("{0,-25} {1,8:N0} lines  ({2,4} files)" -f $name, $count, $fileCount)
    } else {
        Write-Host ("{0,-25} {1,8:N0} lines" -f $name, $count)
    }
}

Write-Host "─────────────────────────────────────" -ForegroundColor Cyan
if ($Detailed) {
    Write-Host ("{0,-25} {1,8:N0} lines  ({2,4} files)" -f "Total:", $grandTotal, $grandFileCount) -ForegroundColor Green
} else {
    Write-Host ("{0,-25} {1,8:N0} lines" -f "Total:", $grandTotal) -ForegroundColor Green
}
Write-Host ""
