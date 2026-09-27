; Installer for MHO Extended Mod Manager (Inno Setup 6). make_release.ps1 compiles it when Inno Setup is installed:
;   ISCC /DAppVersion=<ver> /DSourceDir=<staged app folder> /O<out> installer.iss
;
; Per-user install without admin rights, into %LOCALAPPDATA%\Programs\MHO Extended Mod Manager: the app keeps its
; mods and settings in a "data" folder next to the exe (it refuses to run where it can't write, e.g. Program Files),
; and updates itself in place. Uninstalling asks before deleting that data folder (default: keep it).

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef SourceDir
  #define SourceDir "publish"
#endif

[Setup]
AppId={{7C1E6B52-3A0B-4E7B-9C51-2F4D8B6E1A93}
AppName=MHO Extended Mod Manager
AppVersion={#AppVersion}
AppVerName=MHO Extended Mod Manager {#AppVersion}
AppPublisher=leeper48
AppPublisherURL=https://github.com/leeper48/MHO-UPK-Tools
AppSupportURL=https://github.com/leeper48/MHO-UPK-Tools/releases
DefaultDirName={localappdata}\Programs\MHO Extended Mod Manager
DefaultGroupName=MHO Extended Mod Manager
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputBaseFilename=MHO_Ext_ModManager-{#AppVersion}-Setup
; File metadata (shown in the file's Properties).
VersionInfoProductName=MHO Extended Mod Manager
VersionInfoDescription=MHO Extended Mod Manager Setup
VersionInfoCompany=leeper48
VersionInfoVersion={#AppVersion}
VersionInfoProductVersion={#AppVersion}
SetupIconFile=Assets\app.ico
UninstallDisplayIcon={app}\MHO_Ext_ModManager.exe
LicenseFile=..\LICENSE
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
RestartApplications=no

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Excludes: "data\*,*.pdb"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\MHO Extended Mod Manager"; Filename: "{app}\MHO_Ext_ModManager.exe"
Name: "{autodesktop}\MHO Extended Mod Manager"; Filename: "{app}\MHO_Ext_ModManager.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\MHO_Ext_ModManager.exe"; Description: "{cm:LaunchProgram,MHO Extended Mod Manager}"; Flags: nowait postinstall skipifsilent

[Code]
{ The app is framework-dependent: it needs the .NET 8 Desktop Runtime (Microsoft.WindowsDesktop.App 8.x). }
function HasDesktopRuntime8: Boolean;
var
  R: TFindRec;
begin
  Result := False;
  if FindFirst(ExpandConstant('{commonpf64}\dotnet\shared\Microsoft.WindowsDesktop.App\8.*'), R) then
  begin
    Result := True;
    FindClose(R);
  end;
end;

function InitializeSetup: Boolean;
var
  Code: Integer;
begin
  Result := True;
  if not HasDesktopRuntime8 then
  begin
    if MsgBox('MHO Extended Mod Manager needs Microsoft''s .NET 8 Desktop Runtime (x64), which isn''t installed.' + #13#10#13#10 +
              'Open the download page now? (Install "Windows Desktop Runtime 8 x64", then run this setup again.)' + #13#10#13#10 +
              'Yes: open the page and stop setup.   No: install anyway.', mbConfirmation, MB_YESNO) = IDYES then
    begin
      ShellExec('open', 'https://dotnet.microsoft.com/download/dotnet/8.0', '', '', SW_SHOWNORMAL, ewNoWait, Code);
      Result := False;
    end;
  end;
end;

{ Not Program Files (the app can't write its data there) and not the game's own folders. }
function NextButtonClick(CurPageID: Integer): Boolean;
var
  Dir: String;
begin
  Result := True;
  if CurPageID = wpSelectDir then
  begin
    Dir := Lowercase(AddBackslash(WizardDirValue));
    if (Pos(Lowercase(AddBackslash(ExpandConstant('{commonpf64}'))), Dir) = 1) or (Pos(Lowercase(AddBackslash(ExpandConstant('{commonpf32}'))), Dir) = 1) or
       (Pos('cookedpcconsole', Dir) > 0) or (Pos('\marvel heroes\', Dir) > 0) then
    begin
      MsgBox('Please choose a folder outside Program Files and outside the game''s folder: the manager keeps your mods and settings in a "data" folder next to itself and needs to write there.', mbError, MB_OK);
      Result := False;
    end;
  end;
end;

{ Uninstall: the data folder (mods library, settings, undo history) was made by the app, not by setup; ask before removing it. }
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Data: String;
begin
  if CurUninstallStep = usPostUninstall then
  begin
    Data := ExpandConstant('{app}\data');
    if DirExists(Data) then
      if MsgBox('Also delete your mods library, settings and undo history?' + #13#10#13#10 + Data + #13#10#13#10 +
                'No keeps them, so reinstalling picks them up again.', mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
        DelTree(Data, True, True, True);
  end;
end;
