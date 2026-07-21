$installerOwnerId = 'B44C6547-15D5-421A-88D7-3D2293BEE48C'
$firewallOwnerDescription =
    "Managed by DEEPAi Service Directory installer ($installerOwnerId)."
$firewallRuleGroup = 'DEEPAi Service Directory'
$operatorsGroupDescription =
    'Operators authorized to administer DEEPAi Service Directory.'
$trayRunRegistryPath =
    'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run'
$trayRunValueName = 'DEEPAi.ServiceDirectory.Tray'

function Get-OperatorsGroupSnapshot {
    $path = "WinNT://$env:COMPUTERNAME/$operatorsGroupName,group"
    if (-not [ADSI]::Exists($path)) {
        return [pscustomobject]@{ Exists = $false; Owned = $false }
    }

    $existingGroup = [ADSI]$path
    $description = [string]$existingGroup.InvokeGet('Description')
    return [pscustomobject]@{
        Exists = $true
        Owned = [StringComparer]::Ordinal.Equals(
            $description,
            $operatorsGroupDescription)
    }
}

function Assert-OperatorsGroupCanBeManaged {
    param([Parameter(Mandatory = $true)][object]$Snapshot)

    if ($Snapshot.Exists -and -not $Snapshot.Owned) {
        throw "Local group '$operatorsGroupName' is not owned by this installation."
    }
}

function Ensure-OperatorsGroup {
    $snapshot = Get-OperatorsGroupSnapshot
    Assert-OperatorsGroupCanBeManaged -Snapshot $snapshot
    if ($snapshot.Exists) {
        return $false
    }

    $computer = [ADSI]"WinNT://$env:COMPUTERNAME,computer"
    $group = $computer.Create('group', $operatorsGroupName)
    $group.Put('Description', $operatorsGroupDescription)
    $group.SetInfo()
    $verified = Get-OperatorsGroupSnapshot
    if (-not $verified.Exists -or -not $verified.Owned) {
        throw "Owned local group '$operatorsGroupName' could not be verified."
    }
    return $true
}

function Remove-OperatorsGroup {
    $snapshot = Get-OperatorsGroupSnapshot
    Assert-OperatorsGroupCanBeManaged -Snapshot $snapshot
    if (-not $snapshot.Exists) {
        return
    }

    $computer = [ADSI]"WinNT://$env:COMPUTERNAME,computer"
    $computer.Delete('group', $operatorsGroupName)
    if ((Get-OperatorsGroupSnapshot).Exists) {
        throw "Owned local group '$operatorsGroupName' was not removed."
    }
}

function Assert-InstallResourceOwnership {
    param(
        [Parameter(Mandatory = $true)][string]$RequestedDataRoot,
        [Parameter(Mandatory = $true)][string]$NewAddress,
        [Parameter(Mandatory = $true)][string]$NewHostName
    )

    $NewAddress = Assert-AddressIsEligible -Value $NewAddress
    $NewHostName = ConvertTo-CanonicalDirectoryHostName -Value $NewHostName
    $configPath = Join-Path $RequestedDataRoot 'config.xml'
    $oldIdentity = Read-ConfigurationIdentity -ConfigPath $configPath
    $oldAddress = if ($null -eq $oldIdentity) {
        $null
    } else {
        [string]$oldIdentity.Address
    }
    $productSnapshot = Get-ProductRegistrationSnapshot
    if ($null -eq $oldAddress `
        -and $productSnapshot.Exists `
        -and $null -ne $productSnapshot.Address) {
        $oldAddress = [string]$productSnapshot.Address
    }
    elseif ($null -ne $oldAddress `
        -and $productSnapshot.Exists `
        -and $null -ne $productSnapshot.Address `
        -and -not [StringComparer]::Ordinal.Equals(
            $oldAddress,
            [string]$productSnapshot.Address)) {
        throw 'config.xml and the owned product registration disagree on ListenAddress.'
    }

    $eventSourceSnapshot = Get-EventSourceSnapshot
    if ($eventSourceSnapshot.Exists `
        -and (-not $eventSourceSnapshot.Owned `
            -or (-not $eventSourceSnapshot.Valid `
                -and -not $eventSourceSnapshot.Recoverable))) {
        throw 'The security Event Log source registration is foreign or conflicting.'
    }
    $firewallSnapshot = Get-FirewallRuleSnapshot
    Assert-FirewallRuleCanBeManaged -Snapshot $firewallSnapshot
    if ($firewallSnapshot.Exists -and -not $firewallSnapshot.Valid) {
        throw 'The owned firewall rule is not in a valid managed state.'
    }
    $groupSnapshot = Get-OperatorsGroupSnapshot
    Assert-OperatorsGroupCanBeManaged -Snapshot $groupSnapshot

    $prefixes = New-Object 'System.Collections.Generic.HashSet[string]' `
        ([StringComparer]::OrdinalIgnoreCase)
    $addresses = New-Object 'System.Collections.Generic.HashSet[string]' `
        ([StringComparer]::Ordinal)
    if ($null -ne $oldAddress) {
        [void]$addresses.Add($oldAddress)
    }
    [void]$addresses.Add($NewAddress)
    foreach ($address in $addresses) {
        [void]$prefixes.Add((Get-HttpPrefix -Value $address))
        [void]$prefixes.Add((Get-RemoteHttpsPrefix -Address $address))
        Assert-HttpsBindingCanBeManaged `
            -Snapshot (Get-HttpsBindingSnapshot -Address $address)
    }
    if ($null -ne $oldIdentity) {
        [void]$prefixes.Add((Get-RemoteHttpsHostNamePrefix `
                -HostName ([string]$oldIdentity.HostName)))
    }
    [void]$prefixes.Add((Get-RemoteHttpsHostNamePrefix `
            -HostName $NewHostName))
    [void]$prefixes.Add("http://127.0.0.1:$servicePort/")
    foreach ($prefix in $prefixes) {
        Assert-UrlAclCanBeManaged `
            -Snapshot (Get-UrlAclSnapshot -Prefix $prefix)
    }
}

