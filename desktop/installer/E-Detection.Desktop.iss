#define AppName "E-Detection Desktop"
#define AppExeName "EDetection.Desktop.exe"
#ifndef AppVersion
#define AppVersion "0.1.0"
#endif
#ifndef RuntimeIdentifier
#define RuntimeIdentifier "win-x64"
#endif
#ifndef SourceDir
#define SourceDir "..\..\artifacts\desktop\win-x64\publish"
#endif
#ifndef OutputDir
#define OutputDir "..\..\artifacts\desktop\win-x64\installer"
#endif

[Setup]
AppId={{0EAA42D9-6459-480D-9EB8-5FA69C9BADBB}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher=E-Detection OSS
AppPublisherURL=https://github.com/osGex0o0II/E-Detection-OSS
AppSupportURL=https://github.com/osGex0o0II/E-Detection-OSS/issues
AppUpdatesURL=https://github.com/osGex0o0II/E-Detection-OSS/releases/latest
DefaultDirName={localappdata}\Programs\E-Detection Desktop
DefaultGroupName=E-Detection
AllowNoIcons=yes
DisableDirPage=no
DisableProgramGroupPage=no
OutputDir={#OutputDir}
OutputBaseFilename=E-Detection.Desktop-Setup-{#RuntimeIdentifier}
SetupIconFile={#SourceDir}\Assets\Icons\app.ico
UninstallDisplayIcon={app}\{#AppExeName}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
CloseApplications=yes
RestartApplications=no
CloseApplicationsFilter={#AppExeName}

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\E-Detection Desktop"; Filename: "{app}\{#AppExeName}"; WorkingDir: "{app}"; IconFilename: "{app}\Assets\Icons\app.ico"
Name: "{autodesktop}\E-Detection Desktop"; Filename: "{app}\{#AppExeName}"; WorkingDir: "{app}"; IconFilename: "{app}\Assets\Icons\app.ico"; Tasks: desktopicon

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\App Paths\{#AppExeName}"; ValueType: string; ValueName: ""; ValueData: "{app}\{#AppExeName}"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\App Paths\{#AppExeName}"; ValueType: string; ValueName: "Path"; ValueData: "{app}"

[Run]
Filename: "{app}\{#AppExeName}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent
