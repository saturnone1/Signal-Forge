param(
    [int]$StatusTimeoutSeconds = 120,
    [int]$ReadyTimeoutSeconds = 120,
    [switch]$Force
)

$ErrorActionPreference = "Stop"

function Test-CommandExists {
    param([Parameter(Mandatory = $true)][string]$Name)

    return $null -ne (Get-Command $Name -ErrorAction SilentlyContinue)
}

function Invoke-BackendApi {
    param(
        [Parameter(Mandatory = $true)][ValidateSet("GET", "POST")][string]$Method,
        [Parameter(Mandatory = $true)][string]$Path,
        [string]$Body
    )

    $pipe = [System.IO.Pipes.NamedPipeClientStream]::new('.', 'dockerBackendApiServer', [System.IO.Pipes.PipeDirection]::InOut)
    try {
        $pipe.Connect(5000)

        $writer = New-Object System.IO.StreamWriter($pipe)
        $writer.NewLine = "`r`n"
        $writer.AutoFlush = $true

        $request = "{0} {1} HTTP/1.1`r`nHost: ipc`r`nConnection: close`r`n" -f $Method, $Path
        if ($Body) {
            $byteCount = [System.Text.Encoding]::UTF8.GetByteCount($Body)
            $request += "Content-Type: application/json`r`nContent-Length: {0}`r`n" -f $byteCount
        }
        $request += "`r`n"
        if ($Body) {
            $request += $Body
        }

        $writer.Write($request)

        $reader = New-Object System.IO.StreamReader($pipe)
        $rawResponse = $reader.ReadToEnd()
        $statusLine, $remaining = $rawResponse -split "`r`n", 2

        if (-not $statusLine -or $statusLine -notmatch '^HTTP/1\.1 (?<Code>\d{3}) ') {
            throw "Unexpected BackendAPI response for $Method $Path"
        }

        $statusCode = [int]$Matches.Code

        $bodyText = ""
        if ($remaining -match "`r`n`r`n") {
            $bodyText = ($remaining -split "`r`n`r`n", 2)[1]
        }

        if ($bodyText -match '^[0-9a-fA-F]+`r`n') {
            $bodyText = ($bodyText -replace '^[0-9a-fA-F]+`r`n', '', 1)
            $bodyText = ($bodyText -replace '`r`n0\s*$', '')
        }

        if ($statusCode -ge 400) {
            throw ("BackendAPI {0} {1} failed with HTTP {2}: {3}" -f $Method, $Path, $statusCode, $bodyText)
        }

        return [pscustomobject]@{
            StatusCode = $statusCode
            Body = $bodyText
        }
    }
    finally {
        if ($null -ne $reader) { $reader.Dispose() }
        if ($null -ne $writer) { $writer.Dispose() }
        $pipe.Dispose()
    }
}

function Get-DesktopSettings {
    $response = Invoke-BackendApi -Method GET -Path '/app/settings/flat'
    return $response.Body | ConvertFrom-Json
}

function Set-KubernetesEnabled {
    param([Parameter(Mandatory = $true)][bool]$Enabled)

    $payload = @{
        vm = @{
            kubernetes = @{
                enabled = @{
                    locked = $false
                    value = $Enabled
                }
            }
        }
    } | ConvertTo-Json -Depth 6 -Compress

    [void](Invoke-BackendApi -Method POST -Path '/app/settings' -Body $payload)
}

function Wait-DesktopRunning {
    param([Parameter(Mandatory = $true)][int]$TimeoutSeconds)

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    do {
        $statusOutput = docker desktop status 2>$null | Out-String
        if ($statusOutput -match 'Status\s+running') {
            return
        }
    } while ((Get-Date) -lt $deadline)

    throw "Docker Desktop did not report Status=running within $TimeoutSeconds seconds."
}

function Test-KubernetesHealthy {
    try {
        $null = kubectl get nodes -o wide 2>$null
        return $LASTEXITCODE -eq 0
    }
    catch {
        return $false
    }
}

function Enable-MissingCgroupControllers {
    $script = @'
set -eu
echo '+cpu +cpuset +io' > /sys/fs/cgroup/cgroup.subtree_control
mkdir -p /sys/fs/cgroup/kubepods
echo '+cpu +cpuset +io' > /sys/fs/cgroup/kubepods/cgroup.subtree_control
printf 'root=%s\n' "$(cat /sys/fs/cgroup/cgroup.subtree_control)"
printf 'kubepods=%s\n' "$(cat /sys/fs/cgroup/kubepods/cgroup.subtree_control)"
'@

    $result = wsl -d docker-desktop -u root -- sh -lc $script
    if ($LASTEXITCODE -ne 0) {
        throw "Failed to enable cgroup controllers inside docker-desktop."
    }

    return $result
}

function Wait-KubernetesReady {
    param([Parameter(Mandatory = $true)][int]$TimeoutSeconds)

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    do {
        if (Test-KubernetesHealthy) {
            return
        }
    } while ((Get-Date) -lt $deadline)

    throw "Kubernetes did not become ready within $TimeoutSeconds seconds."
}

if (-not (Test-CommandExists -Name 'docker')) {
    throw "docker is not available on PATH."
}

if (-not (Test-CommandExists -Name 'kubectl')) {
    throw "kubectl is not available on PATH."
}

Write-Host "Checking Docker Desktop status..." -ForegroundColor Cyan
Wait-DesktopRunning -TimeoutSeconds $StatusTimeoutSeconds

if ((Test-KubernetesHealthy) -and -not $Force) {
    Write-Host "Kubernetes is already healthy. No recovery action needed." -ForegroundColor Green
    exit 0
}

Write-Host "Inspecting current Docker Desktop settings..." -ForegroundColor Cyan
$settings = Get-DesktopSettings
if (-not $settings.kubernetesEnabled) {
    Write-Host "Kubernetes is currently disabled. It will be enabled during recovery." -ForegroundColor Yellow
}

Write-Host "Re-enabling missing cgroup controllers in docker-desktop..." -ForegroundColor Yellow
$controllerState = Enable-MissingCgroupControllers
Write-Host $controllerState -ForegroundColor Gray

Write-Host "Disabling Kubernetes via Docker Desktop BackendAPI..." -ForegroundColor Yellow
Set-KubernetesEnabled -Enabled $false

Write-Host "Re-enabling Kubernetes via Docker Desktop BackendAPI..." -ForegroundColor Yellow
Set-KubernetesEnabled -Enabled $true

Write-Host "Waiting for Kubernetes to become ready..." -ForegroundColor Cyan
Wait-KubernetesReady -TimeoutSeconds $ReadyTimeoutSeconds

Write-Host "Kubernetes recovery completed successfully." -ForegroundColor Green
kubectl get nodes -o wide