function Get-OptionalRegistryValue {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Name
    )

    if (-not (Test-Path -LiteralPath $Path)) {
        return [pscustomobject]@{
            Exists = $false
            Value = $null
            Kind = $null
        }
    }
    $key = Get-Item -LiteralPath $Path -ErrorAction Stop
    try {
        if (@($key.GetValueNames()) -notcontains $Name) {
            return [pscustomobject]@{
                Exists = $false
                Value = $null
                Kind = $null
            }
        }
        $value = $key.GetValue(
            $Name,
            $null,
            [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
        return [pscustomobject]@{
            Exists = $true
            Value = $value
            Kind = $key.GetValueKind($Name)
        }
    }
    finally {
        $key.Dispose()
    }
}

function Set-OptionalRegistryValue {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][object]$Snapshot,
        [Parameter(Mandatory = $true)]
        [Microsoft.Win32.RegistryValueKind]$Kind
    )

    if ($Snapshot.Exists) {
        [void](New-Item -Path $Path -Force)
        [void](New-ItemProperty -LiteralPath $Path -Name $Name `
            -Value $Snapshot.Value -PropertyType $Kind -Force)
    }
    elseif (Test-Path -LiteralPath $Path) {
        Remove-ItemProperty -LiteralPath $Path -Name $Name `
            -ErrorAction SilentlyContinue
    }
}

