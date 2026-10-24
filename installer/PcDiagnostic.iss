; PC Diagnostic & Incident Recorder - installer (Inno Setup 7). Build it with installer\build.ps1, which publishes the
; self-contained Agent and Web host, fetches the pinned PawnIO setup, writes THIRD-PARTY-NOTICES.txt and compiles
; this script. Nothing has to be installed separately on the target PC (requirement 2026-10-06):
;   - .NET is inside the application folders (self-contained);
;   - PawnIO, the driver LibreHardwareMonitor uses for CPU temperature and power, is installed by its own unmodified
;     setup when the user keeps that option (it is a separate GPL-2.0 program; see THIRD-PARTY-NOTICES.txt);
;   - a scheduled task starts the recorder with administrator rights at sign-in, without a UAC prompt each time;
;   - a firewall rule (private networks, local subnet, the dashboard host only) lets paired devices reach the
;     dashboard once the user turns network access on in Settings.

#ifndef AppVersion
  #define AppVersion "0.1.0"
#endif
#ifndef StageDir
  #define StageDir "..\artifacts\installer\stage"
#endif
#ifndef OutputDir
  #define OutputDir "..\artifacts\installer"
#endif
#ifndef PawnIOVersion
  #define PawnIOVersion "2.2.0"
#endif

#define AppName "PC Diagnostic"
#define RecorderTask "\PC Diagnostic\Recorder"
#define FirewallTcp "PC Diagnostic dashboard (local network)"
#define FirewallUdp "PC Diagnostic discovery (local network)"

