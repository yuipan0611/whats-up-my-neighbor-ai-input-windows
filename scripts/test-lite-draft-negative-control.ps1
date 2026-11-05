param([Parameter(Mandatory = $true)][string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$output = [IO.Path]::GetFullPath($OutputDirectory)
$build = [IO.Path]::GetFullPath((Join-Path $repo 'build')) + [IO.Path]::DirectorySeparatorChar
if (!$output.StartsWith($build, [StringComparison]::OrdinalIgnoreCase)) { throw 'Negative control output must be inside this checkout build directory.' }
New-Item -ItemType Directory -Force $output | Out-Null
$candidate = Join-Path $output ('candidate-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory $candidate | Out-Null
$archive = Join-Path $candidate 'source.zip'
& git -C $repo archive --format=zip -o $archive HEAD -- windows-lite/NekoChat.Core windows-lite/NekoChat.Windows windows-lite/NekoChat.Animation windows-lite/THIRD-PARTY-LICENSES.txt Shared Examples/CatAppearance
if ($LASTEXITCODE -ne 0) { throw 'Could not archive the tracked negative-control source.' }
$source = Join-Path $candidate 'source'
Expand-Archive -Path $archive -DestinationPath $source
$inbox = Join-Path $source 'windows-lite/NekoChat.Windows/ChatWindow.Inbox.cs'
$text = [IO.File]::ReadAllText($inbox)
$wait = 'await _draftSaveGate.WaitAsync();'
$release = 'finally { _draftSaveGate.Release(); }'
if ([regex]::Matches($text, [regex]::Escape($wait)).Count -ne 1 -or [regex]::Matches($text, [regex]::Escape($release)).Count -ne 1) { throw 'Draft-save negative-control anchors changed.' }
# Only the private source copy loses the autosave/Send gate; the exit gate and original product stay intact.
[IO.File]::WriteAllText($inbox, $text.Replace($wait, 'await Task.CompletedTask;').Replace($release, 'finally { }'))
$binary = Join-Path $candidate 'bin'
& dotnet publish (Join-Path $source 'windows-lite/NekoChat.Windows') -c Release -r win-x64 --self-contained false -o $binary
if ($LASTEXITCODE -ne 0) { throw 'Negative control did not build; this is not valid red evidence.' }
$proof = Join-Path $candidate 'proof'
$probe = Start-Process -FilePath (Join-Path $binary 'NekoChat.Windows.exe') -ArgumentList @('--message-recovery-smoke-test', ('"{0}"' -f $proof)) -PassThru
if (!$probe.WaitForExit(65000)) { $probe.Kill($true); throw 'Negative control timed out; this is not the expected assertion failure.' }
$resultPath = Join-Path $proof 'message-recovery-result.json'
if (!(Test-Path $resultPath)) { throw 'Negative control produced no structured result.' }
$result = Get-Content $resultPath -Raw | ConvertFrom-Json
$expected = 'System.InvalidOperationException: Send waits for concurrent autosave completion without dropping the edit'
if ($probe.ExitCode -eq 0 -or $result.passed -or $result.checks.Count -ne 4 -or !$result.failure.Contains($expected)) { throw 'Negative control did not fail at the exact autosave/Send handoff assertion.' }
Copy-Item $resultPath (Join-Path $output 'negative-result.json')
@{ expectedFailureVerified = $true; exitCode = $probe.ExitCode; prerequisiteChecks = $result.checks.Count; assertion = $expected; originalProductModified = $false } |
    ConvertTo-Json | Set-Content (Join-Path $output 'negative-control.json') -Encoding utf8
Write-Output 'PASS: removing only the scratch autosave/Send gate is caught by the real SQLite/UI handoff assertion.'
