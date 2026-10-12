param([switch]$Rti, [switch]$Docker, [switch]$RuntimeStress)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
if ($RuntimeStress -and -not $Docker) { throw '실제 DDS 부하·종료 실패 검증은 -Docker 격리 환경에서 실행하세요.' }
if ($RuntimeStress -and -not $env:RTI_LICENSE_FILE) { throw 'RTI_LICENSE_FILE로 유효한 라이선스를 지정하세요.' }
if ($Docker) {
    $context = Split-Path $repo -Parent
    $tag = 'signal-forge-ddsclient-integration:' + (Get-Date -Format 'yyyyMMddHHmmss')
    & docker build --target integration-tests -f (Join-Path $repo 'Dockerfile') -t $tag $context
    if ($LASTEXITCODE -ne 0) { throw 'Integration image build failed.' }
    $runArgs = @('run', '--rm', '--network', 'none', '--name', ('signal-forge-ddsclient-test-' + [guid]::NewGuid().ToString('N')))
    if ($RuntimeStress) { $runArgs += @('--cpus', '2', '--memory', '768m') }
    if ($env:RTI_LICENSE_FILE) {
        $licensePath = (Resolve-Path -LiteralPath $env:RTI_LICENSE_FILE).Path
        $runArgs += @('--mount', "type=bind,source=$licensePath,target=/run/rti/license.dat,readonly", '-e', 'RTI_LICENSE_FILE=/run/rti/license.dat')
    }
    $runArgs += $tag
    if ($RuntimeStress) { $runArgs += '--runtime-stress' }
    elseif ($Rti) { $runArgs += '--rti' }
    & docker @runArgs
    if ($LASTEXITCODE -ne 0) { throw 'Container integration checks failed.' }
    return
}
$project = Join-Path $repo 'tests/SignalForge.DdsIntegrationTests/SignalForge.DdsIntegrationTests.csproj'
$props = Join-Path $repo 'eng/Integration.Build.props'
& dotnet build $project -c Release "-p:DirectoryBuildPropsPath=$props" --configfile (Join-Path $repo 'nuget.config') --disable-build-servers
if ($LASTEXITCODE -ne 0) { throw 'Integration build failed.' }
$testArgs = @((Join-Path $repo 'artifacts/integration/SignalForge.DdsIntegrationTests/bin/Release/net9.0/SignalForge.DdsIntegrationTests.dll'))
if ($Rti) { $testArgs += '--rti' }
& dotnet @testArgs
if ($LASTEXITCODE -ne 0) { throw 'Integration checks failed.' }
