param([string]$Version)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
# The VERSION file is the single source of truth for app, installer and release names.
$versionFile = (Get-Content "$root/VERSION" -Raw).Trim()
if (!$Version) { $Version = $versionFile }
if ($Version -ne $versionFile) { throw "Requested version $Version does not match VERSION file ($versionFile)" }
$output = "$root/dist"
$app = "$output/app"
# Remove every artifact from a previous packaging run so a stale installer can never be published.
if (Test-Path $output) { Remove-Item "$output/*" -Recurse -Force }
New-Item -ItemType Directory -Force $app | Out-Null
$build = "$root/Neighbor.Windows/bin/Release/net48"
# Explicit payload: CLR/Windows SDK contract metadata, test code and symbols are not runtime payload.
$runtimeFiles = @('Neighbor.exe', 'Neighbor.Core.dll', 'NAudio.Core.dll', 'NAudio.Wasapi.dll', 'Interop.UIAutomationClient.dll', 'Neighbor.exe.config')
foreach ($name in $runtimeFiles) {
    $source = Join-Path $build $name
    if (!(Test-Path $source -PathType Leaf)) { throw "Missing runtime payload: $name" }
    Copy-Item $source $app
}
Copy-Item "$root/使用說明.txt", "$root/THIRD-PARTY-NOTICES.txt", "$root/LICENSE" $app
if (!(Test-Path "$app/使用說明.txt" -PathType Leaf)) { throw 'User guide missing from installer payload' }
# The executable must report the same version that names the installer and manifest.
$productVersion = (Get-Item "$app/Neighbor.exe").VersionInfo.ProductVersion
# SDK builds append '+<commit>' to ProductVersion; compare only the part before it.
if (($productVersion -split '\+')[0] -ne $Version) { throw "Neighbor.exe ProductVersion '$productVersion' does not match $Version" }
$forbidden = Get-ChildItem $app -File | Where-Object { $_.Name -match 'Tests|\.pdb$|\.winmd$|\.wav$|\.m4a$|whisper|ffmpeg|python|torch' }
if ($forbidden) { throw 'Unexpected files in installer payload' }
$compiler = @(
    Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6/ISCC.exe'
    Join-Path $env:LOCALAPPDATA 'Programs/Inno Setup 6/ISCC.exe'
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (!$compiler) { $compiler = (Get-Command ISCC.exe -ErrorAction Stop).Source }
$numericVersion = $Version.Split('-')[0]
& $compiler "/DAppVersion=$Version" "/DNumericVersion=$numericVersion" "$root/packaging/neighbor.iss"
if ($LASTEXITCODE -ne 0) { throw 'Installer compilation failed' }
$installer = Get-Item "$output/Neighbor-Setup-$Version-x64.exe"
if ($installer.Length -gt 20000000) { throw "Installer exceeds 20 MB: $($installer.Length) bytes" }
$hash = (Get-FileHash $installer.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
"$hash  $($installer.Name)" | Set-Content "$($installer.FullName).sha256" -Encoding ascii
$sourceCommit = (git -C $root rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0) { throw 'Cannot identify source commit' }
$signature = (Get-AuthenticodeSignature $installer.FullName).Status.ToString()
$manifest = [ordered]@{ sourceCommit=$sourceCommit; workflowRun=$env:GITHUB_RUN_ID; signatureStatus=$signature; version=$Version; installer=$installer.Name; bytes=$installer.Length; sha256=$hash; signed=($signature -eq 'Valid'); target='Windows 11 x64; Windows 10 22H2 x64'; files=@(Get-ChildItem $app -File | ForEach-Object { @{name=$_.Name;bytes=$_.Length;sha256=(Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()} }) }
$manifest | ConvertTo-Json -Depth 5 | Set-Content "$output/build-manifest.json" -Encoding utf8
Write-Output "Installer size: $($installer.Length) bytes; SHA-256: $hash"