function Get-ExpectedTrayRunValue {
    param([Parameter(Mandatory = $true)][string]$InstallRoot)

    return '"' + (Join-Path $InstallRoot `
        'DEEPAi.ServiceDirectory.Tray.exe') + '"'
}

function Get-TrayRunValueSnapshot {
    param([Parameter(Mandatory = $true)][string]$InstallRoot)

    $value = Get-OptionalRegistryValue -Path $trayRunRegistryPath `
        -Name $trayRunValueName
    if (-not $value.Exists) {
        return [pscustomobject]@{
            Exists = $false
            Owned = $false
            Value = $null
        }
    }
    return [pscustomobject]@{
        Exists = $true
        Owned = [StringComparer]::OrdinalIgnoreCase.Equals(
            [string]$value.Value,
            (Get-ExpectedTrayRunValue -InstallRoot $InstallRoot))
        Value = [string]$value.Value
    }
}

function Assert-TrayRunValueCanBeManaged {
    param([Parameter(Mandatory = $true)][object]$Snapshot)

    if ($Snapshot.Exists -and -not $Snapshot.Owned) {
        throw "Run value '$trayRunValueName' is owned by another application."
    }
}

function Set-OwnedTrayRunValue {
    param([Parameter(Mandatory = $true)][string]$InstallRoot)

    $snapshot = Get-TrayRunValueSnapshot -InstallRoot $InstallRoot
    Assert-TrayRunValueCanBeManaged -Snapshot $snapshot
    [void](New-ItemProperty -LiteralPath $trayRunRegistryPath `
        -Name $trayRunValueName `
        -Value (Get-ExpectedTrayRunValue -InstallRoot $InstallRoot) `
        -PropertyType String -Force)
    $verified = Get-TrayRunValueSnapshot -InstallRoot $InstallRoot
    if (-not $verified.Exists -or -not $verified.Owned) {
        throw "Owned Run value '$trayRunValueName' could not be verified."
    }
}

function Remove-OwnedTrayRunValue {
    param([Parameter(Mandatory = $true)][string]$InstallRoot)

    $snapshot = Get-TrayRunValueSnapshot -InstallRoot $InstallRoot
    Assert-TrayRunValueCanBeManaged -Snapshot $snapshot
    if ($snapshot.Exists) {
        Remove-ItemProperty -LiteralPath $trayRunRegistryPath `
            -Name $trayRunValueName -ErrorAction Stop
        if ((Get-TrayRunValueSnapshot -InstallRoot $InstallRoot).Exists) {
            throw "Owned Run value '$trayRunValueName' was not removed."
        }
    }
}

function Restore-TrayRunValueSnapshot {
    param(
        [Parameter(Mandatory = $true)][object]$Snapshot,
        [Parameter(Mandatory = $true)][string]$InstallRoot
    )

    if ($Snapshot.Exists) {
        if (-not $Snapshot.Owned) {
            throw 'The tray Run value rollback snapshot is not owned.'
        }
        $current = Get-TrayRunValueSnapshot -InstallRoot $InstallRoot
        Assert-TrayRunValueCanBeManaged -Snapshot $current
        [void](New-ItemProperty -LiteralPath $trayRunRegistryPath `
            -Name $trayRunValueName -Value ([string]$Snapshot.Value) `
            -PropertyType String -Force)
        $verified = Get-TrayRunValueSnapshot -InstallRoot $InstallRoot
        if (-not $verified.Exists -or -not $verified.Owned) {
            throw "Run value '$trayRunValueName' rollback verification failed."
        }
    }
    else {
        Remove-OwnedTrayRunValue -InstallRoot $InstallRoot
    }
}

function Get-EventSourceSnapshot {
    if (-not (Test-Path -LiteralPath $eventSourceRegistryPath)) {
        return [pscustomobject]@{
            Exists = $false
            Owned = $false
            Valid = $false
            Recoverable = $false
        }
    }

    $expectedMessageFile =
        '%SystemRoot%\Microsoft.NET\Framework64\v4.0.30319\EventLogMessages.dll'
    $key = Get-Item -LiteralPath $eventSourceRegistryPath -ErrorAction Stop
    try {
        $unknownValueNames = @($key.GetValueNames() | Where-Object {
                $_ -notin @(
                    'InstallerOwnerId',
                    'EventMessageFile',
                    'TypesSupported')
            })
    }
    finally {
        $key.Dispose()
    }
    $owner = Get-OptionalRegistryValue -Path $eventSourceRegistryPath `
        -Name 'InstallerOwnerId'
    $messageFile = Get-OptionalRegistryValue -Path $eventSourceRegistryPath `
        -Name 'EventMessageFile'
    $types = Get-OptionalRegistryValue -Path $eventSourceRegistryPath `
        -Name 'TypesSupported'
    $owned = $owner.Exists `
        -and $owner.Kind -eq [Microsoft.Win32.RegistryValueKind]::String `
        -and [StringComparer]::Ordinal.Equals(
            [string]$owner.Value,
            $installerOwnerId)
    $messageFileCompatible = -not $messageFile.Exists `
        -or ($messageFile.Kind `
            -eq [Microsoft.Win32.RegistryValueKind]::ExpandString `
            -and [StringComparer]::Ordinal.Equals(
                [string]$messageFile.Value,
                $expectedMessageFile))
    $typesCompatible = -not $types.Exists `
        -or ($types.Kind -eq [Microsoft.Win32.RegistryValueKind]::DWord `
            -and [int]$types.Value -eq 7)
    $valid = $owned `
        -and $unknownValueNames.Count -eq 0 `
        -and $messageFile.Exists `
        -and $types.Exists `
        -and $messageFileCompatible `
        -and $typesCompatible
    $recoverable = $owned `
        -and $unknownValueNames.Count -eq 0 `
        -and $messageFileCompatible `
        -and $typesCompatible `
        -and -not $valid
    return [pscustomobject]@{
        Exists = $true
        Owned = $owned
        Valid = $valid
        Recoverable = $recoverable
    }
}

function Set-EventSource {
    $snapshot = Get-EventSourceSnapshot
    if ($snapshot.Exists -and -not $snapshot.Owned) {
        throw 'The security Event Log source key is owned by another registration.'
    }
    if ($snapshot.Exists `
        -and -not $snapshot.Valid `
        -and -not $snapshot.Recoverable) {
        throw 'The owned security Event Log source registration contains conflicting values.'
    }
    if ($snapshot.Valid) {
        return
    }

    [void](New-Item -Path $eventSourceRegistryPath -Force)
    [void](New-ItemProperty `
        -Path $eventSourceRegistryPath `
        -Name 'InstallerOwnerId' `
        -Value $installerOwnerId `
        -PropertyType String `
        -Force)
    [void](New-ItemProperty `
        -Path $eventSourceRegistryPath `
        -Name 'EventMessageFile' `
        -Value '%SystemRoot%\Microsoft.NET\Framework64\v4.0.30319\EventLogMessages.dll' `
        -PropertyType ExpandString `
        -Force)
    [void](New-ItemProperty `
        -Path $eventSourceRegistryPath `
        -Name 'TypesSupported' `
        -Value 7 `
        -PropertyType DWord `
        -Force)
    $verified = Get-EventSourceSnapshot
    if (-not $verified.Exists -or -not $verified.Owned `
        -or -not $verified.Valid) {
        throw 'The owned security Event Log source registration could not be verified.'
    }
}

function Remove-OwnedEventSource {
    $snapshot = Get-EventSourceSnapshot
    if (-not $snapshot.Exists) {
        return
    }
    if (-not $snapshot.Owned) {
        throw 'The security Event Log source key is not owned by this installation.'
    }
    if (-not $snapshot.Valid -and -not $snapshot.Recoverable) {
        throw 'The owned security Event Log source registration contains conflicting values.'
    }
    Remove-Item -LiteralPath $eventSourceRegistryPath -Recurse -Force
    if (Test-Path -LiteralPath $eventSourceRegistryPath) {
        throw 'The owned security Event Log source key was not removed.'
    }
}

function Set-ProductRegistration {
    param([Parameter(Mandatory = $true)][string]$NewAddress)

    if (Test-Path -LiteralPath $productRegistryPath) {
        [void](Get-ProductRegistrationSnapshot)
    }
    [void](New-Item -Path $productRegistryPath -Force)
    [void](New-ItemProperty `
        -Path $productRegistryPath `
        -Name 'InstallerOwnerId' `
        -Value $installerOwnerId `
        -PropertyType String `
        -Force)
    [void](New-ItemProperty `
        -Path $productRegistryPath `
        -Name 'ListenAddress' `
        -Value $NewAddress `
        -PropertyType String `
        -Force)
    $verified = Get-ProductRegistrationSnapshot
    if (-not $verified.Exists `
        -or -not $verified.Owned `
        -or $verified.Recoverable `
        -or $null -eq $verified.Address `
        -or -not [StringComparer]::Ordinal.Equals(
            [string]$verified.Address,
            $NewAddress)) {
        throw 'The owned product registration could not be verified.'
    }
}

function Get-RegisteredAddress {
    if (-not (Test-Path -LiteralPath $productRegistryPath)) {
        return $null
    }

    $owner = Get-OptionalRegistryValue -Path $productRegistryPath `
        -Name 'InstallerOwnerId'
    if (-not $owner.Exists `
        -or $owner.Kind -ne [Microsoft.Win32.RegistryValueKind]::String `
        -or -not [StringComparer]::Ordinal.Equals(
            [string]$owner.Value,
            $installerOwnerId)) {
        throw 'The product registration key is not owned by this installation.'
    }

    $registration = Get-OptionalRegistryValue -Path $productRegistryPath `
        -Name 'ListenAddress'
    if (-not $registration.Exists) {
        return $null
    }
    if ($registration.Kind -ne [Microsoft.Win32.RegistryValueKind]::String) {
        throw 'The owned product registration ListenAddress has an invalid registry type.'
    }
    if ([string]::IsNullOrEmpty([string]$registration.Value)) {
        throw 'The owned product registration ListenAddress is empty.'
    }
    return ConvertTo-CanonicalAddress -Value ([string]$registration.Value)
}

function Get-ProductRegistrationSnapshot {
    if (-not (Test-Path -LiteralPath $productRegistryPath)) {
        return [pscustomobject]@{
            Exists = $false
            Owned = $false
            Address = $null
            Recoverable = $false
        }
    }

    $address = Get-RegisteredAddress
    $key = Get-Item -LiteralPath $productRegistryPath -ErrorAction Stop
    try {
        $unknownValueNames = @($key.GetValueNames() | Where-Object {
                $_ -notin @('InstallerOwnerId', 'ListenAddress')
            })
    }
    finally {
        $key.Dispose()
    }
    if ($unknownValueNames.Count -ne 0) {
        throw 'The owned product registration contains unknown values.'
    }
    if ($null -eq $address) {
        return [pscustomobject]@{
            Exists = $true
            Owned = $true
            Address = $null
            Recoverable = $true
        }
    }
    return [pscustomobject]@{
        Exists = $true
        Owned = $true
        Address = $address
        Recoverable = $false
    }
}

function Remove-OwnedProductRegistration {
    if (-not (Test-Path -LiteralPath $productRegistryPath)) {
        return
    }
    [void](Get-ProductRegistrationSnapshot)
    Remove-Item -LiteralPath $productRegistryPath -Recurse -Force
    if (Test-Path -LiteralPath $productRegistryPath) {
        throw 'The owned product registration key was not removed.'
    }
}

function Restore-ProductRegistrationSnapshot {
    param([Parameter(Mandatory = $true)][object]$Snapshot)

    if ($Snapshot.Exists) {
        if (-not $Snapshot.Owned) {
            throw 'The product registration rollback snapshot is invalid.'
        }
        if ($Snapshot.Recoverable) {
            Remove-OwnedProductRegistration
            return
        }
        if ($null -eq $Snapshot.Address) {
            throw 'The product registration rollback snapshot has no address.'
        }
        Set-ProductRegistration -NewAddress ([string]$Snapshot.Address)
    }
    else {
        Remove-OwnedProductRegistration
    }
}

function Get-ServiceSnapshot {
    param([Parameter(Mandatory = $true)][string]$Name)

    $service = Get-StableServiceController -Name $Name
    if ($null -eq $service) {
        return [pscustomobject]@{
            Name = $Name
            Exists = $false
            WasRunning = $false
        }
    }
    $escapedName = $Name.Replace("'", "''")
    $configuration = Get-CimInstance -ClassName Win32_Service `
        -Filter "Name='$escapedName'" -ErrorAction Stop
    if ($null -eq $configuration) {
        throw "Windows Service '$Name' configuration could not be read."
    }
    $registryPath = "HKLM:\SYSTEM\CurrentControlSet\Services\$Name"
    $securityOutput = Invoke-NativeCommand `
        -FilePath "$env:SystemRoot\System32\sc.exe" `
        -Arguments @('sdshow', $Name)
    $sddl = $securityOutput | ForEach-Object { ([string]$_).Trim() } |
        Where-Object { $_ -match '^[OGDS]:' } |
        Select-Object -First 1
    if ([string]::IsNullOrWhiteSpace($sddl)) {
        throw "Windows Service '$Name' security descriptor could not be read."
    }

    return [pscustomobject]@{
        Name = $Name
        Exists = $true
        WasRunning = $service.Status -ne 'Stopped'
        DisplayName = [string]$configuration.DisplayName
        PathName = [string]$configuration.PathName
        StartMode = [string]$configuration.StartMode
        StartName = [string]$configuration.StartName
        ServiceType = [string]$configuration.ServiceType
        DelayedAutoStart = Get-OptionalRegistryValue `
            -Path $registryPath -Name 'DelayedAutoStart'
        FailureActions = Get-OptionalRegistryValue `
            -Path $registryPath -Name 'FailureActions'
        FailureFlag = Get-OptionalRegistryValue `
            -Path $registryPath -Name 'FailureActionsOnNonCrashFailures'
        ServiceSidType = Get-OptionalRegistryValue `
            -Path $registryPath -Name 'ServiceSidType'
        Sddl = $sddl.Trim()
    }
}

