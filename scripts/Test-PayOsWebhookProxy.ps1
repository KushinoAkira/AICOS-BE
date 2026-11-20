[CmdletBinding()]
param([switch]$Run, [string]$PythonImage)
$ErrorActionPreference = 'Stop'
$repository = Split-Path $PSScriptRoot -Parent
if (-not $Run) { Write-Output 'Plan only: two scratch containers, internal network, no host ports, no real Gateway/provider/DB. Supply -Run -PythonImage sha256:<reviewed local image>.'; return }
if ($PythonImage -notmatch '^sha256:[0-9a-f]{64}$') { throw 'An immutable reviewed local Python image is required.' }
$nginxImage = 'nginx@sha256:caf4e1e032a20f9c3c5e28fc7f25ffd768ad8677b4975c54193075997b3a97e3'

function Invoke-TestDocker([string[]]$Arguments) {
    $output = & docker @Arguments
    if ($LASTEXITCODE -ne 0) { throw ('Docker operation failed: ' + $Arguments[0]) }
    return $output
}
function RuntimeSnapshot {
    $ids = @(Invoke-TestDocker -Arguments @('ps', '-aq'))
    if ($ids.Count -eq 0) { return @() }
    $items = Invoke-TestDocker -Arguments (@('inspect') + $ids) | ConvertFrom-Json
    return @($items | ForEach-Object {
        [pscustomobject]@{ Id=$_.Id; Image=$_.Image; StartedAt=$_.State.StartedAt; Status=$_.State.Status }
    })
}
$null = Invoke-TestDocker -Arguments @('image', 'inspect', $nginxImage, '--format', '{{.Id}}')
$null = Invoke-TestDocker -Arguments @('image', 'inspect', $PythonImage, '--format', '{{.Id}}')
$before = @(RuntimeSnapshot)
$suffix = [Guid]::NewGuid().ToString('N').Substring(0,10)
$network = 'aicos-payos-proxy-test-' + $suffix
$networkId = $null
$containers = @()
$result = [ordered]@{ Status='FAIL'; Purpose='isolated payOS webhook transport'; NginxImage=$nginxImage; PythonImage=$PythonImage; Network=$network; NoHostPorts=$true; Tests=@(); ExistingContainersUnchanged=$false; Cleanup=$false }
try {
    $networkId = Invoke-TestDocker -Arguments @('network', 'create', '--internal', '--label', 'aicos.purpose=payos-proxy-test', $network)
    $networkInfo = Invoke-TestDocker -Arguments @('network', 'inspect', $network) | ConvertFrom-Json
    if ($networkInfo.Id -ne $networkId -or -not $networkInfo.Internal) { throw 'Scratch network guard failed.' }
    $fixtures = Join-Path $repository 'tests/fixtures/payos-webhook'
    $config = Join-Path $repository 'infra/payos-webhook/nginx.conf'
    $resultPage = Join-Path $repository 'infra/payos-webhook/result.html'
    $mockName = $network + '-upstream'
    $mockId = Invoke-TestDocker -Arguments @('run','-d','--name',$mockName,'--network',$network,'--network-alias','gateway',
        '--label','aicos.purpose=payos-proxy-test','--read-only','--user','65534:65534','--cap-drop','ALL',
        '--security-opt','no-new-privileges:true','--memory','128m','--pids-limit','64',
        '--mount',("type=bind,source=$fixtures,target=/tests,readonly"),'--entrypoint','python',$PythonImage,'-B','/tests/upstream.py')
    $containers += [pscustomobject]@{ Name=$mockName; Id=$mockId }
    $proxyName = $network + '-proxy'
    $proxyId = Invoke-TestDocker -Arguments @('run','-d','--name',$proxyName,'--network',$network,'--network-alias','proxy',
        '--label','aicos.purpose=payos-proxy-test','--read-only','--user','101:101','--cap-drop','ALL',
        '--security-opt','no-new-privileges:true','--memory','64m','--pids-limit','64',
        '--tmpfs','/tmp:rw,noexec,nosuid,size=16m,mode=1777',
        '--mount',("type=bind,source=$config,target=/etc/nginx/nginx.conf,readonly"),
        '--mount',("type=bind,source=$resultPage,target=/srv/payos/result.html,readonly"),
        '--entrypoint','nginx',$nginxImage,'-g','daemon off;')
    $containers += [pscustomobject]@{ Name=$proxyName; Id=$proxyId }
    foreach ($container in $containers) {
        $actual = Invoke-TestDocker -Arguments @('inspect',$container.Name) | ConvertFrom-Json
        if ($actual.Id -ne $container.Id -or $actual.Config.Labels.'aicos.purpose' -ne 'payos-proxy-test' -or
            @($actual.NetworkSettings.Networks.PSObject.Properties.Name).Count -ne 1 -or
            $actual.NetworkSettings.Networks.PSObject.Properties.Name -ne $network -or
            @($actual.HostConfig.PortBindings.PSObject.Properties.Name | Where-Object { $_ }).Count -ne 0 -or
            @($actual.Mounts | Where-Object Type -eq 'volume').Count -ne 0) { throw 'Container isolation guard failed.' }
    }
    $null = Invoke-TestDocker -Arguments @('exec',$proxyName,'nginx','-t')
    $ready = $false
    for ($probe=0; $probe -lt 20; $probe++) {
        & docker exec $mockName python -B /tests/checks.py --ready 2>$null | Out-Null
        if ($LASTEXITCODE -eq 0) { $ready=$true; break }
        Start-Sleep -Milliseconds 250
    }
    if (-not $ready) { throw 'Scratch processes did not start.' }
    $result.Tests = @(Invoke-TestDocker -Arguments @('exec',$mockName,'python','-B','/tests/checks.py'))
    $result.Status = 'PASS'
} catch {
    foreach ($container in $containers) {
        Write-Output ('Scratch diagnostics: '+$container.Name)
        & docker logs --tail 15 $container.Id
    }
    throw
} finally {
    foreach ($container in $containers) {
        $actual = Invoke-TestDocker -Arguments @('inspect',$container.Name) | ConvertFrom-Json
        if ($actual.Id -ne $container.Id -or $actual.Config.Labels.'aicos.purpose' -ne 'payos-proxy-test') { throw 'Cleanup identity mismatch; no deletion attempted.' }
        $null = Invoke-TestDocker -Arguments @('rm','-f',$container.Id)
    }
    if ($networkId) {
        $actual = Invoke-TestDocker -Arguments @('network','inspect',$network) | ConvertFrom-Json
        if ($actual.Id -ne $networkId -or $actual.Labels.'aicos.purpose' -ne 'payos-proxy-test' -or @($actual.Containers.PSObject.Properties.Name | Where-Object { $_ }).Count -ne 0) { throw 'Network cleanup guard failed.' }
        $null = Invoke-TestDocker -Arguments @('network','rm',$networkId)
    }
    $after = @(RuntimeSnapshot)
    foreach ($original in $before) {
        $current = @($after | Where-Object Id -eq $original.Id)
        if ($current.Count -ne 1 -or ($current[0] | ConvertTo-Json -Compress) -cne ($original | ConvertTo-Json -Compress)) { throw 'Existing container state changed; investigate before continuing.' }
    }
    $result.ExistingContainersUnchanged = $true
    $result.ExistingContainerCount = $before.Count
    $result.Cleanup = $true
    $result | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $repository 'docs/PAYOS_WEBHOOK_PROXY_TEST_RESULT.json') -Encoding UTF8
    Write-Output ('Result: '+$result.Status+'; scratch removed; existing containers unchanged: '+$before.Count)
}
