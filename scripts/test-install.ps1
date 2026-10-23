param([string]$Version)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
if (!$Version) { $Version = (Get-Content "$root/VERSION" -Raw).Trim() }
$installer = "$root/dist/Neighbor-Setup-$Version-x64.exe"
$target = Join-Path $env:LOCALAPPDATA 'Programs/WhatsUpMyNeighbor-CI'
$baselineVersion = '0.0.9-ci'
$testId = [Guid]::NewGuid().ToString('N')
$baselineDirectory = Join-Path $env:TEMP "Neighbor-InstallTest-$testId"
$baselineInstaller = Join-Path $baselineDirectory 'Neighbor-CI-Baseline.exe'
$userDataRoot = Join-Path $env:LOCALAPPDATA 'WhatsUpMyNeighbor'
$userData = Join-Path $userDataRoot "InstallTest-$testId"
$uninstallKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\{B747FDE2-3340-45DA-A662-90FF930904EA}_is1'
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$evidence = [System.Collections.Generic.List[string]]::new()
$payload = @(Get-ChildItem "$root/dist/app" -File -Recurse)
$installedByTest = $false
$createdUserDataRoot = $false
function Invoke-Checked([string]$File,[string]$Arguments) {
    $p = Start-Process -FilePath $File -ArgumentList $Arguments -Wait -PassThru
    if ($p.ExitCode -ne 0) { throw "$File failed with exit code $($p.ExitCode)" }
}
function Assert-NoStartup {
    $values = Get-ItemProperty -LiteralPath $runKey -ErrorAction SilentlyContinue
    if ($values -and $values.PSObject.Properties.Name -contains 'WhatsUpMyNeighbor') {
        throw 'Installation or smoke launch unexpectedly enabled login startup'
    }
}
function Assert-Installed([string]$ExpectedVersion) {
    if (!(Test-Path "$target/Neighbor.exe")) { throw 'Installed executable missing' }
    $registration = Get-ItemProperty -LiteralPath $uninstallKey -ErrorAction Stop
    if ($registration.DisplayVersion -ne $ExpectedVersion) {
        throw "Expected uninstall DisplayVersion $ExpectedVersion, got $($registration.DisplayVersion)"
    }
    if ([IO.Path]::GetFullPath($registration.InstallLocation).TrimEnd('\') -ne [IO.Path]::GetFullPath($target).TrimEnd('\')) {
        throw 'Uninstall registration points to an unexpected installation directory'
    }
    foreach ($file in $payload) {
        $relative = [IO.Path]::GetRelativePath("$root/dist/app", $file.FullName)
        $installedFile = Join-Path $target $relative
        if (!(Test-Path $installedFile -PathType Leaf)) { throw "Installed payload missing: $relative" }
        if ((Get-FileHash $installedFile -Algorithm SHA256).Hash -ne (Get-FileHash $file.FullName -Algorithm SHA256).Hash) {
            throw "Installed payload hash mismatch: $relative"
        }
    }
    Assert-NoStartup
}
function Assert-UserData {
    foreach ($file in $userDataHashes.Keys) {
        if (!(Test-Path $file -PathType Leaf) -or (Get-FileHash $file -Algorithm SHA256).Hash -ne $userDataHashes[$file]) {
            throw 'Installation, upgrade, or removal changed the isolated synthetic user data'
        }
    }
}

# Refuse to replace an existing user's installation. CI uses its disposable
# Windows account; all synthetic user data has a unique test-owned directory.
if (Test-Path $uninstallKey) { throw 'Existing Neighbor installation detected; use a clean disposable Windows test account' }
if (Test-Path $target) { throw 'CI installation directory already exists; refusing to overwrite unrelated files' }
if (Test-Path $userDataRoot) { throw 'Existing Neighbor user data detected; use a clean disposable Windows test account' }
if (!(Test-Path $installer) -or $payload.Count -eq 0) { throw 'Current installer and packaged payload are required' }
if ($Version -eq $baselineVersion) { throw 'Current version must differ from the synthetic baseline version' }
Assert-NoStartup
try {
    New-Item -ItemType Directory -Force $baselineDirectory | Out-Null
    $compiler = @(
        Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6/ISCC.exe'
        Join-Path $env:LOCALAPPDATA 'Programs/Inno Setup 6/ISCC.exe'
    ) | Where-Object { Test-Path $_ } | Select-Object -First 1
    if (!$compiler) { $compiler = (Get-Command ISCC.exe -ErrorAction Stop).Source }
    # Same AppId and payload, different AppVersion: exercises installer upgrade
    # and registry behavior, not migration from a historical released binary.
    & $compiler "/DAppVersion=$baselineVersion" '/DNumericVersion=0.0.9' "/O$baselineDirectory" '/FNeighbor-CI-Baseline' "$root/packaging/neighbor.iss"
    if ($LASTEXITCODE -ne 0 -or !(Test-Path $baselineInstaller)) { throw 'Synthetic prior-version installer compilation failed' }
    $evidence.Add("BASELINE: synthetic prior installer $baselineVersion, same AppId and same payload as $Version; not a historical released binary.")
    $installedByTest = $true
    Invoke-Checked $baselineInstaller "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /DIR=`"$target`" /LOG=`"$root/dist/install-baseline.log`""
    Assert-Installed $baselineVersion
    $evidence.Add("PASS baseline install: HKCU uninstall DisplayVersion=$baselineVersion; all $($payload.Count) payload SHA-256 hashes match; login startup absent.")

    New-Item -ItemType Directory $userDataRoot | Out-Null
    $createdUserDataRoot = $true
    New-Item -ItemType Directory $userData | Out-Null
    '{"PrimaryLanguage":"zh","AdditionalLanguages":["en"],"Mode":0,"LaunchAtLogin":false}' | Set-Content (Join-Path $userData 'settings.json') -Encoding utf8
    '{"Version":1,"Entries":[{"Id":"ci-term","Term":"CI常用詞","Enabled":true,"Pinned":true}],"Candidates":[]}' | Set-Content (Join-Path $userData 'vocabulary.json') -Encoding utf8
    $userDataHashes = @{}
    Get-ChildItem $userData -File | ForEach-Object { $userDataHashes[$_.FullName] = (Get-FileHash $_.FullName -Algorithm SHA256).Hash }

    Invoke-Checked $installer "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /DIR=`"$target`" /LOG=`"$root/dist/install-upgrade.log`""
    Assert-Installed $Version
    Assert-UserData
    $evidence.Add("PASS version upgrade: HKCU uninstall DisplayVersion changed from $baselineVersion to $Version; all payload hashes match; isolated settings and vocabulary hashes unchanged.")
    $process = Start-Process "$target/Neighbor.exe" '--smoke-test' -PassThru
    if (!$process.WaitForExit(20000)) { $process.Kill(); throw 'App smoke test timed out' }
    if ($process.ExitCode -ne 0) { throw 'App launch smoke failed' }
    Assert-NoStartup
    Assert-UserData
    $evidence.Add('PASS upgraded executable smoke launch; login startup remains absent. Smoke mode uses synthetic fixtures and does not validate a real microphone or API account.')
    Invoke-Checked "$target/unins000.exe" "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /LOG=`"$root/dist/uninstall.log`""
    if (Test-Path "$target/Neighbor.exe") { throw 'Uninstall left executable' }
    if (Test-Path $uninstallKey) { throw 'Uninstall left the product registration' }
    Assert-NoStartup
    Assert-UserData
    $installedByTest = $false
    $evidence.Add('PASS uninstall: executable and HKCU uninstall registration removed; isolated synthetic settings and vocabulary preserved byte-for-byte.')
    $evidence.Add('Test cleanup removes only the unique synthetic data directory after preservation checks; no baseline installer is included in dist.')
} catch {
    $evidence.Add("FAIL: $($_.Exception.Message)")
    throw
} finally {
    $evidence | Set-Content "$root/dist/install-tests.txt" -Encoding utf8
    try {
        if ($installedByTest -and (Test-Path "$target/unins000.exe")) { Invoke-Checked "$target/unins000.exe" '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART' }
    } finally {
        if (Test-Path $baselineDirectory) { Remove-Item $baselineDirectory -Recurse -Force }
        if (Test-Path $userData) { Remove-Item $userData -Recurse -Force }
        if ($createdUserDataRoot -and (Test-Path $userDataRoot) -and @(Get-ChildItem $userDataRoot -Force).Count -eq 0) { Remove-Item $userDataRoot }
    }
}
