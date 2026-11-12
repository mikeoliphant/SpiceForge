; SpiceForge's Windows installer, built by SpiceForge.Package (Inno Setup 6), which passes:
;   AppVersion  the version (0.1.0)
;   SourceDir   the published, assembled folder (artifacts/stage/win-x64/SpiceForge)
;   OutputDir   where the installer goes (artifacts)

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif

[Setup]
AppId={{8C6E2F41-7B3D-4E59-9A1C-5D2F8E4B7A63}
AppName=SpiceForge
AppVersion={#AppVersion}
AppPublisher=Mike Oliphant
AppPublisherURL=https://github.com/mikeoliphant/SpiceForge
AppSupportURL=https://github.com/mikeoliphant/SpiceForge/issues
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
DefaultDirName={autopf}\SpiceForge
DefaultGroupName=SpiceForge
DisableProgramGroupPage=yes
; For everyone (needs administrator rights) or just the current user, as chosen.
PrivilegesRequiredOverridesAllowed=dialog
SetupIconFile=..\Assets\Icon\SpiceForge.ico
UninstallDisplayIcon={app}\SpiceForgeServer.exe
UninstallDisplayName=SpiceForge
LicenseFile={#SourceDir}\LICENSE.txt
Compression=lzma2
SolidCompression=yes
OutputDir={#OutputDir}
OutputBaseFilename=SpiceForge-{#AppVersion}-setup
WizardStyle=modern

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
; The examples also go to the user's LiveSPICE folder, where the editor's dialogs start; files there aren't replaced.
Source: "{#SourceDir}\Examples\*.schx"; DestDir: "{userdocs}\LiveSPICE\Examples"; Flags: onlyifdoesntexist uninsneveruninstall

[Icons]
Name: "{group}\SpiceForge"; Filename: "{app}\SpiceForgeServer.exe"; WorkingDir: "{app}"
Name: "{group}\Uninstall SpiceForge"; Filename: "{uninstallexe}"
Name: "{autodesktop}\SpiceForge"; Filename: "{app}\SpiceForgeServer.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\SpiceForgeServer.exe"; Description: "Start SpiceForge"; Flags: postinstall nowait skipifsilent

[UninstallRun]
; A running SpiceForge would keep its files in use.
Filename: "{sys}\taskkill.exe"; Parameters: "/F /IM SpiceForgeServer.exe"; Flags: runhidden; RunOnceId: "StopSpiceForge"
