#define ProductVersion GetEnv("DPAI_SD_PACKAGE_VERSION")
#define BuildNumber GetEnv("DPAI_SD_PACKAGE_BUILD")
#define PayloadDirectory GetEnv("DPAI_SD_PACKAGE_PAYLOAD")
#define OutputDirectory GetEnv("DPAI_SD_PACKAGE_OUTPUT")

#if Ver < EncodeVer(6, 3, 0)
  #error Inno Setup 6.3.0 or later is required.
#endif

#if ProductVersion == ""
  #error DPAI_SD_PACKAGE_VERSION must be supplied by tools/package.ps1.
#endif
#if BuildNumber == ""
  #error DPAI_SD_PACKAGE_BUILD must be supplied by tools/package.ps1.
#endif
#if PayloadDirectory == ""
  #error DPAI_SD_PACKAGE_PAYLOAD must be supplied by tools/package.ps1.
#endif
#if OutputDirectory == ""
  #error DPAI_SD_PACKAGE_OUTPUT must be supplied by tools/package.ps1.
#endif

#define ProductName "DEEPAi Service Directory"
#define PublisherName "DEEPAi"
#define MainExecutable "DEEPAi.ServiceDirectory.Service.exe"
#define WatchdogExecutable "DEEPAi.ServiceDirectory.Watchdog.exe"
#define TrayExecutable "DEEPAi.ServiceDirectory.Tray.exe"
#define OutputFileName "DEEPAi-ServiceDirectory-" + ProductVersion + "-build." + BuildNumber + "-x64"

