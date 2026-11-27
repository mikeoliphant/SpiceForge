; SpiceForge's Windows installer, built by SpiceForge.Package (Inno Setup 6), which passes:
;   AppVersion  the version, from git tags (0.2.0, or 0.2.1-dev.4 for a development build)
;   SourceDir   the published, assembled folder (artifacts/stage/win-x64/SpiceForge), with the plugin's bundles in
;               clap\ and vst3\
;   OutputDir   where the installer goes (artifacts)

#ifndef AppVersion
  #error AppVersion isn't defined: build the installer with SpiceForge.Package, which passes the version.
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
Name: "plugins"; Description: "The SpiceForge plugin for DAWs (CLAP and VST3; needs the .NET 10 Desktop Runtime)"; GroupDescription: "Plugins:"

[Files]
Source: "{#SourceDir}\*"; Excludes: "\clap\*,\vst3\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
; The plugin goes where DAWs look for it: Common Files\CLAP and Common Files\VST3, in Program Files for everyone, or
; in %LOCALAPPDATA%\Programs\Common for the current user ({autocf64} is whichever the installation is).
Source: "{#SourceDir}\clap\*"; DestDir: "{autocf64}\CLAP"; Flags: ignoreversion recursesubdirs createallsubdirs skipifsourcedoesntexist; Tasks: plugins
Source: "{#SourceDir}\vst3\*"; DestDir: "{autocf64}\VST3"; Flags: ignoreversion recursesubdirs createallsubdirs skipifsourcedoesntexist; Tasks: plugins
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
