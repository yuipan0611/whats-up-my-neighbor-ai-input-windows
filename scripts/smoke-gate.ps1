# Runs a launch smoke test as a build gate. The child process gets its own TEMP folder,
# so everything the app writes to TEMP lands in a directory this call created; only that
# directory is deleted, and only after its full path is verified.
$ErrorActionPreference = 'Stop'
$script:LastSmokeRoot = $null

function Invoke-SmokeGate {
    param(
        [Parameter(Mandatory = $true)][string]$Executable,
        [string]$Arguments = '',
        [int]$TimeoutSeconds = 30
    )
    if (!(Test-Path -LiteralPath $Executable -PathType Leaf)) { throw "Smoke executable not found: $Executable" }
    $systemTemp = [IO.Path]::GetTempPath().TrimEnd('\')
    $root = Join-Path $systemTemp ('neighbor-smoke-gate-' + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $root | Out-Null
    $script:LastSmokeRoot = $root
    $process = $null
    try
    {
        $startInfo = New-Object System.Diagnostics.ProcessStartInfo
        $startInfo.FileName = $Executable
        $startInfo.Arguments = $Arguments
        $startInfo.UseShellExecute = $false
        $startInfo.EnvironmentVariables['TEMP'] = $root
        $startInfo.EnvironmentVariables['TMP'] = $root
        $process = [Diagnostics.Process]::Start($startInfo)
        if (-not $process.WaitForExit($TimeoutSeconds * 1000))
        {
            $process.Kill()
            $process.WaitForExit(5000) | Out-Null
            throw "Smoke test timed out after $TimeoutSeconds seconds; process $($process.Id) was stopped."
        }
        if ($process.ExitCode -ne 0) { throw "Smoke test exited with code $($process.ExitCode)." }
    }
    finally
    {
        if ($process) { $process.Dispose() }
        # Delete only the folder created above, and only when its parent is the system temp folder.
        if ([IO.Path]::GetDirectoryName($root) -eq $systemTemp -and [IO.Path]::GetFileName($root) -like 'neighbor-smoke-gate-*')
        {
            Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
}

function Test-SmokeGate {
    # Negative checks: the gate must fail for a missing file, a non-zero exit and a timeout.
    # Each uses a harmless system command, so the product is never broken on purpose.
    $cmd = Join-Path ([Environment]::SystemDirectory) 'cmd.exe'
    $ping = Join-Path ([Environment]::SystemDirectory) 'ping.exe'
    $failures = @()

    try { Invoke-SmokeGate -Executable (Join-Path $env:TEMP 'no-such-neighbor.exe') -Arguments '--smoke-test' -TimeoutSeconds 5; $failures += 'missing file did not fail' }
    catch { if ($_.Exception.Message -notlike 'Smoke executable not found*') { $failures += "missing file: $($_.Exception.Message)" } }

    try { Invoke-SmokeGate -Executable $cmd -Arguments '/c exit 0' -TimeoutSeconds 10; if ($script:LastSmokeRoot -and (Test-Path -LiteralPath $script:LastSmokeRoot)) { $failures += 'zero exit left its temp folder' } }
    catch { $failures += "zero exit should pass: $($_.Exception.Message)" }

    try { Invoke-SmokeGate -Executable $cmd -Arguments '/c exit 3' -TimeoutSeconds 10; $failures += 'non-zero exit did not fail the gate' }
    catch { if ($_.Exception.Message -notlike '*exited with code 3*') { $failures += "non-zero exit message: $($_.Exception.Message)" } }
    if ($script:LastSmokeRoot -and (Test-Path -LiteralPath $script:LastSmokeRoot)) { $failures += 'non-zero exit left its temp folder' }

    try { Invoke-SmokeGate -Executable $ping -Arguments '-n 60 127.0.0.1' -TimeoutSeconds 2; $failures += 'timeout did not fail the gate' }
    catch { if ($_.Exception.Message -notlike '*timed out*') { $failures += "timeout message: $($_.Exception.Message)" } }
    if ($script:LastSmokeRoot -and (Test-Path -LiteralPath $script:LastSmokeRoot)) { $failures += 'timeout left its temp folder' }

    if ($failures.Count -gt 0) { throw 'Smoke gate self-test failed: ' + ($failures -join '; ') }
    Write-Output 'Smoke gate self-test passed: missing file, zero exit, non-zero exit and timeout behaved as expected.'
}
