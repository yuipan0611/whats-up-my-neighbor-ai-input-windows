# External MSAA/UIA check against a visible HUD hosted by a synthetic-data process.
# Uses only built-in components (oleacc.dll, UIAutomation, PowerShell). No audio, no API.
# Run it only with no keyboard or mouse input during the run: the focus checks depend on it.
param([string]$Exe = (Join-Path $PSScriptRoot 'bin\Release\a11y-verification.exe'))
$ErrorActionPreference = 'Stop'
$dir = Join-Path $env:TEMP ('a11y-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $dir | Out-Null
$results = New-Object System.Collections.Generic.List[string]
function Record([string]$name, [bool]$ok, [string]$detail = '') {
    $results.Add(('{0} {1} {2}' -f ($(if ($ok) { 'PASS' } else { 'FAIL' })), $name, $detail))
}

Add-Type -TypeDefinition @'
using System;
using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
public static class Win32 {
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll")] static extern bool AllowSetForegroundWindow(uint processId);
    public static long Foreground() { return GetForegroundWindow().ToInt64(); }
    public static int[] Rect(long hwnd) { RECT r; GetWindowRect(new IntPtr(hwnd), out r); return new int[] { r.Left, r.Top, r.Right, r.Bottom }; }
    public static bool Exists(long hwnd) { return IsWindow(new IntPtr(hwnd)); }
    public static bool AllowSet(uint processId) { return AllowSetForegroundWindow(processId); }
}
public static class Msaa {
    [DllImport("oleacc.dll")]
    static extern int AccessibleObjectFromWindow(IntPtr hwnd, uint id, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object acc);
    public static object Get(IntPtr hwnd) {
        Guid iid = new Guid("618736E0-3C3D-11CF-810C-00AA00389B71");
        object acc; int hr = AccessibleObjectFromWindow(hwnd, unchecked((uint)-4), ref iid, out acc);
        if (hr != 0) throw new InvalidOperationException("AccessibleObjectFromWindow hr=0x" + hr.ToString("X8"));
        return acc;
    }
    public static int ChildCount(object acc) { return (int)acc.GetType().InvokeMember("accChildCount", BindingFlags.GetProperty, null, acc, null); }
    public static string Name(object acc, int child) { return (string)acc.GetType().InvokeMember("accName", BindingFlags.GetProperty, null, acc, new object[] { child }); }
    public static int Role(object acc, int child) { return (int)acc.GetType().InvokeMember("accRole", BindingFlags.GetProperty, null, acc, new object[] { child }); }
    public static int State(object acc, int child) { return (int)acc.GetType().InvokeMember("accState", BindingFlags.GetProperty, null, acc, new object[] { child }); }
    public static void DoDefault(object acc, int child) { acc.GetType().InvokeMember("accDoDefaultAction", BindingFlags.InvokeMethod, null, acc, new object[] { child }); }
    public static int[] Location(object acc, int child) {
        object[] args = new object[] { 0, 0, 0, 0, child };
        ParameterModifier mod = new ParameterModifier(5);
        mod[0] = true; mod[1] = true; mod[2] = true; mod[3] = true; mod[4] = false;
        acc.GetType().InvokeMember("accLocation", BindingFlags.InvokeMethod, null, acc, args, new ParameterModifier[] { mod }, null, null);
        return new int[] { (int)args[0], (int)args[1], (int)args[2], (int)args[3] };
    }
}
public static class WinEventWatch {
    public delegate void Proc(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);
    [DllImport("user32.dll")] static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr mod, Proc proc, uint pid, uint tid, uint flags);
    [DllImport("user32.dll")] static extern bool UnhookWinEvent(IntPtr hook);
    [DllImport("user32.dll")] static extern bool PeekMessage(out MSG msg, IntPtr hwnd, uint min, uint max, uint remove);
    [DllImport("user32.dll")] static extern bool TranslateMessage(ref MSG msg);
    [DllImport("user32.dll")] static extern IntPtr DispatchMessage(ref MSG msg);
    [StructLayout(LayoutKind.Sequential)] public struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam; public IntPtr lParam; public uint time; public int x; public int y; }
    public static readonly ConcurrentQueue<string> Events = new ConcurrentQueue<string>();
    static Proc keep; static volatile bool stop;
    public static void Start(uint processId) {
        stop = false;
        Thread worker = new Thread(() => {
            keep = (h, e, hw, o, c, t, tm) => Events.Enqueue(e.ToString("X4") + " " + hw.ToInt64() + " " + o + " " + c);
            IntPtr hook = SetWinEventHook(0x800B, 0x800C, IntPtr.Zero, keep, processId, 0, 0);
            MSG m;
            while (!stop) {
                while (PeekMessage(out m, IntPtr.Zero, 0, 0, 1)) { TranslateMessage(ref m); DispatchMessage(ref m); }
                Thread.Sleep(10);
            }
            UnhookWinEvent(hook);
        });
        worker.IsBackground = true;
        worker.Start();
    }
    public static void Stop() { stop = true; }
}
'@

