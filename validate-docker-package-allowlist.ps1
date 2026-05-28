param(
    [string]$ProjectPath = ".\ASAP.csproj",
    [string]$AssetsPath = ".\obj\project.assets.json",
    [string]$DockerIgnorePath = ".\.dockerignore",
    [string]$PackagesPath = ".\packages",
    [switch]$SkipRestore
)

$ErrorActionPreference = "Stop"

if (-not $SkipRestore) {
    Write-Host "[Validate] Restoring assets graph..." -ForegroundColor Cyan
    dotnet restore $ProjectPath | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "dotnet restore failed" }
}

if (-not (Test-Path $AssetsPath)) {
    throw "Assets file not found: $AssetsPath"
}

if (-not (Test-Path $DockerIgnorePath)) {
    throw ".dockerignore not found: $DockerIgnorePath"
}

if (-not (Test-Path $PackagesPath)) {
    throw "packages directory not found: $PackagesPath"
}

$assets = Get-Content $AssetsPath -Raw | ConvertFrom-Json
$requiredPackages = @(
    $assets.libraries.PSObject.Properties.Name |
        Where-Object { $_ -match '/' } |
        ForEach-Object {
            $name, $version = $_ -split '/'
            '{0}.{1}.nupkg' -f $name.ToLowerInvariant(), $version.ToLowerInvariant()
        } |
        Sort-Object -Unique
)

$allowlistedPackages = @(
    Get-Content $DockerIgnorePath |
        Where-Object { $_ -like '!packages/*.nupkg' } |
        ForEach-Object { ($_ -replace '^!packages/', '').Trim().ToLowerInvariant() } |
        Sort-Object -Unique
)

$missingFromAllowlist = @($requiredPackages | Where-Object { $_ -notin $allowlistedPackages })
$extraInAllowlist = @($allowlistedPackages | Where-Object { $_ -notin $requiredPackages })
$missingPackageFiles = @($requiredPackages | Where-Object { -not (Test-Path (Join-Path $PackagesPath $_)) })

if ($missingFromAllowlist.Count -eq 0 -and $extraInAllowlist.Count -eq 0 -and $missingPackageFiles.Count -eq 0) {
    $packageBytes = ($requiredPackages | ForEach-Object {
        (Get-Item (Join-Path $PackagesPath $_)).Length
    } | Measure-Object -Sum).Sum

    Write-Host "[Validate] Docker package allowlist is in sync." -ForegroundColor Green
    Write-Host ("[Validate] Required packages: {0} files / {1:N2} MB" -f $requiredPackages.Count, ($packageBytes / 1MB)) -ForegroundColor Gray
    exit 0
}

Write-Host "[Validate] Docker package allowlist mismatch detected." -ForegroundColor Red

if ($missingFromAllowlist.Count -gt 0) {
    Write-Host "Missing from .dockerignore allowlist:" -ForegroundColor Yellow
    $missingFromAllowlist | ForEach-Object { Write-Host ("  !packages/{0}" -f $_) }
}

if ($extraInAllowlist.Count -gt 0) {
    Write-Host "No longer needed in .dockerignore allowlist:" -ForegroundColor Yellow
    $extraInAllowlist | ForEach-Object { Write-Host ("  !packages/{0}" -f $_) }
}

if ($missingPackageFiles.Count -gt 0) {
    Write-Host "Missing package files in packages/:" -ForegroundColor Yellow
    $missingPackageFiles | ForEach-Object { Write-Host ("  {0}" -f $_) }
}

exit 1