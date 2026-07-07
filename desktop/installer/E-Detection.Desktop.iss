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

[InstallDelete]
Type: filesandordirs; Name: "{app}\Assets"
Type: filesandordirs; Name: "{app}\core"
Type: filesandordirs; Name: "{app}\e_detection"
Type: filesandordirs; Name: "{app}\python-runtime"
Type: filesandordirs; Name: "{app}\python-wheelhouse"
Type: filesandordirs; Name: "{app}\Styles"
Type: filesandordirs; Name: "{app}\Views"
Type: files; Name: "{app}\*.dll"
Type: files; Name: "{app}\*.exe"
Type: files; Name: "{app}\*.json"
Type: files; Name: "{app}\*.pri"
Type: files; Name: "{app}\*.ps1"
Type: files; Name: "{app}\*.toml"
Type: files; Name: "{app}\*.txt"
Type: files; Name: "{app}\*.xbf"

[Icons]
Name: "{group}\E-Detection Desktop"; Filename: "{app}\{#AppExeName}"; WorkingDir: "{app}"; IconFilename: "{app}\Assets\Icons\app.ico"
Name: "{autodesktop}\E-Detection Desktop"; Filename: "{app}\{#AppExeName}"; WorkingDir: "{app}"; IconFilename: "{app}\Assets\Icons\app.ico"; Tasks: desktopicon

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\App Paths\{#AppExeName}"; ValueType: string; ValueName: ""; ValueData: "{app}\{#AppExeName}"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\App Paths\{#AppExeName}"; ValueType: string; ValueName: "Path"; ValueData: "{app}"

[Run]
Filename: "{app}\{#AppExeName}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\Uninstall-Desktop.ps1"" -InstallDirectory ""{app}"" -CleanupOnly -Quiet"; Flags: runhidden waituntilterminated; RunOnceId: "CleanupUserStartupEntries"

[Code]
function IsSamePath(PathA: string; PathB: string): Boolean;
begin
  Result := CompareText(RemoveBackslashUnlessRoot(ExpandFileName(PathA)), RemoveBackslashUnlessRoot(ExpandFileName(PathB))) = 0;
end;

function IsPathInside(ChildPath: string; ParentPath: string): Boolean;
var
  Child: string;
  Parent: string;
begin
  Child := AddBackslash(ExpandFileName(ChildPath));
  Parent := AddBackslash(ExpandFileName(ParentPath));
  Result := Pos(Lowercase(Parent), Lowercase(Child)) = 1;
end;

function IsUnsafeInstallDirectory(Dir: string): Boolean;
var
  ExpandedDir: string;
  DriveRoot: string;
  UserProfile: string;
begin
  ExpandedDir := RemoveBackslashUnlessRoot(ExpandFileName(Dir));
  DriveRoot := ExtractFileDrive(ExpandedDir) + '\';
  UserProfile := GetEnv('USERPROFILE');
  Result :=
    IsSamePath(ExpandedDir, DriveRoot) or
    ((UserProfile <> '') and IsSamePath(ExpandedDir, UserProfile)) or
    IsSamePath(ExpandedDir, ExpandConstant('{userdesktop}')) or
    IsPathInside(ExpandedDir, ExpandConstant('{autopf}')) or
    IsPathInside(ExpandedDir, ExpandConstant('{commonpf}')) or
    IsPathInside(ExpandedDir, ExpandConstant('{commonpf32}'));
end;

function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := True;
  if CurPageID = wpSelectDir then
  begin
    if IsUnsafeInstallDirectory(WizardDirValue) then
    begin
      MsgBox(
        '当前安装向导按普通用户权限安装。请选择用户目录下的应用文件夹，例如默认位置，避免安装到 Program Files、桌面、用户根目录或磁盘根目录。',
        mbError,
        MB_OK);
      Result := False;
    end;
  end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := '';
  if IsUnsafeInstallDirectory(WizardDirValue) then
  begin
    Result := '当前安装向导按普通用户权限安装。请选择用户目录下的应用文件夹，例如默认位置，避免安装到 Program Files、桌面、用户根目录或磁盘根目录。';
  end;
end;
