; AppVersion and NumericVersion (MAJOR.MINOR.PATCH) come from the VERSION file via
; scripts/package.ps1, so the installer name, uninstall entry and file version match.
#ifndef AppVersion
  #error AppVersion is required; run scripts/package.ps1 so it reads the VERSION file
#endif
#ifndef NumericVersion
  #error NumericVersion is required; run scripts/package.ps1 so it reads the VERSION file
#endif
[Setup]
AppId={{B747FDE2-3340-45DA-A662-90FF930904EA}
AppName=What's up my neighbor
AppVersion={#AppVersion}
; Original author credit is kept in AppPublisher and LICENSE; the URLs point to the upstream Windows repository.
AppPublisher=YUI
AppPublisherURL=https://github.com/yuipan0611/whats-up-my-neighbor-ai-input-windows
AppSupportURL=https://github.com/yuipan0611/whats-up-my-neighbor-ai-input-windows/issues
AppUpdatesURL=https://github.com/yuipan0611/whats-up-my-neighbor-ai-input-windows/releases
DefaultDirName={localappdata}\Programs\WhatsUpMyNeighbor
DefaultGroupName=What's up my neighbor
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64os
ArchitecturesInstallIn64BitMode=x64os
MinVersion=10.0.19045
OutputDir=..\dist
OutputBaseFilename=Neighbor-Setup-{#AppVersion}-x64
SetupIconFile=..\assets\neighbor.ico
UninstallDisplayIcon={app}\Neighbor.exe
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
ShowLanguageDialog=no
CloseApplications=yes
RestartApplications=no
LicenseFile=..\LICENSE
VersionInfoVersion={#NumericVersion}.0
VersionInfoDescription=What's up my neighbor Windows 安裝程式

[Languages]
Name: "traditionalchinese"; MessagesFile: "ChineseTraditional.isl"

[Tasks]
Name: "desktopicon"; Description: "建立桌面捷徑"; Flags: unchecked

[Files]
Source: "..\dist\app\*"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{userprograms}\What's up my neighbor"; Filename: "{app}\Neighbor.exe"
Name: "{userdesktop}\What's up my neighbor"; Filename: "{app}\Neighbor.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\Neighbor.exe"; Description: "啟動 What's up my neighbor"; Flags: nowait postinstall skipifsilent

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueName: "WhatsUpMyNeighbor"; Flags: uninsdeletevalue

[Code]
function InitializeSetup(): Boolean;
var Release: Cardinal;
begin
  Result := RegQueryDWordValue(HKLM, 'SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full', 'Release', Release) and (Release >= 528040);
  if not Result then
    MsgBox('這個程式需要 Windows 10 22H2 與 Windows 11 內建的 .NET Framework 4.8。請從 Windows 設定啟用或修復此元件。安裝程式不會下載額外的執行環境。', mbError, MB_OK);
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usPostUninstall then
    DelTree(ExpandConstant('{localappdata}\WhatsUpMyNeighbor\Recordings'), True, True, True);
end;