$hostProc = $null
try {
    $hostProc = Start-Process -FilePath $Exe -ArgumentList $dir -PassThru
    $events = Join-Path $dir 'events.log'
    $deadline = (Get-Date).AddSeconds(20)
    while (-not ((Test-Path $events) -and (Select-String -Path $events -Pattern 'READY' -Quiet)) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 200 }
    if (-not (Select-String -Path $events -Pattern 'READY' -Quiet)) { throw 'host did not report READY' }
    $hwnd = [IntPtr][long](Get-Content (Join-Path $dir 'hud.hwnd'))
    [WinEventWatch]::Start([uint32]$hostProc.Id)

    function EventLines { if (Test-Path $events) { @(Get-Content $events | Where-Object { $_ -match ' EVENT ' }) } else { @() } }
    function EventCount { (EventLines).Count }
    function Send([string]$command) {
        Set-Content -Path (Join-Path $dir 'cmd.txt') -Value $command -NoNewline
        $wait = (Get-Date).AddSeconds(5)
        while ((Test-Path (Join-Path $dir 'cmd.txt')) -and (Get-Date) -lt $wait) { Start-Sleep -Milliseconds 50 }
        Start-Sleep -Milliseconds 300
    }
    # Exactly one new event, and it must be the expected kind (not just any event).
    function ExpectOneEvent([int]$before, [string]$kind) {
        $lines = @(EventLines)
        $new = @($lines | Select-Object -Skip $before)
        return ($new.Count -eq 1) -and ($new[0] -match ('EVENT ' + $kind + '$'))
    }

    $acc = [Msaa]::Get($hwnd)
    Record 'MSAA: client exposes two children' ([Msaa]::ChildCount($acc) -eq 2) ([string][Msaa]::ChildCount($acc))
    Record 'MSAA: left child role is PushButton (43)' ([Msaa]::Role($acc, 1) -eq 43)
    Record 'MSAA: right child role is PushButton (43)' ([Msaa]::Role($acc, 2) -eq 43)

    Record 'Idle: right name is start' ([Msaa]::Name($acc, 2) -eq '開始錄音') ([Msaa]::Name($acc, 2))
    $b = EventCount; [Msaa]::DoDefault($acc, 2); Start-Sleep -Milliseconds 300
    Record 'Idle: right default action raises exactly one Start' (ExpectOneEvent $b 'Start')

    Send 'state Recording'
    Record 'Recording: right name is finish' ([Msaa]::Name($acc, 2) -eq '完成錄音')
    $b = EventCount; [Msaa]::DoDefault($acc, 1); Start-Sleep -Milliseconds 300
    Record 'Recording: left default action raises exactly one Cancel' (ExpectOneEvent $b 'Cancel')
    $b = EventCount; [Msaa]::DoDefault($acc, 2); Start-Sleep -Milliseconds 300
    Record 'Recording: right default action raises exactly one Finish' (ExpectOneEvent $b 'Finish')

    Send 'state RetainedAudio'
    Record 'RetainedAudio: right name is retry' ([Msaa]::Name($acc, 2) -eq '重試辨識')
    $b = EventCount; [Msaa]::DoDefault($acc, 2); Start-Sleep -Milliseconds 300
    Record 'RetainedAudio: right default action raises exactly one Retry' (ExpectOneEvent $b 'Retry')

    # Unavailable is STATE_SYSTEM_UNAVAILABLE = 0x1 (0x8000 would be Invisible). Raw values are reported too.
    Send 'state Delivering'
    $leftState = [Msaa]::State($acc, 1); $rightState = [Msaa]::State($acc, 2)
    $results.Add("INFO raw accState while Delivering: left=0x$($leftState.ToString('X')) right=0x$($rightState.ToString('X'))")
    Record 'Delivering: left is unavailable (0x1)' (($leftState -band 0x1) -ne 0)
    Record 'Delivering: right is unavailable (0x1)' (($rightState -band 0x1) -ne 0)
    $b = EventCount; [Msaa]::DoDefault($acc, 1); [Msaa]::DoDefault($acc, 2); Start-Sleep -Milliseconds 300
    Record 'Delivering: neither eye raises an event' ((EventCount) - $b -eq 0)

    Send 'state Idle'
    Send 'hide'
    $b = EventCount; [Msaa]::DoDefault($acc, 2); Start-Sleep -Milliseconds 300
    Record 'Hidden: default action raises nothing' ((EventCount) - $b -eq 0)
    Send 'show'

    $windowBefore = [Win32]::Rect($hwnd.ToInt64()); $eyeBefore = [Msaa]::Location($acc, 2)
    Send 'move 700 350'; Start-Sleep -Milliseconds 300
    $windowAfter = [Win32]::Rect($hwnd.ToInt64()); $eyeAfter = [Msaa]::Location($acc, 2)
    $windowDelta = @(($windowAfter[0] - $windowBefore[0]), ($windowAfter[1] - $windowBefore[1]))
    $eyeDelta = @(($eyeAfter[0] - $eyeBefore[0]), ($eyeAfter[1] - $eyeBefore[1]))
    Record 'Move: window reaches the requested position (700, 350)' (($windowAfter[0] -eq 700) -and ($windowAfter[1] -eq 350)) ("left=$($windowAfter[0]) top=$($windowAfter[1])")
    Record 'Move: right eye location moves exactly with the window' (($eyeDelta[0] -eq $windowDelta[0]) -and ($eyeDelta[1] -eq $windowDelta[1])) ("window=$windowDelta eye=$eyeDelta")

    $locEvents = @([WinEventWatch]::Events.ToArray() | Where-Object { $_ -like ('800B ' + $hwnd.ToInt64() + ' *') })
    Record 'Move: LocationChange notification reached the client' ($locEvents.Count -gt 0) ("count=$($locEvents.Count)")
    $pairs = @($locEvents | ForEach-Object { ($_ -split ' ')[2..3] -join ',' } | Sort-Object -Unique)
    $results.Add('INFO LocationChange (idObject,idChild) pairs observed: ' + ($pairs -join ' | '))
    $nameEvents = @([WinEventWatch]::Events.ToArray() | Where-Object { $_ -like ('800C ' + $hwnd.ToInt64() + ' *') })
    Record 'State change: NameChange notification reached the client' ($nameEvents.Count -gt 0) ("count=$($nameEvents.Count)")

    # Focus: the input box must be foreground and focused before, and the eye action must not change either.
    function FocusLine { $l = @(Select-String -Path $events -Pattern 'FOCUS' | Select-Object -Last 1); if ($l) { $l.Line } else { '' } }
    function FocusOk([string]$line) { return ($line -match 'inputIsForeground=True') -and ($line -match 'boxFocused=True') }
    [Win32]::AllowSet([uint32]$hostProc.Id) | Out-Null
    Send 'activate'; Send 'focus'
    $pre = FocusLine
    Record 'Focus precondition: input box is foreground and focused before the eye actions' (FocusOk $pre) $pre
    $pairsOk = $true; $pairDetail = @()
    foreach ($step in @(@('left', 1), @('right', 2))) {
        Send 'activate'; Send 'focus'; $beforeLine = FocusLine
        [Msaa]::DoDefault($acc, $step[1]); Start-Sleep -Milliseconds 300
        Send 'focus'; $afterLine = FocusLine
        $ok = (FocusOk $beforeLine) -and (FocusOk $afterLine)
        if (-not $ok) { $pairsOk = $false }
        $pairDetail += ($step[0] + ' ok=' + $ok)
    }
    Record 'Focus: each eye action keeps the input as foreground and focused (before and after)' $pairsOk ($pairDetail -join '; ')

    # UIA: record what the bridge exposes. TryGetCurrentPattern avoids failing the whole query.
    try {
        Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
        $root = [System.Windows.Automation.AutomationElement]::FromHandle($hwnd)
        $buttons = @($root.FindAll([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button))))
        $names = @($buttons | ForEach-Object { $_.Current.Name })
        $invokable = 0
        foreach ($button in $buttons) {
            $pattern = $null
            if ($button.TryGetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern, [ref]$pattern)) { $invokable++ }
        }
        $results.Add('INFO UIA button elements through the bridge: ' + $buttons.Count + ' names=' + ($names -join ' | '))
        $results.Add("INFO UIA buttons with InvokePattern: $invokable")
        if ($buttons.Count -eq 0) { $results.Add('NOT SUPPORTED UIA: no button elements exposed through the bridge') }
        elseif ($invokable -eq 0) { $results.Add('NOT SUPPORTED UIA Invoke: no element exposes InvokePattern') }
    } catch { $results.Add('INFO UIA query error: ' + $_.Exception.Message) }

    # Close: the HUD window must be destroyed. Event silence after close is a weak check only.
    Send 'close'; Start-Sleep -Milliseconds 800
    Record 'Closed: HUD window is destroyed' (-not [Win32]::Exists($hwnd.ToInt64()))
    $b = EventCount
    try { [Msaa]::DoDefault($acc, 2) } catch { }
    Start-Sleep -Milliseconds 300
    Record 'Closed: no event after close (weak check; in-process dispose behaviour is covered by unit tests)' ((EventCount) - $b -eq 0)
}
finally {
    [WinEventWatch]::Stop()
    if ($hostProc -and -not $hostProc.HasExited) { Stop-Process -Id $hostProc.Id -Force -ErrorAction SilentlyContinue; $results.Add('INFO host process was stopped by cleanup') }
    $results | Set-Content -Path (Join-Path $dir 'results.txt') -Encoding UTF8
}
$results
"artifacts: $dir"