function Assert-OwnedServiceSnapshot {
    param(
        [Parameter(Mandatory = $true)][object]$Snapshot,
        [Parameter(Mandatory = $true)][string]$ExpectedExecutablePath
    )

    if (-not $Snapshot.Exists) {
        return
    }
    $expectedPath = '"' + $ExpectedExecutablePath + '"'
    if (-not [StringComparer]::OrdinalIgnoreCase.Equals(
            [string]$Snapshot.PathName,
            $expectedPath) `
        -or -not [StringComparer]::OrdinalIgnoreCase.Equals(
            [string]$Snapshot.StartName,
            "NT SERVICE\$($Snapshot.Name)") `
        -or -not [StringComparer]::OrdinalIgnoreCase.Equals(
            [string]$Snapshot.ServiceType,
            'Own Process') `
        -or @('Auto', 'Manual', 'Disabled') `
            -notcontains [string]$Snapshot.StartMode) {
        throw "Existing service '$($Snapshot.Name)' is not owned by this installation."
    }
}

function New-SetupStateFileSecurity {
    $administratorsSid = [System.Security.Principal.SecurityIdentifier]::new(
        'S-1-5-32-544')
    $systemSid = [System.Security.Principal.SecurityIdentifier]::new('S-1-5-18')
    $security = [System.Security.AccessControl.FileSecurity]::new()
    $security.SetOwner($administratorsSid)
    $security.SetAccessRuleProtection($true, $false)
    foreach ($sid in @($systemSid, $administratorsSid)) {
        [void]$security.AddAccessRule(
            [System.Security.AccessControl.FileSystemAccessRule]::new(
                $sid,
                [System.Security.AccessControl.FileSystemRights]::FullControl,
                [System.Security.AccessControl.AccessControlType]::Allow))
    }
    return $security
}

