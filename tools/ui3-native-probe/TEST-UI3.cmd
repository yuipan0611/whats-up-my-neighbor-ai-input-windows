@echo off
setlocal
set "UI3_QA_ROOT=C:\NekoChatUI3QA20261105"
set "UI3_CSC_DIR=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319"
if not exist "%UI3_CSC_DIR%\csc.exe" set "UI3_CSC_DIR=%WINDIR%\Microsoft.NET\Framework\v4.0.30319"
if not exist "%UI3_CSC_DIR%\csc.exe" (
  echo Windows C# compiler was not found. No tests were run.
  pause
  exit /b 2
)
if not exist "%UI3_QA_ROOT%\Tools" mkdir "%UI3_QA_ROOT%\Tools"
echo Building the isolated UI-3 test runner...
"%UI3_CSC_DIR%\csc.exe" /nologo /codepage:65001 /target:exe /out:"%UI3_QA_ROOT%\Tools\UI3.NativeProbe.exe" /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:"%UI3_CSC_DIR%\WPF\UIAutomationClient.dll" /r:"%UI3_CSC_DIR%\WPF\UIAutomationTypes.dll" /r:"%UI3_CSC_DIR%\WPF\WindowsBase.dll" "%~dp0NativeProbe.cs" > "%UI3_QA_ROOT%\Tools\compile.log" 2>&1
set "UI3_COMPILE_RESULT=%ERRORLEVEL%"
type "%UI3_QA_ROOT%\Tools\compile.log"
if not "%UI3_COMPILE_RESULT%"=="0" (
  echo The runner did not compile. No product tests were run.
  call :exportlogs
  pause
  exit /b 2
)
echo Testing UI-3 with an isolated profile. Please leave the test windows in front.
echo No classroom messages are sent. No firewall changes or installation are performed.
"%UI3_QA_ROOT%\Tools\UI3.NativeProbe.exe" "%~dp0App\NekoChat.Windows.exe" "%UI3_QA_ROOT%\Profiles" --shared-evidence > "%UI3_QA_ROOT%\Tools\native-run.log" 2>&1
set "UI3_RESULT=%ERRORLEVEL%"
type "%UI3_QA_ROOT%\Tools\native-run.log"
call :exportlogs
echo Native test runner finished with exit code %UI3_RESULT%.
pause
exit /b %UI3_RESULT%

:exportlogs
powershell.exe -NoProfile -Command "$dest = '\\Mac\NekoChat-' + [char]0x56de + [char]0x50b3 + '\UI3-native-20261105'; New-Item -ItemType Directory -Path $dest -Force | Out-Null; Get-ChildItem -LiteralPath 'C:\NekoChatUI3QA20261105\Tools' -Filter '*.log' | Copy-Item -Destination $dest -Force"
exit /b 0
