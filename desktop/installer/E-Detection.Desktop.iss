#define AppName "EDetection"
#define AppExeName "EDetection.exe"
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
#ifndef UnsafeInstallRootOverride
#define UnsafeInstallRootOverride ""
#endif

[Setup]
AppId={{0EAA42D9-6459-480D-9EB8-5FA69C9BADBB}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher=E-Detection OSS
AppPublisherURL=https://github.com/osGex0o0II/E-Detection-OSS
AppSupportURL=https://github.com/osGex0o0II/E-Detection-OSS/issues
AppUpdatesURL=https://github.com/osGex0o0II/E-Detection-OSS/releases/latest
DefaultDirName={localappdata}\Programs\EDetection
DefaultGroupName=E-Detection
AllowNoIcons=yes
DisableDirPage=no
DisableProgramGroupPage=no
OutputDir={#OutputDir}
OutputBaseFilename=EDetection-Setup-{#RuntimeIdentifier}
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
CloseApplicationsFilter={#AppExeName},EDetection.Desktop.exe

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[InstallDelete]
Type: filesandordirs; Name: "{app}\Assets"; Check: ShouldCleanExistingProductDirectory
Type: filesandordirs; Name: "{app}\core"; Check: ShouldCleanExistingProductDirectory
Type: filesandordirs; Name: "{app}\e_detection"; Check: ShouldCleanExistingProductDirectory
Type: filesandordirs; Name: "{app}\python-runtime"; Check: ShouldCleanExistingProductDirectory
Type: filesandordirs; Name: "{app}\python-wheelhouse"; Check: ShouldCleanExistingProductDirectory
Type: filesandordirs; Name: "{app}\Styles"; Check: ShouldCleanExistingProductDirectory
Type: filesandordirs; Name: "{app}\Views"; Check: ShouldCleanExistingProductDirectory

[Icons]
Name: "{group}\EDetection"; Filename: "{app}\{#AppExeName}"; WorkingDir: "{app}"; IconFilename: "{app}\Assets\Icons\app.ico"
Name: "{autodesktop}\EDetection"; Filename: "{app}\{#AppExeName}"; WorkingDir: "{app}"; IconFilename: "{app}\Assets\Icons\app.ico"; Tasks: desktopicon

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
    (('{#UnsafeInstallRootOverride}' <> '') and
      (IsSamePath(ExpandedDir, '{#UnsafeInstallRootOverride}') or
       IsPathInside(ExpandedDir, '{#UnsafeInstallRootOverride}'))) or
    IsPathInside(ExpandedDir, ExpandConstant('{autopf}')) or
    IsPathInside(ExpandedDir, ExpandConstant('{commonpf}')) or
    IsPathInside(ExpandedDir, ExpandConstant('{commonpf32}'));
end;

function LooksLikeExistingProductDirectory(Dir: string): Boolean;
begin
  Result :=
    (
      FileExists(AddBackslash(Dir) + '{#AppExeName}') and
      (
        FileExists(AddBackslash(Dir) + 'release-info.txt') or
        FileExists(AddBackslash(Dir) + 'unins000.dat') or
        FileExists(AddBackslash(Dir) + 'EDetection.dll')
      )
    ) or
    (
      FileExists(AddBackslash(Dir) + 'EDetection.Desktop.exe') and
      (
        FileExists(AddBackslash(Dir) + 'release-info.txt') or
        FileExists(AddBackslash(Dir) + 'unins000.dat') or
        FileExists(AddBackslash(Dir) + 'EDetection.Desktop.dll')
      )
    );
end;

function ShouldCleanExistingProductDirectory: Boolean;
begin
  Result := DirExists(ExpandConstant('{app}')) and LooksLikeExistingProductDirectory(ExpandConstant('{app}'));
end;

function IsSafeInstallManifestPath(RelativePath: string): Boolean;
var
  TargetPath: string;
begin
  Result :=
    (RelativePath <> '') and
    (ExtractFileDrive(RelativePath) = '') and
    (Pos('..', RelativePath) = 0);

  if Result then
  begin
    TargetPath := ExpandFileName(AddBackslash(ExpandConstant('{app}')) + RelativePath);
    Result := IsPathInside(TargetPath, ExpandConstant('{app}'));
  end;
end;

function ShouldPreserveInstallMarker(RelativePath: string): Boolean;
begin
  Result :=
    (CompareText(RelativePath, '{#AppExeName}') = 0) or
    (CompareText(RelativePath, 'EDetection.dll') = 0) or
    (CompareText(RelativePath, 'release-info.txt') = 0) or
    (CompareText(RelativePath, 'install-files.txt') = 0) or
    (CompareText(RelativePath, 'unins000.dat') = 0) or
    (CompareText(RelativePath, 'unins000.exe') = 0);
end;

procedure DeletePreviousInstallManifestFiles;
var
  Files: TArrayOfString;
  I: Integer;
  RelativePath: string;
  TargetPath: string;
begin
  if not ShouldCleanExistingProductDirectory then
  begin
    exit;
  end;

  if not LoadStringsFromFile(AddBackslash(ExpandConstant('{app}')) + 'install-files.txt', Files) then
  begin
    exit;
  end;

  for I := 0 to GetArrayLength(Files) - 1 do
  begin
    RelativePath := Trim(Files[I]);
    if IsSafeInstallManifestPath(RelativePath) and (not ShouldPreserveInstallMarker(RelativePath)) then
    begin
      TargetPath := ExpandFileName(AddBackslash(ExpandConstant('{app}')) + RelativePath);
      if FileExists(TargetPath) then
      begin
        DeleteFile(TargetPath);
      end;
    end;
  end;
end;

function IsDirectoryEmpty(Dir: string): Boolean;
var
  FindRec: TFindRec;
begin
  Result := True;
  if FindFirst(AddBackslash(Dir) + '*', FindRec) then
  begin
    try
      repeat
        if (FindRec.Name <> '.') and (FindRec.Name <> '..') then
        begin
          Result := False;
          break;
        end;
      until not FindNext(FindRec);
    finally
      FindClose(FindRec);
    end;
  end;
end;

function IsInstallDirectoryAllowed(Dir: string): Boolean;
begin
  Result := (not DirExists(Dir)) or IsDirectoryEmpty(Dir) or LooksLikeExistingProductDirectory(Dir);
end;

function InstallDirectoryErrorMessage: string;
begin
  Result := '请选择空文件夹或现有 EDetection 安装目录。安装向导不会安装到已经包含其他文件的普通文件夹，以避免覆盖或清理用户文件。';
end;

function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := True;
  if CurPageID = wpSelectDir then
  begin
    if IsUnsafeInstallDirectory(WizardDirValue) then
    begin
      if not WizardSilent then
      begin
        MsgBox(
          '当前安装向导按普通用户权限安装。请选择用户目录下的应用文件夹，例如默认位置，避免安装到 Program Files、桌面、用户根目录或磁盘根目录。',
          mbError,
          MB_OK);
      end;
      Result := False;
    end;

    if Result and (not IsInstallDirectoryAllowed(WizardDirValue)) then
    begin
      if not WizardSilent then
      begin
        MsgBox(InstallDirectoryErrorMessage, mbError, MB_OK);
      end;
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

  if (Result = '') and (not IsInstallDirectoryAllowed(WizardDirValue)) then
  begin
    Result := InstallDirectoryErrorMessage;
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssInstall then
  begin
    DeletePreviousInstallManifestFiles;
  end;
end;
