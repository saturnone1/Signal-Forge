param(
    [switch]$SkipBuild,
    [switch]$Teardown
)

$ErrorActionPreference = "Stop"

if ($Teardown) {
    Write-Host "[Teardown] Removing test resources..." -ForegroundColor Yellow
    kubectl delete pod     grpc-workbench-test --ignore-not-found
    kubectl delete service grpc-workbench-test --ignore-not-found
    Write-Host "[Teardown] Done." -ForegroundColor Green
    exit 0
}

if (-not $SkipBuild) {
    Write-Host ""
    Write-Host "Building grpc-workbench:test image (no-cache)..." -ForegroundColor Cyan
    docker build --no-cache -t grpc-workbench:test .
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
kubectl delete pod     grpc-workbench-test --ignore-not-found --wait=false 2>$null
kubectl delete service grpc-workbench-test --ignore-not-found 2>$null

# Wait until pod is gone (ignore errors when pod doesn't exist)
$timeout = 30; $elapsed = 0
while ($elapsed -lt $timeout) {
    $pExists = $false
    try { $pExists = [bool](kubectl get pod grpc-workbench-test --no-headers 2>$null) } catch {}
    if (-not $pExists) { break }
    Start-Sleep -Seconds 2; $elapsed += 2
}

Write-Host ""
Write-Host "Deploying pod..." -ForegroundColor Cyan
kubectl apply -f k8s/local-test-pod.yaml
if ($LASTEXITCODE -ne 0) { throw "Deploy failed" }

Write-Host ""
Write-Host "Waiting for pod ready (workbench-a + workbench-b)..." -ForegroundColor Yellow

$maxWait = 180; $elapsed = 0; $ready = $false
while ($elapsed -lt $maxWait) {
    $podJson = kubectl get pod grpc-workbench-test -o json 2>$null | ConvertFrom-Json
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
    Write-Host "  Workbench A: http://localhost:30226   (gRPC server: /var/run/dds-ambassador/grpc.sock)"
    Write-Host "  Workbench B: http://localhost:30227   (gRPC server: /var/run/dds-ambassador/grpc.sock)"
    Write-Host ""
    Write-Host "  [테스트 방법]"
    Write-Host "  A에서 B로 요청:  A UI에서 소켓경로 /var/run/dds-ambassador-peer/grpc.sock 로 세션 생성 → 요청 전송 → B UI에 수신 표시"
    Write-Host "  B에서 A로 요청:  B UI에서 소켓경로 /var/run/dds-ambassador-peer/grpc.sock 로 세션 생성 → 요청 전송 → A UI에 수신 표시"
    Write-Host ""
    Write-Host "  Available gRPC services (each workbench has its own built-in server):"
    Write-Host "    EchoService.Echo          (Unary)"
    Write-Host "    CounterService.Countdown  (Server Streaming)"
    Write-Host "    SumService.CalculateSum   (Client Streaming)"
    Write-Host "    ChatService.Chat          (Bidirectional)"
    Write-Host "----------------------------------------"
} else {
    Write-Host "Timeout: pod not ready yet." -ForegroundColor Yellow
    Write-Host "  Run: kubectl describe pod grpc-workbench-test"
}

Write-Host ""
Write-Host "Logs:"
Write-Host "  kubectl logs grpc-workbench-test -c grpc-workbench-a -f"
Write-Host "  kubectl logs grpc-workbench-test -c grpc-workbench-b -f"
Write-Host ""
Write-Host "Teardown: .\build-and-deploy-test.ps1 -Teardown"