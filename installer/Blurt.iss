; Builds the Blurt installer with Inno Setup 6 or 7:
;   iscc /DAppVersion=0.1.0 /DPublishDir=..\artifacts\publish installer\Blurt.iss
;
; Blurt stores its sounds and settings next to Blurt.exe, so it installs per user into
; %LocalAppData%\Programs\Blurt, where it can write without administrator rights.

#ifndef AppVersion
  #define AppVersion "0.1.0"
#endif
#ifndef PublishDir
  #define PublishDir "..\artifacts\publish"
#endif

[Setup]
AppId={{1C5A039C-72C8-40DC-8E56-890B04DB2306}
AppName=Blurt
AppVersion={#AppVersion}
AppVerName=Blurt {#AppVersion}
AppPublisher=Dušan Jolović
AppCopyright=Copyright (c) 2026 Dušan Jolović
PrivilegesRequired=lowest
DefaultDirName={localappdata}\Programs\Blurt
DisableDirPage=yes
DisableProgramGroupPage=yes
OutputDir=..\artifacts\installer
OutputBaseFilename=Blurt-{#AppVersion}-setup
SetupIconFile=..\src\Blurt\Assets\blurt.ico
UninstallDisplayIcon={app}\Blurt.exe
UninstallDisplayName=Blurt
WizardStyle=modern
; One file per display scale (100, 125, 150, 200, 250 percent); Setup picks the closest.
WizardImageFile=wizard\large-100.png,wizard\large-125.png,wizard\large-150.png,wizard\large-200.png,wizard\large-250.png
WizardSmallImageFile=wizard\small-100.png,wizard\small-125.png,wizard\small-150.png,wizard\small-200.png,wizard\small-250.png
Compression=lzma2/max
SolidCompression=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
; Closes a running Blurt before files are replaced on upgrade.
CloseApplications=yes

[Tasks]
Name: desktopicon; Description: "Create a desktop shortcut"; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Excludes: "*.pdb"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\Blurt"; Filename: "{app}\Blurt.exe"
Name: "{autodesktop}\Blurt"; Filename: "{app}\Blurt.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\Blurt.exe"; Description: "Start Blurt"; Flags: nowait postinstall skipifsilent

[Code]
// The uninstaller does not close running apps. Started while Blurt runs, it removes the uninstall
// entry and then fails to delete the open Blurt.exe. The mutex is created by App.xaml.cs.
function InitializeUninstall(): Boolean;
begin
  Result := True;
  while CheckForMutexes('Blurt.SingleInstance') do
    if UninstallSilent or
       (MsgBox('Blurt is running. Close it, then click OK.', mbError, MB_OKCANCEL) = IDCANCEL) then
    begin
      Result := False;
      Exit;
    end;
end;

// The uninstaller removes only the files it installed. The sounds folder and blurt.json are the
// user's own, so they are deleted only when the user says so.
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if (CurUninstallStep = usPostUninstall) and not UninstallSilent and DirExists(ExpandConstant('{app}')) then
    if MsgBox('Also delete your sounds and settings?' + #13#10#13#10 + ExpandConstant('{app}'),
              mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
      DelTree(ExpandConstant('{app}'), True, True, True);
end;
