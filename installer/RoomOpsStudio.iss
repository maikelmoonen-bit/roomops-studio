; RoomOps Studio — per-user installer (Inno Setup 6). Built by build.ps1 when ISCC.exe is available.
; No administrator rights are required or requested.
#ifndef AppVersion
  #define AppVersion "1.0.1"
#endif
#ifndef SourceDir
  #define SourceDir "..\artifacts\publish\win-x64"
#endif

[Setup]
AppId={{7E6B5A61-2C3D-4F1E-9B7A-52D0C1A0B5E3}
AppName=RoomOps Studio
AppVersion={#AppVersion}
AppPublisher=RoomOps Studio
AppComments=Intelligent Workspace Administration for Microsoft 365 meeting rooms
DefaultDirName={localappdata}\Programs\RoomOps Studio
DefaultGroupName=RoomOps Studio
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
OutputBaseFilename=RoomOpsStudio-{#AppVersion}-setup
SetupIconFile=..\src\RoomOps.Desktop\Assets\roomops.ico
UninstallDisplayIcon={app}\RoomOpsStudio.exe
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Shortcuts:"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\RoomOps Studio"; Filename: "{app}\RoomOpsStudio.exe"
Name: "{autodesktop}\RoomOps Studio"; Filename: "{app}\RoomOpsStudio.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\RoomOpsStudio.exe"; Description: "Start RoomOps Studio"; Flags: nowait postinstall skipifsilent

[Code]
// WebView2 Evergreen Runtime client id (Microsoft documentation: "Detect if a WebView2 Runtime is already installed").
const WebView2Key = 'SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}';

function WebView2Installed: Boolean;
var Version: String;
begin
  Result := (RegQueryStringValue(HKLM, WebView2Key, 'pv', Version) or RegQueryStringValue(HKCU, 'Software\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}', 'pv', Version))
    and (Version <> '') and (Version <> '0.0.0.0');
end;

procedure CurPageChanged(CurPageID: Integer);
var ErrorCode: Integer;
begin
  if (CurPageID = wpFinished) and not WebView2Installed then
    if MsgBox('RoomOps Studio requires the Microsoft Edge WebView2 Runtime, which was not found. Open the Microsoft download page?', mbConfirmation, MB_YESNO) = IDYES then
      ShellExec('open', 'https://go.microsoft.com/fwlink/p/?LinkId=2124703', '', '', SW_SHOWNORMAL, ewNoWait, ErrorCode);
end;
