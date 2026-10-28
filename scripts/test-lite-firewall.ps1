param([Parameter(Mandatory=$true)][string]$Setup,
      [Parameter(Mandatory=$true)][string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
# Real-rule tests only on a disposable GitHub Windows runner.
if ($env:GITHUB_ACTIONS -ne 'true') { throw 'Run firewall smoke tests only on an isolated GitHub Actions runner.' }
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
if (!([Security.Principal.WindowsPrincipal]$identity).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Firewall CI requires an elevated runner.'
}
$setupPath = (Resolve-Path $Setup).Path
$proof = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path $proof) { throw 'Use a fresh firewall evidence directory.' }
New-Item -ItemType Directory -Path $proof | Out-Null
$target = Join-Path $proof 'NekoChat.Windows.exe'
[IO.File]::WriteAllText($target, 'firewall-test-target-only-not-executable')
$other = Join-Path $proof 'other.exe'
[IO.File]::WriteAllText($other, 'unrelated-test-target')
$policy = New-Object -ComObject HNetCfg.FwPolicy2
$defaultName = 'nekochat.windows.exe'
$customName = 'NekoChat smoke custom ' + [Guid]::NewGuid().ToString('N')
$otherName = 'NekoChat smoke unrelated ' + [Guid]::NewGuid().ToString('N')
$ownedName = $null
if (@($policy.Rules | Where-Object Name -eq $defaultName).Count) { throw 'Do not disturb pre-existing app-name rules.' }
function Assert([bool]$condition, [string]$message) { if (!$condition) { throw $message } }
function Add-Block([string]$name, [string]$program, [int]$profiles = 4) {
    $r = New-Object -ComObject HNetCfg.FWRule
    $r.Name=$name; $r.ApplicationName=$program; $r.Protocol=6
    $r.LocalPorts='*'; $r.Direction=1; $r.Profiles=$profiles; $r.Action=0; $r.Enabled=$true
    $policy.Rules.Add($r)
}
function Run-Helper {
    $process = Start-Process -FilePath $setupPath -WindowStyle Hidden -ArgumentList @('--configure-firewall', ('"{0}"' -f $target)) -Wait -PassThru
    return $process.ExitCode
}
try {
    Add-Block $defaultName $target
    Add-Block $otherName $other
    Assert ((Run-Helper) -eq 0) 'Firewall helper failed.'
    $default = @($policy.Rules | Where-Object Name -eq $defaultName)
    Assert ($default.Count -eq 1 -and !$default[0].Enabled) 'Default TCP block remains enabled.'
    $owned = @($policy.Rules | Where-Object { $_.ApplicationName -ieq $target -and $_.Name -like 'NekoChat.LAN.TCP8768.*' })
    Assert ($owned.Count -eq 1) 'Expected exactly one managed allow rule.'
    $ownedName = $owned[0].Name
    $allow = $owned[0]
    Assert ($allow.Enabled -and $allow.Action -eq 1 -and $allow.Direction -eq 1 -and
        $allow.Protocol -eq 6 -and $allow.Profiles -eq 6 -and $allow.LocalPorts -eq '8768' -and
        $allow.RemoteAddresses -eq 'LocalSubnet') 'Allow rule is not restricted to local-subnet chat.'
    Assert ((@($policy.Rules | Where-Object Name -eq $otherName))[0].Enabled) 'Changed another program rule.'
    Assert ((Run-Helper) -eq 0) 'Repeat firewall setup failed.'
    Assert (@($policy.Rules | Where-Object Name -eq $ownedName).Count -eq 1) 'Repeat setup duplicated the allow rule.'

    $policy.Rules.Remove($defaultName)
    Add-Block $defaultName $target 5
    Assert ((Run-Helper) -eq 0) 'Mixed-profile setup failed.'
    $mixed = (@($policy.Rules | Where-Object Name -eq $defaultName))[0]
    Assert ($mixed.Enabled -and $mixed.Profiles -eq 1) 'Domain block was not preserved.'

    $policy.Rules.Remove($defaultName)
    $policy.Rules.Remove($ownedName)
    Add-Block $defaultName $target
    Add-Block $customName $target
    Assert ((Run-Helper) -ne 0) 'Custom block was incorrectly treated as successful setup.'
    Assert ((@($policy.Rules | Where-Object Name -eq $defaultName))[0].Enabled) 'Default block not restored on failure.'
    Assert ((@($policy.Rules | Where-Object Name -eq $customName))[0].Enabled) 'Custom rule was modified.'
    Assert (@($policy.Rules | Where-Object Name -eq $ownedName).Count -eq 0) 'Failed setup left an allow rule behind.'
    [IO.File]::WriteAllText((Join-Path $proof 'result.txt'),
        'PASS: actual helper and Windows firewall; local-subnet TCP 8768 scope; old auto-block repair; unrelated program preservation; idempotency; Domain preservation; custom-policy failure and rollback. UAC acceptance/cancellation and physical classroom network remain manual checks.')
    Get-Content (Join-Path $proof 'result.txt')
} finally {
    foreach ($name in @($defaultName, $customName, $otherName, $ownedName)) {
        if ($name -and @($policy.Rules | Where-Object Name -eq $name).Count) { $policy.Rules.Remove($name) }
    }
}