function Write-SetupState {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$InstallRoot,
        [Parameter(Mandatory = $true)][string]$DataRoot
    )

    $fullPath = [System.IO.Path]::GetFullPath($Path)
    $parent = [System.IO.Path]::GetDirectoryName($fullPath)
    Assert-NoReparsePoint -Path $parent
    if (Test-Path -LiteralPath $fullPath) {
        Assert-NoReparsePoint -Path $fullPath
        Remove-Item -LiteralPath $fullPath -Force
    }

    $mainExecutable = Join-Path $InstallRoot `
        'DEEPAi.ServiceDirectory.Service.exe'
    $watchdogExecutable = Join-Path $InstallRoot `
        'DEEPAi.ServiceDirectory.Watchdog.exe'
    $mainSnapshot = Get-ServiceSnapshot -Name $mainServiceName
    $watchdogSnapshot = Get-ServiceSnapshot -Name $watchdogServiceName
    Assert-OwnedServiceSnapshot -Snapshot $mainSnapshot `
        -ExpectedExecutablePath $mainExecutable
    Assert-OwnedServiceSnapshot -Snapshot $watchdogSnapshot `
        -ExpectedExecutablePath $watchdogExecutable
    $trayRunSnapshot = Get-TrayRunValueSnapshot -InstallRoot $InstallRoot
    Assert-TrayRunValueCanBeManaged -Snapshot $trayRunSnapshot
    $state = [pscustomobject]@{
        SchemaVersion = 1
        InstallRoot = $InstallRoot
        DataRoot = $DataRoot
        MainService = $mainSnapshot
        WatchdogService = $watchdogSnapshot
        TrayRunValue = $trayRunSnapshot
    }
    $bytes = [System.Text.UTF8Encoding]::new($false).GetBytes(
        ($state | ConvertTo-Json -Depth 8 -Compress))
    $security = New-SetupStateFileSecurity
    $stream = [System.IO.File]::Create(
        $fullPath,
        4096,
        [System.IO.FileOptions]::WriteThrough,
        $security)
    try {
        $stream.Write($bytes, 0, $bytes.Length)
        $stream.Flush($true)
    }
    finally {
        $stream.Dispose()
    }
    $actual = Get-Acl -LiteralPath $fullPath -ErrorAction Stop
    $sections = [System.Security.AccessControl.AccessControlSections]::Owner `
        -bor [System.Security.AccessControl.AccessControlSections]::Access
    if (-not $actual.AreAccessRulesProtected `
        -or -not [StringComparer]::Ordinal.Equals(
            $security.GetSecurityDescriptorSddlForm($sections),
            $actual.GetSecurityDescriptorSddlForm($sections))) {
        throw 'The setup state file exact DACL could not be verified.'
    }
}

