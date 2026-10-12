param(
    [Parameter(Mandatory)][string]$LicenseFile,
    [ValidatePattern('^[a-z0-9][a-z0-9_-]*$')][string]$ProjectName = 'signal-forge',
    [ValidateRange(1, 65535)][int]$Port = $(if ($env:SIGNAL_FORGE_PORT) { [int]$env:SIGNAL_FORGE_PORT } else { 5237 })
)
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
$OutputEncoding = [Console]::OutputEncoding
$env:RTI_LICENSE_FILE = (Resolve-Path -LiteralPath $LicenseFile).Path
$env:SIGNAL_FORGE_PORT = [string]$Port
$imageArchive = Join-Path $PSScriptRoot 'signal-forge-images.tar'
if (Test-Path -LiteralPath $imageArchive) {
    & docker load -i $imageArchive
    if ($LASTEXITCODE -ne 0) { throw 'Docker 이미지 불러오기에 실패했습니다.' }
}
& docker compose -p $ProjectName -f (Join-Path $PSScriptRoot 'compose.yaml') up -d --pull never
if ($LASTEXITCODE -ne 0) { throw '배포에 실패했습니다.' }
Write-Host "배포 프로젝트: $ProjectName / 접속 주소: http://localhost:$Port"
