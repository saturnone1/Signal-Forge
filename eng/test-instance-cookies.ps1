param([Parameter(Mandatory)][string]$Image)
$ErrorActionPreference = 'Stop'
$created = [Collections.Generic.List[string]]::new()
$cookieNames = [Collections.Generic.List[string]]::new()
try {
    foreach ($id in @('a', 'b')) {
        $name = 'signal-forge-cookie-check-' + [guid]::NewGuid().ToString('N')
        & docker run -d --name $name -p '127.0.0.1::5226' -e "SignalForge__InstanceId=cookie-check-$id" $Image | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'Test container start failed.' }
        $created.Add($name)
        $inspection = (& docker inspect $name | ConvertFrom-Json)[0]
        $port = $inspection.NetworkSettings.Ports.'5226/tcp'[0].HostPort
        $response = $null
        for ($attempt = 0; $attempt -lt 40; $attempt++) {
            try { $response = Invoke-WebRequest "http://127.0.0.1:$port/" -TimeoutSec 3; break }
            catch { Start-Sleep -Milliseconds 250 }
        }
        if ($null -eq $response) { throw 'Test app not ready.' }
        $cookie = @($response.Headers['Set-Cookie'] | Where-Object { $_ -like '.SignalForge.Antiforgery.*' })[0]
        if (-not $cookie) { throw 'Missing instance-scoped cookie.' }
        $cookieNames.Add(($cookie -split '=')[0])
    }
    if ($cookieNames[0] -eq $cookieNames[1]) { throw 'Independent instances still share a cookie name.' }
    Write-Output 'PASS: two deployed instances issue distinct antiforgery cookie names.'
} finally {
    foreach ($name in $created) { & docker rm -f -v $name | Out-Null }
}
