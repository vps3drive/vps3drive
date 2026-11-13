#ifndef MyAppVersion
  #define MyAppVersion "1.0.0"
#endif

[Setup]
AppId={{8F9C7E5B-6C4F-4B0B-AE6F-9E5C0B3D2E02}
AppName=VPS3Drive
AppVersion={#MyAppVersion}
AppPublisher=VPS3Drive Contributors
AppPublisherURL=https://github.com/vps3drive/vps3drive
DefaultDirName={autopf}\VPS3Drive
DefaultGroupName=VPS3Drive
DisableProgramGroupPage=yes
LicenseFile=..\LICENSE
OutputDir=..
OutputBaseFilename=VPS3Drive-Rust-Setup-v{#MyAppVersion}
SetupIconFile=..\assets\vps3drive.ico
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=admin

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "..\publish\win-x64\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "deps\winfsp.msi"; DestDir: "{tmp}"; Flags: deleteafterinstall; Check: not IsWinFspInstalled

[Icons]
Name: "{group}\VPS3Drive"; Filename: "{app}\vps3drive-rust.exe"
Name: "{group}\Uninstall VPS3Drive"; Filename: "{uninstallexe}"
Name: "{autodesktop}\VPS3Drive"; Filename: "{app}\vps3drive-rust.exe"; Tasks: desktopicon

[Run]
Filename: "msiexec.exe"; Parameters: "/i ""{tmp}\winfsp.msi"" /qn /norestart"; StatusMsg: "Installing WinFsp filesystem driver..."; Flags: waituntilterminated; Check: not IsWinFspInstalled
Filename: "{app}\vps3drive-rust.exe"; Description: "{cm:LaunchProgram,VPS3Drive}"; Flags: nowait postinstall skipifsilent

[Code]
function IsWinFspInstalled(): Boolean;
begin
  Result :=
    RegKeyExists(HKLM, 'SOFTWARE\WOW6432Node\WinFsp') or
    RegKeyExists(HKLM, 'SOFTWARE\WinFsp') or
    FileExists(ExpandConstant('{commonpf32}\WinFsp\bin\winfsp-x64.dll'));
end;