function Read-SetupState {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$InstallRoot,
        [Parameter(Mandatory = $true)][string]$DataRoot
    )

    $fullPath = [System.IO.Path]::GetFullPath($Path)
    Assert-NoReparsePoint -Path $fullPath
    $security = Get-Acl -LiteralPath $fullPath -ErrorAction Stop
    $expected = New-SetupStateFileSecurity
    $sections = [System.Security.AccessControl.AccessControlSections]::Owner `
        -bor [System.Security.AccessControl.AccessControlSections]::Access
    if (-not $security.AreAccessRulesProtected `
        -or -not [StringComparer]::Ordinal.Equals(
            $expected.GetSecurityDescriptorSddlForm($sections),
            $security.GetSecurityDescriptorSddlForm($sections))) {
        throw 'The setup state file does not have the protected exact DACL.'
    }
    $state = [System.IO.File]::ReadAllText(
        $fullPath,
        [System.Text.Encoding]::UTF8) | ConvertFrom-Json
    if ([int]$state.SchemaVersion -ne 1 `
        -or -not [StringComparer]::OrdinalIgnoreCase.Equals(
            [string]$state.InstallRoot,
            $InstallRoot) `
        -or -not [StringComparer]::OrdinalIgnoreCase.Equals(
            [string]$state.DataRoot,
            $DataRoot) `
        -or -not [StringComparer]::Ordinal.Equals(
            [string]$state.MainService.Name,
            $mainServiceName) `
        -or -not [StringComparer]::Ordinal.Equals(
            [string]$state.WatchdogService.Name,
            $watchdogServiceName) `
        -or $null -eq $state.TrayRunValue) {
        throw 'The setup state file does not match this installation.'
    }
    return $state
}

function Restore-ServiceDefinitionSnapshot {
    param([Parameter(Mandatory = $true)][object]$Snapshot)

    if (-not $Snapshot.Exists) {
        if (Test-ServiceExists -Name $Snapshot.Name) {
            Remove-ServiceDefinition -Name $Snapshot.Name
        }
        return
    }
    if (-not (Test-ServiceExists -Name $Snapshot.Name)) {
        throw "Pre-existing service '$($Snapshot.Name)' disappeared during setup."
    }

    Stop-ServiceAndWait -Name $Snapshot.Name
    $startMode = switch ([string]$Snapshot.StartMode) {
        'Auto' {
            if ($Snapshot.DelayedAutoStart.Exists `
                -and [int]$Snapshot.DelayedAutoStart.Value -ne 0) {
                'delayed-auto'
            }
            else { 'auto' }
        }
        'Manual' { 'demand' }
        'Disabled' { 'disabled' }
        default {
            throw "Unsupported original StartMode '$($Snapshot.StartMode)'."
        }
    }
    [void](Invoke-NativeCommand -FilePath "$env:SystemRoot\System32\sc.exe" `
        -Arguments @(
            'config', [string]$Snapshot.Name,
            'binPath=', [string]$Snapshot.PathName,
            'start=', $startMode,
            'obj=', [string]$Snapshot.StartName,
            'DisplayName=', [string]$Snapshot.DisplayName))

    $sidType = if (-not $Snapshot.ServiceSidType.Exists) { 'none' }
        elseif ([int]$Snapshot.ServiceSidType.Value -eq 1) { 'unrestricted' }
        elseif ([int]$Snapshot.ServiceSidType.Value -eq 3) { 'restricted' }
        elseif ([int]$Snapshot.ServiceSidType.Value -eq 0) { 'none' }
        else { throw 'The original service SID type is unsupported.' }
    [void](Invoke-NativeCommand -FilePath "$env:SystemRoot\System32\sc.exe" `
        -Arguments @('sidtype', [string]$Snapshot.Name, $sidType))

    $registryPath =
        "HKLM:\SYSTEM\CurrentControlSet\Services\$($Snapshot.Name)"
    if ($Snapshot.FailureActions.Exists) {
        $Snapshot.FailureActions.Value = [byte[]]$Snapshot.FailureActions.Value
    }
    Set-OptionalRegistryValue -Path $registryPath -Name 'FailureActions' `
        -Snapshot $Snapshot.FailureActions -Kind Binary
    Set-OptionalRegistryValue -Path $registryPath `
        -Name 'FailureActionsOnNonCrashFailures' `
        -Snapshot $Snapshot.FailureFlag -Kind DWord
    Set-OptionalRegistryValue -Path $registryPath -Name 'DelayedAutoStart' `
        -Snapshot $Snapshot.DelayedAutoStart -Kind DWord
    Set-OptionalRegistryValue -Path $registryPath -Name 'ServiceSidType' `
        -Snapshot $Snapshot.ServiceSidType -Kind DWord
    [void](Invoke-NativeCommand -FilePath "$env:SystemRoot\System32\sc.exe" `
        -Arguments @('sdset', [string]$Snapshot.Name, [string]$Snapshot.Sddl))

    $restored = Get-ServiceSnapshot -Name ([string]$Snapshot.Name)
    if (-not $restored.Exists `
        -or -not [StringComparer]::OrdinalIgnoreCase.Equals(
            [string]$restored.DisplayName,
            [string]$Snapshot.DisplayName) `
        -or -not [StringComparer]::OrdinalIgnoreCase.Equals(
            [string]$restored.PathName,
            [string]$Snapshot.PathName) `
        -or -not [StringComparer]::OrdinalIgnoreCase.Equals(
            [string]$restored.StartMode,
            [string]$Snapshot.StartMode) `
        -or -not [StringComparer]::OrdinalIgnoreCase.Equals(
            [string]$restored.StartName,
            [string]$Snapshot.StartName) `
        -or -not [StringComparer]::OrdinalIgnoreCase.Equals(
            [string]$restored.ServiceType,
            [string]$Snapshot.ServiceType) `
        -or -not [StringComparer]::Ordinal.Equals(
            [string]$restored.Sddl,
            [string]$Snapshot.Sddl) `
        -or -not (Test-OptionalRegistrySnapshotEqual `
            -Expected $Snapshot.DelayedAutoStart `
            -Actual $restored.DelayedAutoStart) `
        -or -not (Test-OptionalRegistrySnapshotEqual `
            -Expected $Snapshot.FailureActions `
            -Actual $restored.FailureActions) `
        -or -not (Test-OptionalRegistrySnapshotEqual `
            -Expected $Snapshot.FailureFlag `
            -Actual $restored.FailureFlag) `
        -or -not (Test-OptionalRegistrySnapshotEqual `
            -Expected $Snapshot.ServiceSidType `
            -Actual $restored.ServiceSidType)) {
        throw "Windows Service '$($Snapshot.Name)' rollback verification failed."
    }
}

function Test-OptionalRegistrySnapshotEqual {
    param(
        [Parameter(Mandatory = $true)][object]$Expected,
        [Parameter(Mandatory = $true)][object]$Actual
    )

    if ([bool]$Expected.Exists -ne [bool]$Actual.Exists) {
        return $false
    }
    if (-not $Expected.Exists) {
        return $true
    }
    if ($Expected.Value -is [System.Array] -or $Actual.Value -is [System.Array]) {
        $expectedBytes = [byte[]]$Expected.Value
        $actualBytes = [byte[]]$Actual.Value
        return [StringComparer]::Ordinal.Equals(
            [Convert]::ToBase64String($expectedBytes),
            [Convert]::ToBase64String($actualBytes))
    }
    return [string]$Expected.Value -ceq [string]$Actual.Value
}

function Restore-ServiceRunningStates {
    param([Parameter(Mandatory = $true)][object]$State)

    foreach ($snapshot in @($State.WatchdogService, $State.MainService)) {
        if ($snapshot.Exists -and $snapshot.WasRunning) {
            Start-ServiceAndWait -Name $snapshot.Name
        }
    }
}