[Setup]
AppId={{A3C6E1B2-5D4F-4E8A-9B27-6C1D0F3E8A54}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=PC Diagnostic
AppComments=Local-first black-box recorder for Windows: records system activity and preserves the evidence around problems.
VersionInfoVersion={#AppVersion}
DefaultDirName={autopf}\{#AppName}
DisableProgramGroupPage=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19041
OutputDir={#OutputDir}
OutputBaseFilename=PcDiagnostic-Setup-{#AppVersion}
SetupIconFile=..\src\PcDiag.Agent\app.ico
UninstallDisplayIcon={app}\agent\PcDiag.Agent.exe
UninstallDisplayName={#AppName}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
; The welcome page names the version this installs (and the window title says it too).
DisableWelcomePage=no
CloseApplications=force
RestartApplications=no
SetupLogging=yes
#ifdef SignTool
SignTool={#SignTool}
SignedUninstaller=yes
#endif

[Tasks]
Name: "autostart"; Description: "Start the recorder when I sign in, with administrator rights and without a prompt (recommended: CPU temperature, power and storage health need them)"; GroupDescription: "Recorder:"
Name: "pawnio"; Description: "Install the PawnIO driver for CPU temperature and power readings (recommended; a separate open-source program)"; GroupDescription: "Hardware sensors:"; Check: PawnIONeeded
Name: "firewall"; Description: "Let my other devices reach the dashboard when I turn network access on in Settings (private networks only)"; GroupDescription: "Network:"
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[InstallDelete]
; An update replaces the application folders completely (no stale files from older versions).
Type: filesandordirs; Name: "{app}\agent"
Type: filesandordirs; Name: "{app}\web"

[Files]
Source: "{#StageDir}\agent\*"; DestDir: "{app}\agent"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#StageDir}\web\*"; DestDir: "{app}\web"; Flags: ignoreversion recursesubdirs createallsubdirs
; PC Diagnostic Storage: SeaweedFS (set up as the default storage location on the first start).
Source: "{#StageDir}\tools\*"; DestDir: "{app}\tools"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "RecorderTask.ps1"; DestDir: "{app}\setup"; Flags: ignoreversion
Source: "{#StageDir}\THIRD-PARTY-NOTICES.txt"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#StageDir}\PawnIO_setup.exe"; DestDir: "{tmp}"; Flags: deleteafterinstall; Tasks: pawnio

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\agent\PcDiag.Agent.exe"; Parameters: "open"; WorkingDir: "{app}\agent"; Comment: "Open the dashboard (starts the recorder if it is not running)"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\agent\PcDiag.Agent.exe"; Parameters: "open"; WorkingDir: "{app}\agent"; Comment: "Open the dashboard (starts the recorder if it is not running)"; Tasks: desktopicon

[Run]
Filename: "{tmp}\PawnIO_setup.exe"; Parameters: "-install -silent"; StatusMsg: "Installing the PawnIO driver..."; Tasks: pawnio; Flags: waituntilterminated
Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; Parameters: "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File ""{app}\setup\RecorderTask.ps1"" -Register -AgentPath ""{app}\agent\PcDiag.Agent.exe"" -User ""{code:OriginalUser}"""; StatusMsg: "Registering the recorder to start at sign-in..."; Tasks: autostart; Flags: runhidden waituntilterminated
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""{#FirewallTcp}"""; Tasks: firewall; Flags: runhidden waituntilterminated
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""{#FirewallUdp}"""; Tasks: firewall; Flags: runhidden waituntilterminated
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall add rule name=""{#FirewallTcp}"" dir=in action=allow program=""{app}\web\PcDiag.Web.exe"" protocol=TCP profile=private remoteip=localsubnet enable=yes"; StatusMsg: "Adding the firewall rule..."; Tasks: firewall; Flags: runhidden waituntilterminated
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall add rule name=""{#FirewallUdp}"" dir=in action=allow program=""{app}\web\PcDiag.Web.exe"" protocol=UDP profile=private remoteip=localsubnet enable=yes"; Tasks: firewall; Flags: runhidden waituntilterminated
; Starts the recorder (through the task when it was registered) and opens the dashboard, as the signed-in user.
Filename: "{app}\agent\PcDiag.Agent.exe"; Parameters: "open"; WorkingDir: "{app}\agent"; Description: "Start {#AppName} and open the dashboard"; Flags: postinstall nowait skipifsilent runasoriginaluser

[UninstallRun]
Filename: "{sys}\taskkill.exe"; Parameters: "/F /IM PcDiag.Agent.exe"; Flags: runhidden waituntilterminated; RunOnceId: "StopAgent"
Filename: "{sys}\taskkill.exe"; Parameters: "/F /IM PcDiag.Web.exe"; Flags: runhidden waituntilterminated; RunOnceId: "StopWeb"
Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; Parameters: "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File ""{app}\setup\RecorderTask.ps1"" -Unregister"; Flags: runhidden waituntilterminated; RunOnceId: "RemoveTask"
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""{#FirewallTcp}"""; Flags: runhidden waituntilterminated; RunOnceId: "RemoveFirewallTcp"
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""{#FirewallUdp}"""; Flags: runhidden waituntilterminated; RunOnceId: "RemoveFirewallUdp"

[Code]
const
  PawnIOKey = 'SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PawnIO';
  OwnUninstallKey = 'SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{A3C6E1B2-5D4F-4E8A-9B27-6C1D0F3E8A54}_is1';
  { Written into the data folder when an uninstall kept it, so the next install offers to use it again. }
  KeptMarker = 'kept-at-uninstall.txt';

var
  DataPage: TInputOptionWizardPage;

function CompareVersions(A, B: String): Integer;
var
  PackedA, PackedB: Int64;
begin
  if not StrToVersion(A, PackedA) then PackedA := 0;
  if not StrToVersion(B, PackedB) then PackedB := 0;
  Result := ComparePackedVersion(PackedA, PackedB);
end;

{ PawnIO is offered when it is missing or older than the bundled version (its own setup upgrades it in place). }
function PawnIONeeded: Boolean;
var
  Installed: String;
begin
  Result := True;
  if RegQueryStringValue(HKLM64, PawnIOKey, 'DisplayVersion', Installed) then
    Result := CompareVersions(Installed, '{#PawnIOVersion}') < 0;
end;

{ The account that started Setup before it was elevated: the recorder task is registered for that user, also when
  an administrator's credentials were typed into the UAC prompt. Falls back to the account running Setup. }
function OriginalUser(Param: String): String;
var
  Output: AnsiString;
  ResultCode, Start, Finish: Integer;
  FileName, Text: String;
begin
  Result := GetUserNameString;
  FileName := ExpandConstant('{tmp}\original-user.txt');
  if ExecAsOriginalUser(ExpandConstant('{cmd}'), '/c whoami /user /fo csv /nh > "' + FileName + '"', '', SW_HIDE, ewWaitUntilTerminated, ResultCode)
     and (ResultCode = 0) and LoadStringFromFile(FileName, Output) then
  begin
    Text := String(Output);
    Start := Pos('"S-1-', Text);
    if Start > 0 then
    begin
      Text := Copy(Text, Start + 1, Length(Text));
      Finish := Pos('"', Text);
      if Finish > 1 then
        Result := Copy(Text, 1, Finish - 1);
    end;
  end;
  Log('Recorder task user: ' + Result);
end;

{ The recordings, incidents and settings (the recorder's databases) of the user installing. }
function DataDir: String;
begin
  Result := ExpandConstant('{localappdata}\PcDiagnostic');
end;

function FolderSize(Folder: String): Int64;
var
  Find: TFindRec;
begin
  Result := 0;
  if FindFirst(Folder + '\*', Find) then
  try
    repeat
      if (Find.Name <> '.') and (Find.Name <> '..') then
        if Find.Attributes and FILE_ATTRIBUTE_DIRECTORY <> 0 then
          Result := Result + FolderSize(Folder + '\' + Find.Name)
        else
          Result := Result + Int64(Find.SizeHigh) * 4294967296 + Find.SizeLow;
    until not FindNext(Find);
  finally
    FindClose(Find);
  end;
end;

function SizeText(Bytes: Int64): String;
begin
  if Bytes >= 1073741824 then
    Result := Format('%.1f GB', [Bytes / 1073741824.0])
  else if Bytes >= 1048576 then
    Result := Format('%.0f MB', [Bytes / 1048576.0])
  else
    Result := Format('%.0f KB', [Bytes / 1024.0]);
end;

{ The version of PC Diagnostic installed now ('' when none). }
function InstalledVersion: String;
begin
  if not RegQueryStringValue(HKLM64, OwnUninstallKey, 'DisplayVersion', Result) then
    if not RegQueryStringValue(HKLM32, OwnUninstallKey, 'DisplayVersion', Result) then
      Result := '';
end;

{ What this setup does: 'new' (none installed), 'repair' (the same version), 'upgrade' (an older one) or 'downgrade'. }
var
  SetupMode: String;

function IsUpdate: Boolean;
begin
  Result := SetupMode <> 'new';
end;

{ Runs the installed copy's uninstaller (it asks about the drivers and the data itself) and waits for it. }
procedure RunInstalledUninstaller;
var
  Command: String;
  ResultCode: Integer;
begin
  if not RegQueryStringValue(HKLM64, OwnUninstallKey, 'UninstallString', Command) then
    if not RegQueryStringValue(HKLM32, OwnUninstallKey, 'UninstallString', Command) then
      Exit;
  Exec(RemoveQuotes(Command), '', '', SW_SHOW, ewWaitUntilTerminated, ResultCode);
end;

{
  Before the wizard: the same version installed is repaired or uninstalled (asked); an older one is upgraded (said on
  the data page); a newer one is replaced by this older one only when the user says so. A silent setup repairs,
  upgrades or replaces without asking.
}
function InitializeSetup: Boolean;
var
  Installed: String;
  Order: Integer;
begin
  Result := True;
  Installed := InstalledVersion;
  if Installed = '' then
  begin
    SetupMode := 'new';
    Exit;
  end;

  Order := CompareVersions('{#AppVersion}', Installed);
  if Order = 0 then
  begin
    SetupMode := 'repair';
    if not WizardSilent then
      case TaskDialogMsgBox('PC Diagnostic {#AppVersion} is already installed.',
             'Repair puts its program files, start-up task and firewall rules back as this version has them; your recordings, incidents and settings are kept.' + #13#10#13#10 +
             'Uninstall removes it (it asks whether to keep your data and drivers).',
             mbConfirmation, MB_YESNOCANCEL, ['Repair', 'Uninstall'], 0) of
        IDYES: ;
        IDNO:
          begin
            RunInstalledUninstaller;
            Result := False;
          end;
      else
        Result := False;
      end;
  end
  else if Order > 0 then
    SetupMode := 'upgrade'
  else
  begin
    SetupMode := 'downgrade';
    if not WizardSilent then
      Result := TaskDialogMsgBox('A newer PC Diagnostic (' + Installed + ') is installed.',
                  'This setup installs the older version {#AppVersion} in its place. Data saved by the newer version may not open in the older one.',
                  mbConfirmation, MB_YESNO, ['Install {#AppVersion} anyway', 'Keep ' + Installed], 0) = IDYES;
  end;
end;

function HasData: Boolean;
begin
  Result := DirExists(DataDir) and (FolderSize(DataDir) > 0);
end;

{ /DATA=keep (the default for a silent install), /DATA=fresh (the old data kept as a backup) or /DATA=delete. }
function DataChoice: String;
begin
  Result := Lowercase(ExpandConstant('{param:DATA|}'));
  if not WizardSilent and (DataPage <> nil) then
    case DataPage.SelectedValueIndex of
      0: Result := 'keep';
      1: Result := 'fresh';
      2: Result := 'delete';
    end;
  if (Result <> 'fresh') and (Result <> 'delete') then
    Result := 'keep';
end;

procedure InitializeWizard;
var
  Kept: Boolean;
  Heading, Found: String;
begin
  WizardForm.Caption := 'PC Diagnostic {#AppVersion} Setup';
  { A repair keeps the data without asking; an upgrade, a downgrade or a new installation that finds data asks. }
  if (SetupMode = 'repair') or not HasData then
    Exit;
  Kept := FileExists(DataDir + '\' + KeptMarker);
  Found := 'Recordings, incidents and settings are in' + #13#10 + DataDir + ' (' + SizeText(FolderSize(DataDir)) + ').' + #13#10;
  if SetupMode = 'upgrade' then
    Heading := 'PC Diagnostic ' + InstalledVersion + ' is installed: this upgrades it to {#AppVersion}.'
  else if SetupMode = 'downgrade' then
    Heading := 'PC Diagnostic ' + InstalledVersion + ' is installed: this replaces it with {#AppVersion}.'
  else
  begin
    Heading := 'PC Diagnostic data is already on this PC.';
    Found := Found + 'They come from an earlier installation or another copy of PC Diagnostic. ';
  end;
  DataPage := CreateInputOptionPage(wpSelectTasks, 'Your data', Heading, Found + 'What should this installation do with them?', True, False);
  DataPage.Add('Keep using them');
  DataPage.Add('Start fresh, and keep the old data as a backup folder beside it');
  DataPage.Add('Start fresh, and delete the old data');
  { An upgrade, and data an uninstall kept for later, use them again by default; any other data is set aside. }
  if IsUpdate or Kept then
    DataPage.SelectedValueIndex := 0
  else
    DataPage.SelectedValueIndex := 1;
end;

{ Before the files are copied (the running copies were stopped in PrepareToInstall). }
procedure ApplyDataChoice;
var
  Choice, Backup: String;
  Tries: Integer;
begin
  if not DirExists(DataDir) then
    Exit;
  Choice := DataChoice;
  Log('Existing data: ' + Choice);
  if Choice = 'keep' then
  begin
    DeleteFile(DataDir + '\' + KeptMarker);
    Exit;
  end;
  if Choice = 'delete' then
  begin
    if not DelTree(DataDir, True, True, True) then
      MsgBox('Some of the old data could not be deleted (a program may still be using it):' + #13#10 + DataDir, mbError, MB_OK);
    Exit;
  end;
  Backup := DataDir + '.backup-' + GetDateTimeString('yyyymmdd-hhnnss', #0, #0);
  { The stopped programs may take a moment to let go of their databases. }
  for Tries := 1 to 10 do
  begin
    if RenameFile(DataDir, Backup) then
    begin
      Log('Old data moved to ' + Backup);
      Exit;
    end;
    Sleep(500);
  end;
  MsgBox('The old data could not be moved aside (a program may still be using it), so this installation will use it.' + #13#10 +
         'To start fresh later, close PC Diagnostic and rename or delete:' + #13#10 + DataDir, mbError, MB_OK);
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssInstall then
    ApplyDataChoice;
end;

{ Running copies hold their files open; an update stops them first (the recorder flushes at most the last seconds). }
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ResultCode: Integer;
begin
  Exec(ExpandConstant('{sys}\schtasks.exe'), '/End /TN "{#RecorderTask}"', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM PcDiag.Agent.exe', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM PcDiag.Web.exe', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Result := '';
end;

{ The drivers PC Diagnostic installed: the Windows drivers installed from Settings > Devices (their oemNN.inf names,
  recorded in windows-drivers\installed.json) and PawnIO. }
var
  RemoveDrivers: Boolean;

function InstalledWindowsDrivers: TArrayOfString;
var
  Text: AnsiString;
  Rest, Name: String;
  Start, Finish, Count, I: Integer;
  Valid: Boolean;
begin
  SetArrayLength(Result, 0);
  if not LoadStringFromFile(ExpandConstant('{commonappdata}\PcDiagnostic\windows-drivers\installed.json'), Text) then
    Exit;
  Rest := Lowercase(String(Text));
  Count := 0;
  Start := Pos('"oem', Rest);
  while Start > 0 do
  begin
    Rest := Copy(Rest, Start + 1, Length(Rest));
    Finish := Pos('"', Rest);
    if Finish = 0 then
      Break;
    Name := Copy(Rest, 1, Finish - 1);
    Rest := Copy(Rest, Finish + 1, Length(Rest));
    { oem<digits>.inf only, as the recorder writes it. }
    Valid := (Length(Name) > 7) and (Length(Name) <= 13) and (Copy(Name, Length(Name) - 3, 4) = '.inf');
    if Valid then
      for I := 4 to Length(Name) - 4 do
        if (Name[I] < '0') or (Name[I] > '9') then
          Valid := False;
    if Valid then
    begin
      SetArrayLength(Result, Count + 1);
      Result[Count] := Name;
      Count := Count + 1;
    end;
    Start := Pos('"oem', Rest);
  end;
end;

function PawnIOInstalled: Boolean;
begin
  Result := RegKeyExists(HKLM64, PawnIOKey);
end;

{ Runs PawnIO's own quiet uninstaller (QuietUninstallString: "<path>\uninstall.exe" -uninstall -silent). }
procedure RemovePawnIO;
var
  Command, FileName, Params: String;
  Finish, ResultCode: Integer;
begin
  if not RegQueryStringValue(HKLM64, PawnIOKey, 'QuietUninstallString', Command) then
    Exit;
  Command := Trim(Command);
  if Copy(Command, 1, 1) = '"' then
  begin
    Command := Copy(Command, 2, Length(Command));
    Finish := Pos('"', Command);
    FileName := Copy(Command, 1, Finish - 1);
    Params := Trim(Copy(Command, Finish + 1, Length(Command)));
  end
  else
  begin
    Finish := Pos(' ', Command);
    if Finish = 0 then Finish := Length(Command) + 1;
    FileName := Copy(Command, 1, Finish - 1);
    Params := Trim(Copy(Command, Finish + 1, Length(Command)));
  end;
  if FileExists(FileName) then
    Exec(FileName, Params, '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Log('PawnIO uninstall: ' + IntToStr(ResultCode));
end;

{ Uninstalls each Windows driver from the devices using it and removes it from the driver store (never forced). }
procedure RemoveWindowsDrivers(Drivers: TArrayOfString);
var
  I, ResultCode: Integer;
begin
  for I := 0 to GetArrayLength(Drivers) - 1 do
  begin
    Exec(ExpandConstant('{sys}\pnputil.exe'), '/delete-driver ' + Drivers[I] + ' /uninstall', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    Log('pnputil /delete-driver ' + Drivers[I] + ': ' + IntToStr(ResultCode));
  end;
end;

{ Recordings, incidents and settings live in each user's %LOCALAPPDATA%\PcDiagnostic; they are kept unless the
  user asks to remove them (a marker in them says so, and the next installation offers to use them again). Driver packages in %ProgramData%\PcDiagnostic are removed with the program. The
  drivers it installed (PawnIO, Windows drivers for devices) are removed when the user chooses to (asked first, No
  by default; /REMOVEDRIVERS on the command line for a silent uninstall): other programs may use PawnIO. }
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  SharedDir, List: String;
  Drivers: TArrayOfString;
  I: Integer;
begin
  if CurUninstallStep = usUninstall then
  begin
    Drivers := InstalledWindowsDrivers;
    RemoveDrivers := Pos('/REMOVEDRIVERS', Uppercase(GetCmdTail)) > 0;
    if not RemoveDrivers and not UninstallSilent and (PawnIOInstalled or (GetArrayLength(Drivers) > 0)) then
    begin
      List := '';
      if PawnIOInstalled then
        List := List + #13#10 + '  - PawnIO (CPU temperature and power readings; other monitoring programs such as FanControl or LibreHardwareMonitor may use it too)';
      for I := 0 to GetArrayLength(Drivers) - 1 do
        List := List + #13#10 + '  - ' + Drivers[I] + ' (a device driver installed from PC Diagnostic)';
      RemoveDrivers := MsgBox('Also remove the drivers PC Diagnostic installed?' + #13#10 + List + #13#10#13#10 +
                              'Choose No to keep them.', mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES;
    end;
  end;
  if CurUninstallStep = usPostUninstall then
  begin
    { The recorder and the dashboard have been stopped by now, so nothing holds the drivers open. }
    if RemoveDrivers then
    begin
      RemoveWindowsDrivers(InstalledWindowsDrivers);
      if PawnIOInstalled then
        RemovePawnIO;
    end;
    SharedDir := ExpandConstant('{commonappdata}\PcDiagnostic');
    DelTree(SharedDir + '\drivers', True, True, True);
    DelTree(SharedDir + '\driver-staging', True, True, True);
    DelTree(SharedDir + '\screen-drivers', True, True, True);
    DelTree(SharedDir + '\windows-drivers', True, True, True);
    if DirExists(DataDir) then
    begin
      if not UninstallSilent and (MsgBox('Also delete your recordings, incidents and settings?' + #13#10#13#10 + DataDir + #13#10#13#10 +
                'Choose No to keep them for a later installation.', mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES) then
        DelTree(DataDir, True, True, True)
      else
        SaveStringToFile(DataDir + '\' + KeptMarker, 'Kept when PC Diagnostic was uninstalled; the next installation offers to use it again.', False);
    end;
  end;
end;
