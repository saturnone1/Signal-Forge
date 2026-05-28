# Assumptions:
#   - `docker` and `kubectl` are on PATH.
#   - kubectl's *current context* points at a working cluster whose container
#     runtime shares the image built here (imagePullPolicy: Never). Verify with
#     `kubectl config current-context` / `kubectl get nodes` before running;
#     switch with `kubectl config use-context <ctx>` if needed.
#   - Image tag: asap:test (built locally, not pulled).

param(
    [switch]$SkipBuild,
    [switch]$Teardown
)

$ErrorActionPreference = "Stop"

if ($Teardown) {
    Write-Host "[Teardown] Removing test resources..." -ForegroundColor Yellow
    kubectl delete pod     asap-test --ignore-not-found
    kubectl delete service asap-test --ignore-not-found
    Write-Host "[Teardown] Done." -ForegroundColor Green
    exit 0
}

if (-not $SkipBuild) {
    Write-Host ""
    Write-Host "Validating Docker package allowlist..." -ForegroundColor Cyan
    & .\validate-docker-package-allowlist.ps1
    if ($LASTEXITCODE -ne 0) { throw "Docker package allowlist validation failed" }

    Write-Host ""
    Write-Host "Building asap:test image (no-cache)..." -ForegroundColor Cyan
    docker build --no-cache -t asap:test .
    if ($LASTEXITCODE -ne 0) { throw "Docker build failed" }
    Write-Host "Build complete." -ForegroundColor Green

    Write-Host ""
    Write-Host "Pruning dangling images and build cache..." -ForegroundColor Yellow
    docker image prune -f | Out-Null
    docker builder prune -f | Out-Null
    Write-Host "Docker cleanup done." -ForegroundColor Green
}

Write-Host ""
Write-Host "Cleaning up old test resources..." -ForegroundColor Yellow
kubectl delete pod     asap-test --ignore-not-found --wait=false 2>$null
kubectl delete service asap-test --ignore-not-found 2>$null

# Wait until pod is gone (ignore errors when pod doesn't exist)
$timeout = 30; $elapsed = 0
while ($elapsed -lt $timeout) {
    $pExists = $false
    try { $pExists = [bool](kubectl get pod asap-test --no-headers 2>$null) } catch {}
    if (-not $pExists) { break }
    Start-Sleep -Seconds 2; $elapsed += 2
}

Write-Host ""
Write-Host "Deploying pod..." -ForegroundColor Cyan
kubectl apply -f k8s/local-test-pod.yaml
if ($LASTEXITCODE -ne 0) { throw "Deploy failed" }

Write-Host ""
Write-Host "Waiting for pod ready..." -ForegroundColor Yellow

$maxWait = 180; $elapsed = 0; $ready = $false
while ($elapsed -lt $maxWait) {
    $podJson = kubectl get pod asap-test -o json 2>$null | ConvertFrom-Json
    if ($podJson) {
        $readyCond = $podJson.status.conditions | Where-Object { $_.type -eq "Ready" }
        if ($readyCond -and $readyCond.status -eq "True") { $ready = $true; break }

        $phase = $podJson.status.phase
        $cs = $podJson.status.containerStatuses
        if ($cs) {
            $summary = ($cs | ForEach-Object {
                $st = if ($_.ready) { "Ready" } elseif ($_.state.waiting) { "Waiting:$($_.state.waiting.reason)" } else { "Running" }
                "$($_.name)=$st"
            }) -join " | "
            Write-Host "  [$($elapsed)s] $phase | $summary" -ForegroundColor Gray
        }
    }
    Start-Sleep -Seconds 4; $elapsed += 4
}

Write-Host ""
if ($ready) {
    Write-Host "Pod is ready!" -ForegroundColor Green
    Write-Host "----------------------------------------"
    Write-Host "  Workbench: http://localhost:30226   (gRPC server: /var/run/dds-ambassador/grpc.sock)"
    Write-Host ""
    Write-Host "  [테스트 방법]"
    Write-Host "  UI에서 소켓경로 /var/run/dds-ambassador/grpc.sock 로 세션 생성 → proto 업로드 → 요청 전송"
    Write-Host ""
    Write-Host "  Built-in gRPC receiver: 들어오는 모든 호출을 디코딩해 UI에 표시하고 OK를 반환합니다."
    Write-Host "  번들 proto: ddssim.DdsBridge — 16개 양방향 스트리밍 RPC"
    Write-Host "    예) StreamAirThreatInformation, StreamMunitionInformation, StreamWeaponFire ..."
    Write-Host "    (모두 BidirectionalStreaming: rpc StreamX(stream X) returns (stream X))"
    Write-Host "----------------------------------------"
} else {
    Write-Host "Timeout: pod not ready yet." -ForegroundColor Yellow
    Write-Host "  Run: kubectl describe pod asap-test"
}

Write-Host ""
Write-Host "Logs:"
Write-Host "  kubectl logs asap-test -c asap -f"
Write-Host ""
Write-Host "Teardown: .\build-and-deploy-test.ps1 -Teardown"