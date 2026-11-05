# UI-3 native Windows probe

This runner tests the existing UI-3 product assembly (7e9f70c) without changing it. It starts the delivered executable with `--ui-test-data` on a fresh `C:\NekoChatUI3QA20261105\Profiles\run-*` profile and port 18769. It does not send messages, change firewall rules or install anything. It deliberately restarts only its own test process to verify nickname/draft persistence. Clipboard data is held in memory and restored at exit; it is never exported.

`TEST-UI3.cmd` uses Windows' existing .NET Framework C# compiler to build the probe. If the compiler is absent, it stops without claiming tests ran. The source is compatible with C# 5 and cross-built against the .NET Windows Desktop reference assemblies as a compile check; this does not prove the Windows .NET Framework compiler or native UI checks have passed.

The user starts the launcher once in the logged-in Windows desktop. UI Automation selects only the launched product process. Keyboard events are refused if another application holds foreground. Leave the test windows in front while running.

Output goes only to `\\Mac\NekoChat-回傳\UI3-native-20261105\run-*`: a unique run directory, product file hashes, result.txt, original Windows screenshot PNGs and their visible-control coordinate maps. A run is complete only when its result.txt exists and is inspected together with the hashes and screenshot contents. started.txt alone is not a live process handle or proof of success.

Coverage: startup/connectionId regression, private roster availability, teacher/student sticker scope, IP-only add dialog and invalid IP, local nickname, text clipboard/indentation, image clipboard draft, file picker draft, and restart persistence. No send button or reaction send action is invoked.

Not covered: drag/drop, installed IME, changed OS DPI, two Windows computers, and end-to-end delivery. Native test fixture execution is distinct from manual device acceptance.
