# Live Stack Custom Fork - Dev Deploy Script (run on the NINA machine)
# Usage: .\scripts\dev-deploy.ps1
#
# What this does:
#   1. Closes NINA if running (so DLLs are unlocked)
#   2. Removes the stock Livestack plugin if present
#   3. Pulls latest and builds the plugin (PostBuild in csproj handles deployment)

$ErrorActionPreference = "Stop"
$repoRoot     = Split-Path -Parent $PSScriptRoot
$solutionFile = Join-Path $repoRoot "nina.plugin.livestack.sln"

# --- Show current branch ---
$currentBranch = git -C $repoRoot rev-parse --abbrev-ref HEAD
if ($LASTEXITCODE -ne 0) { Write-Error "Failed to detect current branch."; exit 1 }
Write-Host "Building from branch: $currentBranch" -ForegroundColor Cyan

# --- Close NINA if running ---
function Close-NINA {
    $ninaProc = Get-Process -Name "NINA" -ErrorAction SilentlyContinue
    if (-not $ninaProc) { return $false }

    Write-Host "NINA is running. Closing NINA..." -ForegroundColor Yellow
    $ninaProc | ForEach-Object { $_.CloseMainWindow() | Out-Null }
    $timeout = 15
    $waited = 0
    while ((Get-Process -Name "NINA" -ErrorAction SilentlyContinue) -and $waited -lt $timeout) {
        Start-Sleep -Seconds 1
        $waited++
    }
    if (Get-Process -Name "NINA" -ErrorAction SilentlyContinue) {
        Write-Host "NINA did not close gracefully after ${timeout}s. Force killing..." -ForegroundColor Red
        Stop-Process -Name "NINA" -Force
        Start-Sleep -Seconds 2
    }
    Write-Host "NINA closed." -ForegroundColor Green
    return $true
}

Close-NINA | Out-Null

# --- Remove stock versioned Livestack DLL if present (causes duplicate MEF export) ---
$stockDir = Join-Path $env:LOCALAPPDATA "NINA\Plugins\3.0.0\Livestack"
if (Test-Path $stockDir) {
    $versionedDlls = Get-ChildItem $stockDir -Filter "nina.plugin.livestack-*.dll"
    foreach ($dll in $versionedDlls) {
        Write-Host "Removing stock versioned DLL: $($dll.Name)" -ForegroundColor Yellow
        Remove-Item $dll.FullName -Force
    }
    if ($versionedDlls.Count -eq 0) {
        Write-Host "No stock versioned DLLs found. Build will overwrite existing." -ForegroundColor Cyan
    }
}

# --- Pull latest ---
Write-Host "Pulling latest from origin..." -ForegroundColor Cyan
git -C $repoRoot pull
if ($LASTEXITCODE -ne 0) { Write-Error "git pull failed."; exit 1 }
Write-Host "Pull complete." -ForegroundColor Green

# --- Restore, clean, and build (PostBuild target in csproj deploys to NINA plugins folder) ---
Write-Host "Restoring packages..." -ForegroundColor Cyan
dotnet restore $solutionFile --nologo -v q
if ($LASTEXITCODE -ne 0) { Write-Error "Restore failed."; exit 1 }

Write-Host "Cleaning and building..." -ForegroundColor Cyan
dotnet clean $solutionFile -c Release --nologo -v q 2>$null
dotnet build $solutionFile -c Release --no-restore --nologo -v q -p:WarningLevel=0 -clp:ErrorsOnly
if ($LASTEXITCODE -ne 0) { Write-Error "Build failed."; exit 1 }

Write-Host ""
Write-Host "Done. Built and deployed from '$currentBranch'. Start NINA when ready." -ForegroundColor White
Start-Sleep -Seconds 5
