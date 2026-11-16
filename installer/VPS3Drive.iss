#ifndef MyAppVersion
  #define MyAppVersion "1.0.0"
#endif

[Setup]
AppId={{7E8B6F4A-5B3E-4A9A-9D5E-8F4B9A2C1D01}
AppName=VPS3Drive
AppVersion={#MyAppVersion}
AppPublisher=VPS3Drive Contributors
AppPublisherURL=https://github.com/vps3drive/vps3drive
DefaultDirName={autopf}\VPS3Drive
DefaultGroupName=VPS3Drive
DisableProgramGroupPage=yes
LicenseFile=..\LICENSE
OutputDir=..
OutputBaseFilename=VPS3Drive-Setup-v{#MyAppVersion}
SetupIconFile=..\src\VPS3Drive.App\Resources\vps3drive.ico
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
Name: "{group}\VPS3Drive"; Filename: "{app}\VPS3Drive.exe"
Name: "{group}\Uninstall VPS3Drive"; Filename: "{uninstallexe}"
Name: "{autodesktop}\VPS3Drive"; Filename: "{app}\VPS3Drive.exe"; Tasks: desktopicon

[Run]
Filename: "msiexec.exe"; Parameters: "/i ""{tmp}\winfsp.msi"" /qn /norestart"; StatusMsg: "Installing WinFsp filesystem driver..."; Flags: waituntilterminated; Check: not IsWinFspInstalled
Filename: "{app}\VPS3Drive.exe"; Description: "{cm:LaunchProgram,VPS3Drive}"; Flags: nowait postinstall skipifsilent

[Code]
function IsWinFspInstalled(): Boolean;
begin
  Result :=
    RegKeyExists(HKLM, 'SOFTWARE\WOW6432Node\WinFsp') or
    RegKeyExists(HKLM, 'SOFTWARE\WinFsp') or
    FileExists(ExpandConstant('{commonpf32}\WinFsp\bin\winfsp-x64.dll'));
end;

function IsDotNet8DesktopInstalled(): Boolean;
var
  FindRec: TFindRec;
  BaseDir: String;
begin
  Result := False;
  BaseDir := ExpandConstant('{commonpf64}\dotnet\shared\Microsoft.WindowsDesktop.App');
  if DirExists(BaseDir) then
  begin
    if FindFirst(BaseDir + '\8.*', FindRec) then
    begin
      try
        repeat
          if (FindRec.Attributes and FILE_ATTRIBUTE_DIRECTORY) <> 0 then
          begin
            Result := True;
            Break;
          end;
        until not FindNext(FindRec);
      finally
        FindClose(FindRec);
      end;
    end;
  end;
end;

function InitializeSetup(): Boolean;
var
  ErrorCode: Integer;
begin
  Result := True;
  if not IsDotNet8DesktopInstalled() then
  begin
    if MsgBox(
      'VPS3Drive requires .NET 8 Desktop Runtime (x64) to run.' + #13#10 + #13#10 +
      'It was not detected on this PC. Would you like to open the Microsoft download page now?' + #13#10 +
      '(You can still continue installing VPS3Drive and install .NET 8 Desktop Runtime afterwards.)',
      mbConfirmation, MB_YESNO) = IDYES then
    begin
      ShellExec('open', 'https://dotnet.microsoft.com/en-us/download/dotnet/8.0', '', '', SW_SHOWNORMAL, ewNoWait, ErrorCode);
    end;
  end;
end;