[Setup]
AppId={{B44C6547-15D5-421A-88D7-3D2293BEE48C}
AppName={#ProductName}
AppVersion={#ProductVersion}
AppVerName={#ProductName} {#ProductVersion} build {#BuildNumber}
AppPublisher={#PublisherName}
AppCopyright=Copyright (C) {#PublisherName}
DefaultDirName={autopf64}\DEEPAi\ServiceDirectory
AllowNetworkDrive=no
AllowRootDirectory=no
AllowUNCPath=no
DisableDirPage=yes
DisableProgramGroupPage=yes
UninstallDisplayName={#ProductName}
UninstallDisplayIcon={app}\{#TrayExecutable}
OutputDir={#OutputDirectory}
OutputBaseFilename={#OutputFileName}
ArchitecturesAllowed=x64
ArchitecturesInstallIn64BitMode=x64
MinVersion=10.0.14393
PrivilegesRequired=admin
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
RestartApplications=no
RestartIfNeededByRun=no
SetupLogging=yes
UsePreviousAppDir=no
VersionInfoVersion={#ProductVersion}.{#BuildNumber}
VersionInfoCompany={#PublisherName}
VersionInfoDescription={#ProductName} installer
VersionInfoProductName={#ProductName}
VersionInfoProductVersion={#ProductVersion}

[Files]
Source: "{#PayloadDirectory}\application\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#PayloadDirectory}\notices\*"; DestDir: "{app}\notices"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "scripts\ServiceDirectory.Setup.ps1"; Flags: dontcopy
Source: "scripts\ServiceDirectory.Network.ps1"; Flags: dontcopy
Source: "scripts\ServiceDirectory.InstallState.ps1"; Flags: dontcopy
Source: "scripts\ServiceDirectory.FileSystemSecurity.ps1"; Flags: dontcopy
Source: "scripts\ServiceDirectory.Setup.ps1"; DestDir: "{app}\installer-support"; Flags: ignoreversion
Source: "scripts\ServiceDirectory.Network.ps1"; DestDir: "{app}\installer-support"; Flags: ignoreversion
Source: "scripts\ServiceDirectory.InstallState.ps1"; DestDir: "{app}\installer-support"; Flags: ignoreversion
Source: "scripts\ServiceDirectory.FileSystemSecurity.ps1"; DestDir: "{app}\installer-support"; Flags: ignoreversion

[Code]
const
  MinimumServerBuild = 14393;
  MinimumClientBuild = 17763;
  MinimumWindows11Build = 26100;
  MinimumDotNet48Release = 528040;
  HelperScriptName = 'ServiceDirectory.Setup.ps1';
  NetworkScriptName = 'ServiceDirectory.Network.ps1';
  InstallStateScriptName = 'ServiceDirectory.InstallState.ps1';
  FileSystemSecurityScriptName = 'ServiceDirectory.FileSystemSecurity.ps1';
  SetupStateFileName = 'deepai-service-directory-setup-state.json';

var
  AddressPage: TInputOptionWizardPage;
  CaRestorePage: TInputFileWizardPage;
  EligibleAddresses: TArrayOfString;
  SelectedListenAddress: String;
  SelectedCaRestorePath: String;
  ExistingInstallation: Boolean;
  PurgeDataOnUninstall: Boolean;
  ServicesPrepared: Boolean;
  PostInstallStarted: Boolean;
  ConfigurationCompleted: Boolean;
  LastPowerShellHelperError: String;

function IsAllowedClientEdition(const EditionId: String): Boolean;
begin
  Result :=
    (CompareText(EditionId, 'Professional') = 0) or
    (CompareText(EditionId, 'Enterprise') = 0) or
    (CompareText(EditionId, 'EnterpriseS') = 0) or
    (CompareText(EditionId, 'IoTEnterprise') = 0) or
    (CompareText(EditionId, 'IoTEnterpriseS') = 0);
end;

function IsAllowedServerEdition(const EditionId: String): Boolean;
begin
  Result :=
    (CompareText(EditionId, 'ServerStandard') = 0) or
    (CompareText(EditionId, 'ServerDatacenter') = 0);
end;

function ValidateSupportedOperatingSystem(var ErrorText: String): Boolean;
var
  InstallationType: String;
  EditionId: String;
  BuildText: String;
  ProcessorArchitecture: String;
  BuildNumber: Integer;
begin
  Result := False;
  if not IsWin64 then
  begin
    ErrorText := 'This product requires a 64-bit Windows installation.';
    Exit;
  end;

  ProcessorArchitecture := GetEnv('PROCESSOR_ARCHITEW6432');
  if ProcessorArchitecture = '' then
    ProcessorArchitecture := GetEnv('PROCESSOR_ARCHITECTURE');
  if CompareText(ProcessorArchitecture, 'AMD64') <> 0 then
  begin
    ErrorText := 'This product supports only the x64 (AMD64) Windows architecture.';
    Exit;
  end;

  if not RegQueryStringValue(
      HKLM64,
      'SOFTWARE\Microsoft\Windows NT\CurrentVersion',
      'InstallationType',
      InstallationType) or
    not RegQueryStringValue(
      HKLM64,
      'SOFTWARE\Microsoft\Windows NT\CurrentVersion',
      'EditionID',
      EditionId) or
    not RegQueryStringValue(
      HKLM64,
      'SOFTWARE\Microsoft\Windows NT\CurrentVersion',
      'CurrentBuildNumber',
      BuildText) then
  begin
    ErrorText := 'Windows edition and build information could not be read.';
    Exit;
  end;

  BuildNumber := StrToIntDef(BuildText, -1);

  if CompareText(InstallationType, 'Server Core') = 0 then
  begin
    ErrorText := 'Windows Server Core is not supported. Desktop Experience is required.';
    Exit;
  end;

  if CompareText(InstallationType, 'Server') = 0 then
  begin
    if BuildNumber < MinimumServerBuild then
    begin
      ErrorText := 'Windows Server 2016 or later is required for server installations.';
      Exit;
    end;

    if not IsAllowedServerEdition(EditionId) then
    begin
      ErrorText := 'Only Windows Server Standard and Datacenter editions are supported.';
      Exit;
    end;
  end
  else if CompareText(InstallationType, 'Client') = 0 then
  begin
    if BuildNumber < MinimumClientBuild then
    begin
      ErrorText := 'Windows 10 1809 or later is required for client installations.';
      Exit;
    end;

    if not IsAllowedClientEdition(EditionId) then
    begin
      ErrorText := 'Only Windows Pro, Enterprise and IoT Enterprise editions are supported.';
      Exit;
    end;

    if (BuildNumber >= 22000) and (BuildNumber < MinimumWindows11Build) then
    begin
      ErrorText := 'Windows 11 24H2 or later is required for Windows 11 installations.';
      Exit;
    end;
  end
  else
  begin
    ErrorText := 'This Windows installation type is not supported.';
    Exit;
  end;

  Result := True;
end;

function ValidateDotNetFramework(var ErrorText: String): Boolean;
var
  ReleaseValue: Cardinal;
begin
  Result := RegQueryDWordValue(
    HKLM64,
    'SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full',
    'Release',
    ReleaseValue) and
    (ReleaseValue >= MinimumDotNet48Release);
  if not Result then
    ErrorText := 'Microsoft .NET Framework 4.8 or later must be installed before setup.';
end;

function InitializeSetup(): Boolean;
var
  ErrorText: String;
begin
  Result := ValidateSupportedOperatingSystem(ErrorText) and
    ValidateDotNetFramework(ErrorText);
  if not Result then
    SuppressibleMsgBox(ErrorText, mbCriticalError, MB_OK, IDOK);
end;

function ContainsOnlyAddressCharacters(const Value: String): Boolean;
var
  Index: Integer;
  Current: Char;
begin
  Result := Value <> '';
  for Index := 1 to Length(Value) do
  begin
    Current := Value[Index];
    if not (((Current >= '0') and (Current <= '9')) or
      ((Current >= 'a') and (Current <= 'f')) or
      (Current = ':') or (Current = '.')) then
    begin
      Result := False;
      Exit;
    end;
  end;
end;

function PowerShellPath(): String;
begin
  Result := ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe');
end;

function TemporaryHelperPath(): String;
begin
  Result := ExpandConstant('{tmp}\') + HelperScriptName;
end;

function InstalledHelperPath(): String;
begin
  Result := ExpandConstant('{app}\installer-support\') + HelperScriptName;
end;

function SetupStatePath(): String;
begin
  Result := ExpandConstant('{tmp}\') + SetupStateFileName;
end;

function QuoteArgument(const Value: String): String;
begin
  Result := '"' + Value + '"';
end;

procedure CapturePowerShellHelperOutput(
  const S: String;
  const Error, FirstLine: Boolean);
var
  CleanLine: String;
begin
  CleanLine := Trim(S);
  if CleanLine = '' then
    Exit;

  Log('PowerShell helper output: ' + CleanLine);
  { ExecAndLogOutput's Error flag reports capture failure, not stderr. The
    first helper line contains PowerShell's terminating-error summary. }
  if LastPowerShellHelperError = '' then
    LastPowerShellHelperError := Copy(CleanLine, 1, 512);
end;

function WithPowerShellHelperError(const MessageText: String): String;
begin
  Result := MessageText;
  if LastPowerShellHelperError <> '' then
    Result := Result + #13#10 + 'Details: ' + LastPowerShellHelperError;
end;

function RunPowerShellHelper(
  const HelperPath: String;
  const Arguments: String;
  var ResultCode: Integer): Boolean;
var
  Parameters: String;
begin
  Parameters := '-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File ' +
    QuoteArgument(HelperPath) + ' ' + Arguments;
  LastPowerShellHelperError := '';
  try
    Result := ExecAndLogOutput(
      PowerShellPath(),
      Parameters,
      '',
      SW_HIDE,
      ewWaitUntilTerminated,
      ResultCode,
      @CapturePowerShellHelperOutput);
  except
    LastPowerShellHelperError := Copy(GetExceptionMessage, 1, 512);
    Log('PowerShell helper execution failed: ' + LastPowerShellHelperError);
    Result := False;
  end;
end;

function RunPowerShellHelperVisible(
  const HelperPath: String;
  const Arguments: String;
  var ResultCode: Integer): Boolean;
var
  Parameters: String;
begin
  Parameters := '-NoLogo -NoProfile -ExecutionPolicy Bypass -File ' +
    QuoteArgument(HelperPath) + ' ' + Arguments;
  LastPowerShellHelperError := '';
  Result := Exec(
    PowerShellPath(),
    Parameters,
    '',
    SW_SHOW,
    ewWaitUntilTerminated,
    ResultCode);
end;

function LoadEligibleAddresses(var Addresses: TArrayOfString): Boolean;
var
  ResultCode: Integer;
  OutputPath: String;
begin
  OutputPath := ExpandConstant('{tmp}\eligible-listen-addresses.txt');
  DeleteFile(OutputPath);
  Result := RunPowerShellHelper(
    TemporaryHelperPath(),
    '-Mode ListAddresses -OutputPath ' + QuoteArgument(OutputPath),
    ResultCode) and (ResultCode = 0) and
    LoadStringsFromFile(OutputPath, Addresses) and
    (GetArrayLength(Addresses) > 0);
end;

function LoadExistingAddress(var ExistingAddress: String): Boolean;
var
  Lines: TArrayOfString;
  ResultCode: Integer;
  OutputPath: String;
begin
  ExistingAddress := '';
  OutputPath := ExpandConstant('{tmp}\existing-listen-address.txt');
  DeleteFile(OutputPath);
  Result := RunPowerShellHelper(
    TemporaryHelperPath(),
    '-Mode ReadAddress -DataRoot ' + QuoteArgument(ExpandConstant('{commonappdata}\DEEPAi\ServiceDirectory')) +
      ' -OutputPath ' + QuoteArgument(OutputPath),
    ResultCode) and (ResultCode = 0) and
    LoadStringsFromFile(OutputPath, Lines);
  if Result and (GetArrayLength(Lines) > 0) then
    ExistingAddress := Lines[0];
end;

procedure InitializeWizard();
var
  ExistingAddress: String;
  Index: Integer;
  SelectedIndex: Integer;
begin
  ExtractTemporaryFile(NetworkScriptName);
  ExtractTemporaryFile(InstallStateScriptName);
  ExtractTemporaryFile(FileSystemSecurityScriptName);
  ExtractTemporaryFile(HelperScriptName);
  WizardForm.DirEdit.Text := ExpandConstant('{autopf64}\DEEPAi\ServiceDirectory');
  if not LoadEligibleAddresses(EligibleAddresses) then
    RaiseException(
      'No active Domain or Private network interface has a supported IPv4 or IPv6 address.');

  AddressPage := CreateInputOptionPage(
    wpSelectDir,
    'Service listener address',
    'Select the exact IP literal used by External and Peer HTTP endpoints.',
    'Only addresses assigned to active Domain or Private interfaces are listed. ' +
      'Wildcard, loopback, link-local, multicast and Public-profile addresses are rejected.',
    True,
    True);
  SelectedIndex := 0;
  LoadExistingAddress(ExistingAddress);
  ExistingInstallation := ExistingAddress <> '';
  for Index := 0 to GetArrayLength(EligibleAddresses) - 1 do
  begin
    AddressPage.Add(EligibleAddresses[Index]);
    if CompareStr(EligibleAddresses[Index], ExistingAddress) = 0 then
      SelectedIndex := Index;
  end;
  AddressPage.SelectedValueIndex := SelectedIndex;

  CaRestorePage := CreateInputFilePage(
    AddressPage.ID,
    'Site CA restore',
    'Optionally restore an encrypted Site CA backup during repair.',
    'Leave this empty unless this is a repair and the existing Site CA state must be restored.');
  CaRestorePage.Add(
    'Encrypted CA backup:',
    'DEEPAi CA backup files (*.dpca)|*.dpca',
    '.dpca');

  if WizardSilent then
  begin
    SelectedListenAddress := ExpandConstant('{param:ListenAddress|}');
    if SelectedListenAddress = '' then
      RaiseException('Silent setup requires an explicit /ListenAddress=IP argument.');
    if ExpandConstant('{param:CaRestorePath|}') <> '' then
      RaiseException('Silent Site CA restore is not supported because the backup password must be entered through standard input.');
  end;
end;

function ShouldSkipPage(PageID: Integer): Boolean;
begin
  Result := (PageID = CaRestorePage.ID) and not ExistingInstallation;
end;

function ValidateSelectedAddress(const Value: String): Boolean;
var
  ResultCode: Integer;
begin
  Result := ContainsOnlyAddressCharacters(Value) and
    RunPowerShellHelper(
      TemporaryHelperPath(),
      '-Mode ValidateAddress -Address ' + QuoteArgument(Value),
      ResultCode) and
    (ResultCode = 0);
end;

function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := True;
  if CurPageID = AddressPage.ID then
  begin
    if not WizardSilent then
      SelectedListenAddress := EligibleAddresses[AddressPage.SelectedValueIndex];
    Result := ValidateSelectedAddress(SelectedListenAddress);
    if not Result then
      MsgBox(
        'The selected address is no longer a canonical address on an active Domain or Private interface.',
        mbError,
        MB_OK);
  end;
  if Result and (CurPageID = CaRestorePage.ID) then
  begin
    SelectedCaRestorePath := CaRestorePage.Values[0];
    if SelectedCaRestorePath <> '' then
    begin
      Result := FileExists(SelectedCaRestorePath) and
        (CompareStr(ExtractFileExt(SelectedCaRestorePath), '.dpca') = 0) and
        (ExtractFileDrive(SelectedCaRestorePath) <> '') and
        (Copy(SelectedCaRestorePath, 1, 2) <> '\\');
      if not Result then
        MsgBox(
          'Select an existing local file with the exact .dpca extension.',
          mbError,
          MB_OK);
    end;
  end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ResultCode: Integer;
begin
  Result := '';
  if CompareText(
      ExpandConstant('{app}'),
      ExpandConstant('{autopf64}\DEEPAi\ServiceDirectory')) <> 0 then
  begin
    Result := 'The installation directory must remain under %ProgramFiles%\DEEPAi\ServiceDirectory.';
    Exit;
  end;

  if SelectedListenAddress = '' then
  begin
    if WizardSilent then
      SelectedListenAddress := ExpandConstant('{param:ListenAddress|}')
    else
      SelectedListenAddress := EligibleAddresses[AddressPage.SelectedValueIndex];
  end;

  if not ValidateSelectedAddress(SelectedListenAddress) then
  begin
    Result := 'ListenAddress validation failed. Setup did not change the installation.';
    Exit;
  end;

  if not RunPowerShellHelper(
      TemporaryHelperPath(),
      '-Mode Prepare -Address ' + QuoteArgument(SelectedListenAddress) +
        ' -InstallRoot ' + QuoteArgument(ExpandConstant('{app}')) +
        ' -DataRoot ' + QuoteArgument(ExpandConstant('{commonappdata}\DEEPAi\ServiceDirectory')) +
        ' -SetupStatePath ' + QuoteArgument(SetupStatePath()),
      ResultCode) or (ResultCode <> 0) then
    Result := WithPowerShellHelperError(
      'Setup ownership preflight or safe service stop failed. No installation files were changed.')
  else
    ServicesPrepared := True;
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  Arguments: String;
  ResultCode: Integer;
begin
  if CurStep <> ssPostInstall then
    Exit;

  PostInstallStarted := True;
  Arguments := '-Mode Install -Address ' + QuoteArgument(SelectedListenAddress) +
    ' -InstallRoot ' + QuoteArgument(ExpandConstant('{app}')) +
    ' -DataRoot ' + QuoteArgument(ExpandConstant('{commonappdata}\DEEPAi\ServiceDirectory')) +
    ' -SetupStatePath ' + QuoteArgument(SetupStatePath());
  if SelectedCaRestorePath <> '' then
    Arguments := Arguments + ' -CaRestorePath ' +
      QuoteArgument(SelectedCaRestorePath);
  if SelectedCaRestorePath <> '' then
    ConfigurationCompleted := RunPowerShellHelperVisible(
      TemporaryHelperPath(), Arguments, ResultCode) and (ResultCode = 0)
  else
    ConfigurationCompleted := RunPowerShellHelper(
      TemporaryHelperPath(), Arguments, ResultCode) and (ResultCode = 0);
  if not ConfigurationCompleted then
    RaiseException(
      WithPowerShellHelperError(
        'Service configuration failed. Managed system changes were rolled back where possible, ' +
          'and the services remain stopped. Resolve the reported cause and run repair.'));
  DeleteFile(SetupStatePath());
end;

procedure DeinitializeSetup();
var
  ResultCode: Integer;
begin
  if ServicesPrepared and not ConfigurationCompleted and not PostInstallStarted then
  begin
    if not RunPowerShellHelper(
        TemporaryHelperPath(),
        '-Mode Resume' +
          ' -InstallRoot ' + QuoteArgument(ExpandConstant('{app}')) +
          ' -DataRoot ' + QuoteArgument(ExpandConstant('{commonappdata}\DEEPAi\ServiceDirectory')) +
          ' -SetupStatePath ' + QuoteArgument(SetupStatePath()),
        ResultCode) or (ResultCode <> 0) then
      Log('Service resume was skipped or failed; affected services remain stopped.');
  end;
end;

function InitializeUninstall(): Boolean;
var
  FirstConfirmation: Integer;
  SecondConfirmation: Integer;
begin
  Result := True;
  PurgeDataOnUninstall := False;
  if UninstallSilent then
  begin
    PurgeDataOnUninstall :=
      CompareText(ExpandConstant('{param:PurgeData|0}'), '1') = 0;
    Exit;
  end;

  FirstConfirmation := MsgBox(
    'Keep operational data, configuration, logs, backups and peer credentials?' + #13#10 +
      'Choose Yes for the normal recoverable uninstall. Choose No only to request permanent deletion.',
    mbConfirmation,
    MB_YESNO or MB_DEFBUTTON1);
  if FirstConfirmation = IDNO then
  begin
    SecondConfirmation := MsgBox(
      'Permanently delete %ProgramData%\DEEPAi\ServiceDirectory and all peer credentials?' + #13#10 +
        'This operation cannot be recovered and reinstalling will require peer re-pairing.',
      mbCriticalError,
      MB_YESNO or MB_DEFBUTTON2);
    PurgeDataOnUninstall := SecondConfirmation = IDYES;
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Arguments: String;
  ResultCode: Integer;
begin
  if CurUninstallStep <> usUninstall then
    Exit;

  Arguments := '-Mode Uninstall -InstallRoot ' +
    QuoteArgument(ExpandConstant('{app}')) +
    ' -DataRoot ' +
    QuoteArgument(ExpandConstant('{commonappdata}\DEEPAi\ServiceDirectory'));
  if PurgeDataOnUninstall then
    Arguments := Arguments + ' -PurgeData';

  if not RunPowerShellHelper(
      InstalledHelperPath(),
      Arguments,
      ResultCode) or (ResultCode <> 0) then
    RaiseException(WithPowerShellHelperError(
      'Windows Service, URL ACL or firewall cleanup failed.'));
end;