function Get-CanonicalServiceSid {
    param([Parameter(Mandatory = $true)][string]$ServiceName)

    if ([string]::IsNullOrWhiteSpace($ServiceName) `
        -or -not [StringComparer]::Ordinal.Equals(
            $ServiceName,
            $ServiceName.Trim())) {
        throw 'A canonical Windows Service name is required for SID calculation.'
    }

    $output = Invoke-NativeCommand `
        -FilePath "$env:SystemRoot\System32\sc.exe" `
        -Arguments @('showsid', $ServiceName)
    $text = $output | Out-String
    $matches = [regex]::Matches(
        $text,
        '(?<![0-9-])S-1-5-80-(?:[0-9]+-){4}[0-9]+(?![0-9-])',
        [System.Text.RegularExpressions.RegexOptions]::CultureInvariant)
    $sidValues = @($matches | ForEach-Object { $_.Value } |
        Sort-Object -Unique)
    if ($sidValues.Count -ne 1) {
        throw "Windows did not return one canonical service SID for '$ServiceName'."
    }

    try {
        $sid = [System.Security.Principal.SecurityIdentifier]::new(
            $sidValues[0])
    }
    catch {
        throw "Windows returned an invalid service SID for '$ServiceName'."
    }
    if ($sid.BinaryLength -ne 32 `
        -or -not [StringComparer]::Ordinal.Equals(
            $sid.Value,
            $sidValues[0])) {
        throw "Windows returned a non-canonical service SID for '$ServiceName'."
    }
    return $sid.Value
}

function Get-ExpectedUrlAclSddl {
    $sid = Get-CanonicalServiceSid -ServiceName $mainServiceName
    return "D:(A;;GX;;;$sid)"
}

