param(
    [Parameter(Mandatory = $true)][string]$App,
    [Parameter(Mandatory = $true)][string]$OutputDirectory
)
$ErrorActionPreference = 'Stop'
$appPath = (Resolve-Path $App).Path
$proof = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path $proof) { throw "Use a fresh output directory to avoid stale test evidence: $proof" }
New-Item -ItemType Directory -Path $proof | Out-Null
$reservations = @()
$processes = @()
try {
    $gestureProof = Join-Path $proof 'gestures'
    $gesture = Start-Process -FilePath $appPath -ArgumentList @('--interaction-smoke-test', "`"$gestureProof`"") -PassThru
    $processes += $gesture
    if (!$gesture.WaitForExit(30000)) { throw 'Mouse interaction smoke timed out.' }
    Get-Content (Join-Path $gestureProof 'result.txt')
    if ($gesture.ExitCode -ne 0) { throw 'Mouse interaction smoke failed.' }

    # Exercise the production receiver port plus one distinct local test port.
    foreach ($port in @(8768, 0)) {
        $socket = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, $port)
        $socket.Start()
        $reservations += $socket
    }
    $alicePort = $reservations[0].LocalEndpoint.Port
    $bobPort = $reservations[1].LocalEndpoint.Port
    foreach ($socket in $reservations) { $socket.Stop() }
    $processes += Start-Process -FilePath $appPath -ArgumentList @('--messaging-smoke-test', "`"$proof`"", 'alice', $alicePort, $bobPort) -PassThru
    $processes += Start-Process -FilePath $appPath -ArgumentList @('--messaging-smoke-test', "`"$proof`"", 'bob', $bobPort, $alicePort) -PassThru
    foreach ($process in $processes) {
        if (!$process.WaitForExit(60000)) { throw 'Messaging smoke timed out.' }
    }
    foreach ($role in @('alice', 'bob')) {
        $result = Join-Path $proof "$role-result.txt"
        if (!(Test-Path $result)) { throw "$role did not produce a messaging report." }
        Get-Content $result
        if (!(Get-Content $result -Raw).StartsWith('PASS:')) { throw "$role failed messaging verification." }
    }
    foreach ($process in $processes) {
        if ($process.ExitCode -ne 0) { throw "Messaging app exited with $($process.ExitCode)." }
    }
} finally {
    foreach ($socket in $reservations) { $socket.Stop() }
    foreach ($process in $processes) {
        if (!$process.HasExited) { $process.Kill($true); $process.WaitForExit() }
    }
}
