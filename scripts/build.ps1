param([string]$Configuration = 'Release')
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
. "$PSScriptRoot/smoke-gate.ps1"
dotnet restore "$root/Neighbor.Tests/Neighbor.Tests.csproj" --locked-mode
if ($LASTEXITCODE -ne 0) { throw 'Dependency restore failed' }
dotnet build "$root/Neighbor.Tests/Neighbor.Tests.csproj" -c $Configuration --no-restore
if ($LASTEXITCODE -ne 0) { throw 'Windows build failed' }
& "$root/Neighbor.Tests/bin/$Configuration/net48/Neighbor.Tests.exe"
if ($LASTEXITCODE -ne 0) { throw 'Windows tests failed' }
Test-SmokeGate
# Launch gate: runs the real application entry point in smoke mode. This shows a short-lived HUD and companion window.
Invoke-SmokeGate -Executable "$root/Neighbor.Windows/bin/$Configuration/net48/Neighbor.exe" -Arguments '--smoke-test' -TimeoutSeconds 30
Write-Output 'Launch smoke gate passed.'