function Get-UrlAclSnapshot {
    param([Parameter(Mandatory = $true)][string]$Prefix)

    $netshPath = "$env:SystemRoot\System32\netsh.exe"
    $output = @(& $netshPath http show urlacl "url=$Prefix" 2>&1)
    $exitCode = $LASTEXITCODE
    $text = $output | Out-String
    $matches = [regex]::Matches(
        $text,
        '(?im)^\s*SDDL\s*:\s*(?<Sddl>\S+)\s*$')

    if ($exitCode -ne 0 -or $matches.Count -eq 0) {
        $allOutput = @(& $netshPath http show urlacl 2>&1)
        $allExitCode = $LASTEXITCODE
        if ($allExitCode -ne 0) {
            throw "URL ACL query failed with exit codes $exitCode and $allExitCode."
        }

        $prefixListed = $false
        foreach ($line in $allOutput) {
            $trimmed = ([string]$line).Trim()
            $prefixIndex = $trimmed.IndexOf(
                $Prefix,
                [System.StringComparison]::OrdinalIgnoreCase)
            if ($prefixIndex -ge 0 `
                -and [StringComparer]::OrdinalIgnoreCase.Equals(
                    $trimmed.Substring($prefixIndex),
                    $Prefix)) {
                $prefixListed = $true
                break
            }
        }

        if ($prefixListed) {
            throw "URL ACL '$Prefix' was listed but its exact SDDL could not be read."
        }

        return [pscustomobject]@{
            Prefix = $Prefix
            Exists = $false
            Owned = $false
        }
    }

    if ($matches.Count -ne 1) {
        throw "URL ACL '$Prefix' returned more than one SDDL value."
    }

    $owned = [StringComparer]::OrdinalIgnoreCase.Equals(
        $matches[0].Groups['Sddl'].Value,
        (Get-ExpectedUrlAclSddl))
    return [pscustomobject]@{ Prefix = $Prefix; Exists = $true; Owned = $owned }
}

function Assert-UrlAclCanBeManaged {
    param([Parameter(Mandatory = $true)][object]$Snapshot)

    if ($Snapshot.Exists -and -not $Snapshot.Owned) {
        throw "URL ACL '$($Snapshot.Prefix)' is reserved by another owner."
    }
}

function Remove-OwnedUrlAcl {
    param([Parameter(Mandatory = $true)][string]$Prefix)

    $snapshot = Get-UrlAclSnapshot -Prefix $Prefix
    Assert-UrlAclCanBeManaged -Snapshot $snapshot
    if ($snapshot.Exists) {
        [void](Invoke-NativeCommand `
            -FilePath "$env:SystemRoot\System32\netsh.exe" `
            -Arguments @('http', 'delete', 'urlacl', "url=$Prefix"))
        $after = Get-UrlAclSnapshot -Prefix $Prefix
        if ($after.Exists) {
            throw "Owned URL ACL '$Prefix' was not removed."
        }
    }
}

function Ensure-OwnedUrlAcl {
    param([Parameter(Mandatory = $true)][string]$Prefix)

    $snapshot = Get-UrlAclSnapshot -Prefix $Prefix
    Assert-UrlAclCanBeManaged -Snapshot $snapshot
    if ($snapshot.Exists) {
        Remove-OwnedUrlAcl -Prefix $Prefix
    }
    [void](Invoke-NativeCommand `
        -FilePath "$env:SystemRoot\System32\netsh.exe" `
        -Arguments @(
            'http', 'add', 'urlacl',
            "url=$Prefix",
            "sddl=$(Get-ExpectedUrlAclSddl)"))
    $verified = Get-UrlAclSnapshot -Prefix $Prefix
    if (-not $verified.Exists -or -not $verified.Owned) {
        throw "Owned URL ACL '$Prefix' could not be verified."
    }
}

function Restore-UrlAclSnapshot {
    param([Parameter(Mandatory = $true)][object]$Snapshot)

    if ($Snapshot.Exists) {
        if (-not $Snapshot.Owned) {
            return
        }
        Ensure-OwnedUrlAcl -Prefix $Snapshot.Prefix
    }
    else {
        Remove-OwnedUrlAcl -Prefix $Snapshot.Prefix
    }
}

function Get-FirewallRuleSnapshot {
    $rules = @(Get-NetFirewallRule -Name $firewallRuleName `
        -ErrorAction SilentlyContinue)
    if ($rules.Count -eq 0) {
        return [pscustomobject]@{ Exists = $false; Owned = $false }
    }
    if ($rules.Count -ne 1) {
        return [pscustomobject]@{ Exists = $true; Owned = $false }
    }

    $rule = $rules[0]
    $port = Get-NetFirewallPortFilter -AssociatedNetFirewallRule $rule
    $address = Get-NetFirewallAddressFilter -AssociatedNetFirewallRule $rule
    $application = Get-NetFirewallApplicationFilter `
        -AssociatedNetFirewallRule $rule
    $service = Get-NetFirewallServiceFilter -AssociatedNetFirewallRule $rule
    $markedOwned = [StringComparer]::Ordinal.Equals(
            [string]$rule.Description,
            $firewallOwnerDescription) `
        -and [StringComparer]::Ordinal.Equals(
            [string]$rule.Group,
            $firewallRuleGroup)
    $valid = $markedOwned `
        -and [string]$rule.Direction -eq 'Inbound' `
        -and [string]$rule.Action -eq 'Allow' `
        -and [string]$rule.Enabled -eq 'True' `
        -and [int]$rule.Profile -eq 3 `
        -and [string]$rule.EdgeTraversalPolicy -eq 'Block' `
        -and [string]$port.Protocol -eq 'TCP' `
        -and [string]$port.LocalPort -eq [string]$servicePort `
        -and [string]$service.Service -eq $mainServiceName
    return [pscustomobject]@{
        Exists = $true
        Owned = $markedOwned
        Valid = $valid
        LocalAddress = @($address.LocalAddress)
        Program = [string]$application.Program
    }
}

function Assert-FirewallRuleCanBeManaged {
    param([Parameter(Mandatory = $true)][object]$Snapshot)

    if ($Snapshot.Exists -and -not $Snapshot.Owned) {
        throw "Firewall rule '$firewallRuleName' is not owned by this installation."
    }
}

function Remove-OwnedFirewallRule {
    $snapshot = Get-FirewallRuleSnapshot
    Assert-FirewallRuleCanBeManaged -Snapshot $snapshot
    if ($snapshot.Exists) {
        Get-NetFirewallRule -Name $firewallRuleName -ErrorAction Stop |
            Remove-NetFirewallRule -ErrorAction Stop
        if (@(Get-NetFirewallRule -Name $firewallRuleName `
                    -ErrorAction SilentlyContinue).Count -ne 0) {
            throw "Owned firewall rule '$firewallRuleName' was not removed."
        }
    }
}

function New-OwnedFirewallRule {
    param(
        [Parameter(Mandatory = $true)][string[]]$LocalAddress,
        [Parameter(Mandatory = $true)][string]$Program
    )

    [void](New-NetFirewallRule `
        -Name $firewallRuleName `
        -DisplayName $firewallDisplayName `
        -Description $firewallOwnerDescription `
        -Group $firewallRuleGroup `
        -Direction Inbound `
        -Action Allow `
        -Enabled True `
        -Profile Domain,Private `
        -Protocol TCP `
        -LocalPort $servicePort `
        -LocalAddress $LocalAddress `
        -Program $Program `
        -Service $mainServiceName `
        -EdgeTraversalPolicy Block)

    $verified = Get-FirewallRuleSnapshot
    $expectedAddresses = @($LocalAddress | Sort-Object)
    $actualAddresses = @($verified.LocalAddress | Sort-Object)
    if (-not $verified.Exists -or -not $verified.Owned `
        -or -not $verified.Valid `
        -or -not [StringComparer]::OrdinalIgnoreCase.Equals(
            [string]$verified.Program,
            $Program) `
        -or -not [StringComparer]::Ordinal.Equals(
            ($expectedAddresses -join "`n"),
            ($actualAddresses -join "`n"))) {
        throw "Owned firewall rule '$firewallRuleName' could not be verified."
    }
}

function Set-OwnedFirewallRule {
    param(
        [Parameter(Mandatory = $true)][string]$NewAddress,
        [Parameter(Mandatory = $true)][string]$MainExecutablePath
    )

    $snapshot = Get-FirewallRuleSnapshot
    Assert-FirewallRuleCanBeManaged -Snapshot $snapshot
    if ($snapshot.Exists -and -not $snapshot.Valid) {
        throw "Owned firewall rule '$firewallRuleName' is not in a valid managed state."
    }
    if ($snapshot.Exists) {
        Remove-OwnedFirewallRule
    }
    New-OwnedFirewallRule -LocalAddress @($NewAddress) `
        -Program $MainExecutablePath
}

function Restore-FirewallRuleSnapshot {
    param([Parameter(Mandatory = $true)][object]$Snapshot)

    $current = Get-FirewallRuleSnapshot
    Assert-FirewallRuleCanBeManaged -Snapshot $current
    if ($current.Exists) {
        Remove-OwnedFirewallRule
    }
    if ($Snapshot.Exists) {
        if (-not $Snapshot.Owned) {
            throw 'A foreign firewall rule was unexpectedly selected for rollback.'
        }
        New-OwnedFirewallRule -LocalAddress @($Snapshot.LocalAddress) `
            -Program ([string]$Snapshot.Program)
    }
}
