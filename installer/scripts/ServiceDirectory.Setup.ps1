[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('ListAddresses', 'ValidateAddress', 'ReadAddress', 'Prepare', 'Resume', 'Install', 'Uninstall')]
    [string]$Mode,

    [string]$Address,

    [string]$OutputPath,

    [string]$InstallRoot,

    [string]$DataRoot,

    [string]$SetupStatePath,

    [string]$CaRestorePath,

    [ValidateSet('None', 'Restore', 'ConfigureStandby', 'PromoteStandby')]
    [string]$CaOperation = 'None',

    [switch]$PurgeData
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot 'ServiceDirectory.Network.ps1')

$mainServiceName = 'DEEPAi.ServiceDirectory'
$watchdogServiceName = 'DEEPAi.ServiceDirectory.Watchdog'
$operatorsGroupName = 'DEEPAi-ServiceDirectory-Operators'
$firewallRuleName = 'DEEPAi-ServiceDirectory-TCP-21000'
$firewallDisplayName = 'DEEPAi Service Directory (TCP 21000)'
$eventSourceRegistryPath =
    'HKLM:\SYSTEM\CurrentControlSet\Services\EventLog\Application\DEEPAi.ServiceDirectory.Security'
$productRegistryPath = 'HKLM:\SOFTWARE\DEEPAi\ServiceDirectory'
$servicePort = 21000
. (Join-Path $PSScriptRoot 'ServiceDirectory.InstallState.ps1')
. (Join-Path $PSScriptRoot 'ServiceDirectory.FileSystemSecurity.ps1')
. (Join-Path $PSScriptRoot 'ServiceDirectory.HttpsBinding.ps1')
. (Join-Path $PSScriptRoot 'ServiceDirectory.PkiRepair.ps1')

function Assert-ExactInstallationPaths {
    param(
        [Parameter(Mandatory = $true)]
        [string]$RequestedInstallRoot,

        [Parameter(Mandatory = $true)]
        [string]$RequestedDataRoot
    )

    $expectedInstallRoot = [System.IO.Path]::GetFullPath(
        (Join-Path ${env:ProgramFiles} 'DEEPAi\ServiceDirectory'))
    $expectedDataRoot = [System.IO.Path]::GetFullPath(
        (Join-Path ([Environment]::GetFolderPath('CommonApplicationData')) `
            'DEEPAi\ServiceDirectory'))
    $actualInstallRoot = [System.IO.Path]::GetFullPath(
        $RequestedInstallRoot.TrimEnd('\'))
    $actualDataRoot = [System.IO.Path]::GetFullPath(
        $RequestedDataRoot.TrimEnd('\'))

    if (-not [StringComparer]::OrdinalIgnoreCase.Equals(
            $actualInstallRoot,
            $expectedInstallRoot)) {
        throw "The installation root must be exactly '$expectedInstallRoot'."
    }

    if (-not [StringComparer]::OrdinalIgnoreCase.Equals(
            $actualDataRoot,
            $expectedDataRoot)) {
        throw "The data root must be exactly '$expectedDataRoot'."
    }

    return @($actualInstallRoot, $actualDataRoot)
}

function Assert-NoReparsePoint {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,

        [switch]$AllowMissing
    )

    $current = [System.IO.Path]::GetFullPath($Path)
    while (-not [string]::IsNullOrEmpty($current)) {
        if (Test-Path -LiteralPath $current) {
            $item = Get-Item -LiteralPath $current -Force
            if (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) `
                -ne 0) {
                throw "A reparse point is not allowed in setup path '$current'."
            }
        }
        elseif (-not $AllowMissing) {
            throw "Required setup path '$current' does not exist."
        }

        $parent = [System.IO.Path]::GetDirectoryName($current)
        if ([string]::IsNullOrEmpty($parent) `
            -or [StringComparer]::OrdinalIgnoreCase.Equals($parent, $current)) {
            break
        }

        $current = $parent
        $AllowMissing = $true
    }
}

function Test-ServiceExists {
    param([Parameter(Mandatory = $true)][string]$Name)

    return $null -ne (Get-Service -Name $Name -ErrorAction SilentlyContinue)
}

function Test-ServiceRegistrationExists {
    param([Parameter(Mandatory = $true)][string]$Name)

    return (Test-ServiceExists -Name $Name) `
        -or (Test-Path -LiteralPath `
            "HKLM:\SYSTEM\CurrentControlSet\Services\$Name")
}

function Get-StableServiceController {
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [int]$TimeoutSeconds = 30
    )

    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    while ($true) {
        $service = Get-Service -Name $Name -ErrorAction SilentlyContinue
        if ($null -eq $service) {
            return $null
        }

        if (@('Running', 'Stopped', 'Paused') `
            -contains [string]$service.Status) {
            return $service
        }

        if ([DateTime]::UtcNow -ge $deadline) {
            throw "Windows Service '$Name' did not reach a stable state within $TimeoutSeconds seconds."
        }

        Start-Sleep -Milliseconds 200
    }
}

function Stop-ServiceAndWait {
    param([Parameter(Mandatory = $true)][string]$Name)

    $service = Get-StableServiceController -Name $Name
    if ($null -eq $service -or $service.Status -eq 'Stopped') {
        return
    }

    Stop-Service -InputObject $service -Force -ErrorAction Stop
    $service.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30))
    $service.Refresh()
    if ($service.Status -ne 'Stopped') {
        throw "Windows Service '$Name' did not stop."
    }
}

function Start-ServiceAndWait {
    param([Parameter(Mandatory = $true)][string]$Name)

    $service = Get-Service -Name $Name -ErrorAction Stop
    if ($service.Status -ne 'Running') {
        Start-Service -InputObject $service -ErrorAction Stop
        $service.WaitForStatus('Running', [TimeSpan]::FromSeconds(30))
        $service.Refresh()
    }

    if ($service.Status -ne 'Running') {
        throw "Windows Service '$Name' did not start."
    }
}

function Invoke-NativeCommand {
    param(
        [Parameter(Mandatory = $true)]
        [string]$FilePath,

        [Parameter(Mandatory = $true)]
        [string[]]$Arguments,

        [int[]]$AllowedExitCodes = @(0)
    )

    $output = @(& $FilePath @Arguments 2>&1)
    $exitCode = $LASTEXITCODE
    if ($AllowedExitCodes -notcontains $exitCode) {
        $message = ($output | Out-String).Trim()
        throw "'$FilePath' failed with exit code $exitCode. $message"
    }

    return $output
}

function Get-AccountSid {
    param([Parameter(Mandatory = $true)][string]$AccountName)

    $account = [System.Security.Principal.NTAccount]::new($AccountName)
    return [System.Security.Principal.SecurityIdentifier]$account.Translate(
        [System.Security.Principal.SecurityIdentifier])
}

function Ensure-ServiceDefinition {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Name,

        [Parameter(Mandatory = $true)]
        [string]$DisplayName,

        [Parameter(Mandatory = $true)]
        [string]$ExecutablePath,

        [Parameter(Mandatory = $true)]
        [ValidateSet('auto', 'delayed-auto')]
        [string]$StartMode,

        [Parameter(Mandatory = $true)]
        [ValidateSet('None', 'ScmRestart')]
        [string]$FailureRecoveryPolicy
    )

    if (-not (Test-Path -LiteralPath $ExecutablePath -PathType Leaf)) {
        throw "Service executable '$ExecutablePath' does not exist."
    }

    $binaryPath = '"' + $ExecutablePath + '"'
    $account = "NT SERVICE\$Name"
    $created = -not (Test-ServiceExists -Name $Name)
    if ($created) {
        [void](Invoke-NativeCommand -FilePath "$env:SystemRoot\System32\sc.exe" `
            -Arguments @(
                'create', $Name,
                'binPath=', $binaryPath,
                'start=', $StartMode,
                'obj=', $account,
                'DisplayName=', $DisplayName))
    }
    else {
        [void](Invoke-NativeCommand -FilePath "$env:SystemRoot\System32\sc.exe" `
            -Arguments @(
                'config', $Name,
                'binPath=', $binaryPath,
                'start=', $StartMode,
                'obj=', $account,
                'DisplayName=', $DisplayName))
    }

    [void](Invoke-NativeCommand -FilePath "$env:SystemRoot\System32\sc.exe" `
        -Arguments @('sidtype', $Name, 'unrestricted'))
    if ($FailureRecoveryPolicy -eq 'ScmRestart') {
        [void](Invoke-NativeCommand `
            -FilePath "$env:SystemRoot\System32\sc.exe" `
            -Arguments @(
                'failure', $Name,
                'reset=', '86400',
                'actions=', 'restart/5000/restart/15000/none/0'))
        [void](Invoke-NativeCommand `
            -FilePath "$env:SystemRoot\System32\sc.exe" `
            -Arguments @('failureflag', $Name, '1'))
    }
    else {
        [void](Invoke-NativeCommand `
            -FilePath "$env:SystemRoot\System32\sc.exe" `
            -Arguments @(
                'failure', $Name,
                'reset=', '0',
                'actions=', '""'))
        [void](Invoke-NativeCommand `
            -FilePath "$env:SystemRoot\System32\sc.exe" `
            -Arguments @('failureflag', $Name, '0'))
    }
    return $created
}

function Grant-WatchdogMainServiceControl {
    $watchdogSid = Get-AccountSid -AccountName "NT SERVICE\$watchdogServiceName"
    $output = Invoke-NativeCommand `
        -FilePath "$env:SystemRoot\System32\sc.exe" `
        -Arguments @('sdshow', $mainServiceName)
    $sddl = ($output | ForEach-Object { ([string]$_).Trim() } |
        Where-Object { $_ -match '^[OGDS]:' } | Select-Object -First 1)
    if ([string]::IsNullOrWhiteSpace($sddl)) {
        throw 'The main service security descriptor could not be read.'
    }

    $descriptor = [System.Security.AccessControl.CommonSecurityDescriptor]::new(
        $false,
        $false,
        $sddl.Trim())
    $serviceQueryStatus = 0x0004
    $serviceStart = 0x0010
    $serviceStop = 0x0020
    $serviceInterrogate = 0x0080
    $accessMask = $serviceQueryStatus -bor $serviceStart -bor $serviceStop `
        -bor $serviceInterrogate
    for ($index = $descriptor.DiscretionaryAcl.Count - 1; $index -ge 0; $index--) {
        $ace = $descriptor.DiscretionaryAcl[$index]
        if ($ace -is [System.Security.AccessControl.QualifiedAce] `
            -and $ace.SecurityIdentifier -eq $watchdogSid) {
            $descriptor.DiscretionaryAcl.RemoveAce($index)
        }
    }
    $descriptor.DiscretionaryAcl.AddAccess(
        [System.Security.AccessControl.AccessControlType]::Allow,
        $watchdogSid,
        $accessMask,
        [System.Security.AccessControl.InheritanceFlags]::None,
        [System.Security.AccessControl.PropagationFlags]::None)
    $nextSddl = $descriptor.GetSddlForm(
        [System.Security.AccessControl.AccessControlSections]::Access)
    [void](Invoke-NativeCommand -FilePath "$env:SystemRoot\System32\sc.exe" `
        -Arguments @('sdset', $mainServiceName, $nextSddl))
}

function Read-ConfigurationIdentity {
    param([Parameter(Mandatory = $true)][string]$ConfigPath)

    if (-not (Test-Path -LiteralPath $ConfigPath -PathType Leaf)) {
        return $null
    }

    Assert-NoReparsePoint -Path $ConfigPath
    $settings = [System.Xml.XmlReaderSettings]::new()
    $settings.DtdProcessing = [System.Xml.DtdProcessing]::Prohibit
    $settings.XmlResolver = $null
    $settings.MaxCharactersInDocument = 1MB
    $reader = [System.Xml.XmlReader]::Create($ConfigPath, $settings)
    try {
        $document = [System.Xml.XmlDocument]::new()
        $document.XmlResolver = $null
        $document.Load($reader)
    }
    finally {
        $reader.Dispose()
    }

    if ($document.DocumentElement.LocalName -ne 'Config' `
        -or $document.DocumentElement.NamespaceURI -ne '' `
        -or $document.DocumentElement.GetAttribute('SchemaVersion') -ne '1') {
        throw 'Existing config.xml does not use the supported canonical schema.'
    }

    $addressNodes = $document.SelectNodes('/Config/ListenAddress')
    $hostNameNodes = $document.SelectNodes('/Config/DirectoryHostName')
    $identityAddressNodes =
        $document.SelectNodes('/Config/DirectoryIpv4Address')
    if ($addressNodes.Count -ne 1 `
        -or $hostNameNodes.Count -ne 1 `
        -or $identityAddressNodes.Count -ne 1) {
        throw 'Existing config.xml must contain exactly one canonical Directory identity.'
    }

    $address = ConvertTo-CanonicalAddress -Value $addressNodes[0].InnerText
    $identityAddress = ConvertTo-CanonicalAddress `
        -Value $identityAddressNodes[0].InnerText
    if (-not [StringComparer]::Ordinal.Equals($address, $identityAddress)) {
        throw 'Existing config.xml ListenAddress and DirectoryIpv4Address disagree.'
    }

    $hostName = ConvertTo-CanonicalDirectoryHostName `
        -Value $hostNameNodes[0].InnerText
    if (-not [StringComparer]::Ordinal.Equals(
            $hostNameNodes[0].InnerText,
            $hostName)) {
        throw 'Existing config.xml DirectoryHostName is not canonical.'
    }

    return [pscustomobject]@{
        Address = $address
        HostName = $hostName
    }
}

function Read-ConfigurationAddress {
    param([Parameter(Mandatory = $true)][string]$ConfigPath)

    $identity = Read-ConfigurationIdentity -ConfigPath $ConfigPath
    if ($null -eq $identity) {
        return $null
    }
    return [string]$identity.Address
}

function Assert-NoActiveJournal {
    param([Parameter(Mandatory = $true)][string]$RequestedDataRoot)

    $journalRoot = Join-Path $RequestedDataRoot 'journal'
    if (-not (Test-Path -LiteralPath $journalRoot -PathType Container)) {
        return
    }

    Assert-NoReparsePoint -Path $journalRoot
    $activePattern = '^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$'
    $active = @(Get-ChildItem -LiteralPath $journalRoot -Directory -Force |
        Where-Object { $_.Name -cmatch $activePattern })
    if ($active.Count -ne 0) {
        throw 'Setup cannot modify ListenAddress while an active recovery journal exists.'
    }
}

function Write-BytesDurably {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,

        [Parameter(Mandatory = $true)]
        [byte[]]$Bytes
    )

    $stream = [System.IO.FileStream]::new(
        $Path,
        [System.IO.FileMode]::CreateNew,
        [System.IO.FileAccess]::Write,
        [System.IO.FileShare]::None,
        4096,
        [System.IO.FileOptions]::WriteThrough)
    try {
        $stream.Write($Bytes, 0, $Bytes.Length)
        $stream.Flush($true)
    }
    finally {
        $stream.Dispose()
    }
}

function Replace-FileDurably {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,

        [Parameter(Mandatory = $true)]
        [byte[]]$Bytes,

        [string]$BackupPath
    )

    $temporaryPath = Join-Path ([System.IO.Path]::GetDirectoryName($Path)) `
        ('.' + [System.IO.Path]::GetFileName($Path) + '.' `
            + [Guid]::NewGuid().ToString('N') + '.tmp')
    try {
        Write-BytesDurably -Path $temporaryPath -Bytes $Bytes
        if (Test-Path -LiteralPath $Path -PathType Leaf) {
            [System.IO.File]::Replace($temporaryPath, $Path, $BackupPath, $true)
        }
        else {
            [System.IO.File]::Move($temporaryPath, $Path)
        }
    }
    finally {
        if (Test-Path -LiteralPath $temporaryPath -PathType Leaf) {
            Remove-Item -LiteralPath $temporaryPath -Force
        }
    }
}

function Get-LocalDirectoryHostName {
    $properties = [System.Net.NetworkInformation.IPGlobalProperties]::
        GetIPGlobalProperties()
    $hostName = [string]$properties.HostName
    $domainName = [string]$properties.DomainName
    if ([string]::IsNullOrWhiteSpace($hostName)) {
        throw 'The local Management Server hostname could not be determined.'
    }

    $value = if ($hostName.Contains('.') `
        -or [string]::IsNullOrWhiteSpace($domainName)) {
        $hostName
    }
    else {
        $hostName + '.' + $domainName
    }
    return ConvertTo-CanonicalDirectoryHostName -Value $value
}

function Set-InitialOrRepairedConfiguration {
    param(
        [Parameter(Mandatory = $true)]
        [string]$RequestedDataRoot,

        [Parameter(Mandatory = $true)]
        [string]$NewAddress,

        [Parameter(Mandatory = $true)]
        [string]$NewHostName,

        [Parameter(Mandatory = $true)]
        [bool]$FreshInstallation
    )

    Assert-NoActiveJournal -RequestedDataRoot $RequestedDataRoot
    $configPath = Join-Path $RequestedDataRoot 'config.xml'
    $backupPath = $configPath + '.bak'
    $directoryPath = Join-Path $RequestedDataRoot 'directory.xml'
    $directoryBackupPath = $directoryPath + '.bak'
    $pendingPath = Join-Path $RequestedDataRoot 'pending.xml'
    $pendingBackupPath = $pendingPath + '.bak'
    $directoryHostName = ConvertTo-CanonicalDirectoryHostName `
        -Value $NewHostName

    foreach ($statePath in @(
            $configPath,
            $backupPath,
            $directoryPath,
            $directoryBackupPath,
            $pendingPath,
            $pendingBackupPath)) {
        if ((Test-Path -LiteralPath $statePath) `
            -and -not (Test-Path -LiteralPath $statePath -PathType Leaf)) {
            throw "Installed state path '$statePath' is not a regular file."
        }
        if (Test-Path -LiteralPath $statePath -PathType Leaf) {
            Assert-NoReparsePoint -Path $statePath
        }
    }

    $configExists = Test-Path -LiteralPath $configPath -PathType Leaf
    $configBackupExists = Test-Path -LiteralPath $backupPath -PathType Leaf
    $directoryExists = Test-Path -LiteralPath $directoryPath -PathType Leaf
    $directoryBackupExists = Test-Path -LiteralPath $directoryBackupPath `
        -PathType Leaf
    $pendingExists = Test-Path -LiteralPath $pendingPath -PathType Leaf
    $pendingBackupExists = Test-Path -LiteralPath $pendingBackupPath `
        -PathType Leaf

    if ($pendingExists -or $pendingBackupExists) {
        throw 'The target storage schema does not accept pending.xml artifacts; reset unshipped development state explicitly.'
    }

    if (-not $configExists -and $configBackupExists) {
        throw 'config.xml backup exists without its primary file.'
    }

    if ($FreshInstallation) {
        if ($configExists `
            -or $configBackupExists `
            -or $directoryExists `
            -or $directoryBackupExists) {
            throw 'Fresh installation state initialization requires every primary and backup state file to be absent.'
        }
    }
    else {
        if (-not $configExists) {
            throw 'Existing installation config.xml is missing; setup will not initialize over preserved or damaged state.'
        }
        if (-not $directoryExists) {
            throw 'Existing installation directory.xml is missing; setup will not reset the logical high-water state.'
        }
    }

    if ($configExists) {
        $oldAddress = Read-ConfigurationAddress -ConfigPath $configPath
        $text = [System.IO.File]::ReadAllText(
            $configPath,
            [System.Text.Encoding]::UTF8)
        $listenMatches = [regex]::Matches(
            $text,
            '<ListenAddress>([^<]+)</ListenAddress>',
            [System.Text.RegularExpressions.RegexOptions]::CultureInvariant)
        $hostMatches = [regex]::Matches(
            $text,
            '<DirectoryHostName>([^<]+)</DirectoryHostName>',
            [System.Text.RegularExpressions.RegexOptions]::CultureInvariant)
        $identityAddressMatches = [regex]::Matches(
            $text,
            '<DirectoryIpv4Address>([^<]+)</DirectoryIpv4Address>',
            [System.Text.RegularExpressions.RegexOptions]::CultureInvariant)
        if ($listenMatches.Count -ne 1 `
            -or $hostMatches.Count -ne 1 `
            -or $identityAddressMatches.Count -ne 1 `
            -or -not [StringComparer]::Ordinal.Equals(
                $listenMatches[0].Groups[1].Value,
                $oldAddress) `
            -or -not [StringComparer]::Ordinal.Equals(
                $identityAddressMatches[0].Groups[1].Value,
                $oldAddress)) {
            throw 'Existing config.xml is not in the canonical storage form.'
        }

        $oldHostName = $hostMatches[0].Groups[1].Value
        if ([StringComparer]::Ordinal.Equals($oldAddress, $NewAddress) `
            -and [StringComparer]::Ordinal.Equals(
                $oldHostName,
                $directoryHostName)) {
            return $oldAddress
        }

        $nextText = $text.Replace(
            '<ListenAddress>' + $oldAddress + '</ListenAddress>',
            '<ListenAddress>' + $NewAddress + '</ListenAddress>')
        $nextText = $nextText.Replace(
            '<DirectoryHostName>' + $oldHostName + '</DirectoryHostName>',
            '<DirectoryHostName>' + $directoryHostName + '</DirectoryHostName>')
        $nextText = $nextText.Replace(
            '<DirectoryIpv4Address>' + $oldAddress `
                + '</DirectoryIpv4Address>',
            '<DirectoryIpv4Address>' + $NewAddress `
                + '</DirectoryIpv4Address>')
        $nextBytes = [System.Text.UTF8Encoding]::new($false).GetBytes($nextText)
        Replace-FileDurably -Path $configPath -Bytes $nextBytes `
            -BackupPath $backupPath
        return $oldAddress
    }

    $directoryXml = '<?xml version="1.0" encoding="utf-8"?>' + "`r`n" `
        + '<Directory SchemaVersion="1">' + "`r`n" `
        + '  <LogicalClock>0</LogicalClock>' + "`r`n" `
        + '  <Records />' + "`r`n" `
        + '</Directory>' + "`r`n"
    $strictUtf8 = [System.Text.UTF8Encoding]::new($false)
    Replace-FileDurably -Path $directoryPath `
        -Bytes $strictUtf8.GetBytes($directoryXml)

    $instanceId = [Guid]::NewGuid().ToString('D')
    $xml = '<?xml version="1.0" encoding="utf-8"?>' + "`r`n" `
        + '<Config SchemaVersion="1">' + "`r`n" `
        + "  <ListenAddress>$NewAddress</ListenAddress>" + "`r`n" `
        + "  <DirectoryHostName>$directoryHostName</DirectoryHostName>" + "`r`n" `
        + "  <DirectoryIpv4Address>$NewAddress</DirectoryIpv4Address>" + "`r`n" `
        + "  <InstanceId>$instanceId</InstanceId>" + "`r`n" `
        + '  <LastPeerKeyEpoch>0</LastPeerKeyEpoch>' + "`r`n" `
        + '  <LogRetentionDays>30</LogRetentionDays>' + "`r`n" `
        + '  <Sync>' + "`r`n" `
        + '    <State>Unpaired</State>' + "`r`n" `
        + '    <LastResult>NOT_RUN</LastResult>' + "`r`n" `
        + '    <LastPeerNotificationOperation>NONE</LastPeerNotificationOperation>' + "`r`n" `
        + '    <LastPeerNotificationResult>NOT_RUN</LastPeerNotificationResult>' + "`r`n" `
        + '  </Sync>' + "`r`n" `
        + '</Config>' + "`r`n"
    $bytes = $strictUtf8.GetBytes($xml)
    Replace-FileDurably -Path $configPath -Bytes $bytes
    return $null
}

function Get-HttpPrefix {
    param([Parameter(Mandatory = $true)][string]$Value)

    $canonical = ConvertTo-CanonicalAddress -Value $Value
    return "http://$canonical`:$servicePort/"
}

function Restore-FileSnapshot {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][bool]$Existed,
        [byte[]]$Bytes
    )

    if ($Existed) {
        if ($null -eq $Bytes) {
            throw "A required rollback image for '$Path' is missing."
        }

        $rollbackBackup = $Path + '.setup-rollback'
        Replace-FileDurably -Path $Path -Bytes $Bytes -BackupPath $rollbackBackup
        if (Test-Path -LiteralPath $rollbackBackup -PathType Leaf) {
            Remove-Item -LiteralPath $rollbackBackup -Force
        }
    }
    elseif (Test-Path -LiteralPath $Path -PathType Leaf) {
        Remove-Item -LiteralPath $Path -Force
    }

    $existsAfterRestore = Test-Path -LiteralPath $Path -PathType Leaf
    if ($existsAfterRestore -ne $Existed) {
        throw "File rollback existence verification failed for '$Path'."
    }
    if ($Existed) {
        $actualBytes = [System.IO.File]::ReadAllBytes($Path)
        if (-not [StringComparer]::Ordinal.Equals(
                [Convert]::ToBase64String($Bytes),
                [Convert]::ToBase64String($actualBytes))) {
            throw "File rollback content verification failed for '$Path'."
        }
    }
}

function Get-FileSnapshot {
    param([Parameter(Mandatory = $true)][string]$Path)

    if (-not (Test-Path -LiteralPath $Path)) {
        return [pscustomobject]@{
            Existed = $false
            Bytes = $null
        }
    }

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "Rollback snapshot path '$Path' is not a regular file."
    }

    Assert-NoReparsePoint -Path $Path
    return [pscustomobject]@{
        Existed = $true
        Bytes = [System.IO.File]::ReadAllBytes($Path)
    }
}

function Remove-ServiceDefinition {
    param([Parameter(Mandatory = $true)][string]$Name)

    $registryPath = "HKLM:\SYSTEM\CurrentControlSet\Services\$Name"
    if (-not (Test-ServiceExists -Name $Name)) {
        $deadline = [DateTime]::UtcNow.AddSeconds(30)
        while ((Test-Path -LiteralPath $registryPath) `
            -and [DateTime]::UtcNow -lt $deadline) {
            Start-Sleep -Milliseconds 200
        }
        if (Test-Path -LiteralPath $registryPath) {
            throw "Windows Service '$Name' has a stale registration that SCM did not release."
        }
        return
    }

    Stop-ServiceAndWait -Name $Name
    [void](Invoke-NativeCommand -FilePath "$env:SystemRoot\System32\sc.exe" `
        -Arguments @('delete', $Name))
    $deadline = [DateTime]::UtcNow.AddSeconds(30)
    while ((Test-ServiceRegistrationExists -Name $Name) `
        -and [DateTime]::UtcNow -lt $deadline) {
        Start-Sleep -Milliseconds 200
    }

    if (Test-ServiceRegistrationExists -Name $Name) {
        throw "Windows Service '$Name' was marked for deletion but its SCM or registry registration did not disappear."
    }
}

function Invoke-Install {
    param(
        [Parameter(Mandatory = $true)][string]$RequestedInstallRoot,
        [Parameter(Mandatory = $true)][string]$RequestedDataRoot,
        [Parameter(Mandatory = $true)][string]$NewAddress,
        [Parameter(Mandatory = $true)][string]$StatePath,
        [string]$RequestedCaRestorePath,
        [Parameter(Mandatory = $true)]
        [ValidateSet('None', 'Restore', 'ConfigureStandby', 'PromoteStandby')]
        [string]$RequestedCaOperation
    )

    $paths = Assert-ExactInstallationPaths `
        -RequestedInstallRoot $RequestedInstallRoot `
        -RequestedDataRoot $RequestedDataRoot
    $RequestedInstallRoot = $paths[0]
    $RequestedDataRoot = $paths[1]
    $NewAddress = Assert-AddressIsEligible -Value $NewAddress
    $newHostName = Get-LocalDirectoryHostName
    Assert-NoReparsePoint -Path $RequestedInstallRoot
    Assert-NoReparsePoint -Path $RequestedDataRoot -AllowMissing
    $setupState = Read-SetupState -Path $StatePath `
        -InstallRoot $RequestedInstallRoot -DataRoot $RequestedDataRoot

    $mainExecutable = Join-Path $RequestedInstallRoot `
        'DEEPAi.ServiceDirectory.Service.exe'
    $watchdogExecutable = Join-Path $RequestedInstallRoot `
        'DEEPAi.ServiceDirectory.Watchdog.exe'
    Assert-OwnedServiceSnapshot -Snapshot $setupState.MainService `
        -ExpectedExecutablePath $mainExecutable
    Assert-OwnedServiceSnapshot -Snapshot $setupState.WatchdogService `
        -ExpectedExecutablePath $watchdogExecutable
    $currentMainService = Get-ServiceSnapshot -Name $mainServiceName
    $currentWatchdogService = Get-ServiceSnapshot -Name $watchdogServiceName
    if ([bool]$currentMainService.Exists -ne [bool]$setupState.MainService.Exists `
        -or [bool]$currentWatchdogService.Exists `
            -ne [bool]$setupState.WatchdogService.Exists) {
        throw 'Service presence changed after setup preparation.'
    }
    Assert-OwnedServiceSnapshot -Snapshot $currentMainService `
        -ExpectedExecutablePath $mainExecutable
    Assert-OwnedServiceSnapshot -Snapshot $currentWatchdogService `
        -ExpectedExecutablePath $watchdogExecutable
    Assert-TrayRunValueCanBeManaged -Snapshot $setupState.TrayRunValue
    $configPath = Join-Path $RequestedDataRoot 'config.xml'
    $configBackupPath = $configPath + '.bak'
    $directoryStatePath = Join-Path $RequestedDataRoot 'directory.xml'
    $directoryStateBackupPath = $directoryStatePath + '.bak'
    $configSnapshot = Get-FileSnapshot -Path $configPath
    $configBackupSnapshot = Get-FileSnapshot -Path $configBackupPath
    $directorySnapshot = Get-FileSnapshot -Path $directoryStatePath
    $directoryBackupSnapshot = Get-FileSnapshot `
        -Path $directoryStateBackupPath
    $certificateAuthoritySnapshots =
        Get-CertificateAuthorityStateSnapshots `
            -DataRoot $RequestedDataRoot
    $configExisted = [bool]$configSnapshot.Existed
    $oldIdentity = if ($configExisted) {
        Read-ConfigurationIdentity -ConfigPath $configPath
    } else {
        $null
    }
    $oldAddress = if ($null -ne $oldIdentity) {
        [string]$oldIdentity.Address
    } else {
        $null
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
    $addresses = New-Object 'System.Collections.Generic.HashSet[string]' `
        ([StringComparer]::Ordinal)
    if ($null -ne $oldAddress) {
        [void]$addresses.Add($oldAddress)
    }
    [void]$addresses.Add($NewAddress)
    $prefixes = New-Object 'System.Collections.Generic.HashSet[string]' `
        ([StringComparer]::OrdinalIgnoreCase)
    foreach ($address in $addresses) {
        [void]$prefixes.Add((Get-HttpPrefix -Value $address))
        [void]$prefixes.Add((Get-RemoteHttpsPrefix -Address $address))
    }
    $newRemotePrefix = Get-RemoteHttpsPrefix -Address $NewAddress
    $newHostNamePrefix = Get-RemoteHttpsHostNamePrefix `
        -HostName $newHostName
    if ($null -ne $oldIdentity) {
        [void]$prefixes.Add((Get-RemoteHttpsHostNamePrefix `
                -HostName ([string]$oldIdentity.HostName)))
    }
    [void]$prefixes.Add($newHostNamePrefix)
    $loopbackPrefix = "http://127.0.0.1:$servicePort/"
    [void]$prefixes.Add($loopbackPrefix)
    $urlAclSnapshots = @($prefixes | ForEach-Object {
            Get-UrlAclSnapshot -Prefix $_
        })
    foreach ($snapshot in $urlAclSnapshots) {
        Assert-UrlAclCanBeManaged -Snapshot $snapshot
    }
    $httpsBindingSnapshots = @($addresses | ForEach-Object {
            Get-HttpsBindingSnapshot -Address $_
        })
    foreach ($snapshot in $httpsBindingSnapshots) {
        Assert-HttpsBindingCanBeManaged -Snapshot $snapshot
    }

    $dataRootPresent = Test-Path -LiteralPath $RequestedDataRoot
    $dataRootExisted = Test-Path -LiteralPath $RequestedDataRoot `
        -PathType Container
    if ($dataRootPresent -and -not $dataRootExisted) {
        throw 'The exact service data root exists but is not a directory.'
    }
    $freshInstallation = -not $dataRootExisted `
        -and -not [bool]$setupState.MainService.Exists `
        -and -not [bool]$setupState.WatchdogService.Exists `
        -and (-not [bool]$productSnapshot.Exists `
            -or [bool]$productSnapshot.Recoverable)
    $backupOperation = -not [StringComparer]::Ordinal.Equals(
        $RequestedCaOperation,
        'None')
    if ($backupOperation -ne (-not [string]::IsNullOrWhiteSpace(
                $RequestedCaRestorePath))) {
        throw 'A CA role operation and an encrypted backup path must be selected together.'
    }
    if ($freshInstallation `
        -and ($RequestedCaOperation -eq 'Restore' `
            -or $RequestedCaOperation -eq 'PromoteStandby')) {
        throw 'A fresh installation can create an active issuer or configure a standby only.'
    }
    $aclRoots = @($RequestedInstallRoot)
    if ($dataRootExisted) {
        $aclRoots += $RequestedDataRoot
    }
    $aclSnapshots = @(Get-FileSystemAclSnapshot -Roots $aclRoots)
    $createdDirectories = New-Object 'System.Collections.Generic.List[string]'
    $installedDirectoryCertificate = $null

    try {
        Stop-ServiceAndWait -Name $watchdogServiceName
        Stop-ServiceAndWait -Name $mainServiceName
        [void](Ensure-OperatorsGroup)
        [void](Ensure-ServiceDefinition `
            -Name $mainServiceName `
            -DisplayName 'DEEPAi Service Directory' `
            -ExecutablePath $mainExecutable `
            -StartMode delayed-auto `
            -FailureRecoveryPolicy None)
        [void](Ensure-ServiceDefinition `
            -Name $watchdogServiceName `
            -DisplayName 'DEEPAi Service Directory Watchdog' `
            -ExecutablePath $watchdogExecutable `
            -StartMode auto `
            -FailureRecoveryPolicy ScmRestart)
        Grant-WatchdogMainServiceControl

        if (-not (Test-Path -LiteralPath $RequestedDataRoot `
                -PathType Container)) {
            [void](New-Item -ItemType Directory -Path $RequestedDataRoot)
            [void]$createdDirectories.Add($RequestedDataRoot)
        }
        foreach ($relativePath in @(
                'logs\system',
                'secrets',
                'journal',
                'pki')) {
            $directoryPath = Join-Path $RequestedDataRoot $relativePath
            if (-not (Test-Path -LiteralPath $directoryPath -PathType Container)) {
                [void](New-Item -ItemType Directory -Path $directoryPath -Force)
                [void]$createdDirectories.Add($directoryPath)
            }
        }
        Set-InstallationAcls `
            -RequestedInstallRoot $RequestedInstallRoot `
            -RequestedDataRoot $RequestedDataRoot

        [void](Set-InitialOrRepairedConfiguration `
            -RequestedDataRoot $RequestedDataRoot `
            -NewAddress $NewAddress `
            -NewHostName $newHostName `
            -FreshInstallation $freshInstallation)

        if ($freshInstallation `
            -and [StringComparer]::Ordinal.Equals(
                $RequestedCaOperation,
                'None')) {
            Invoke-CaProvision -ExecutablePath $mainExecutable
        }
        elseif ([StringComparer]::Ordinal.Equals(
                $RequestedCaOperation,
                'Restore')) {
            Invoke-CaRestore -ExecutablePath $mainExecutable `
                -BackupPath $RequestedCaRestorePath
        }

        if ($RequestedCaOperation -eq 'ConfigureStandby' `
            -or $RequestedCaOperation -eq 'PromoteStandby') {
            $installedDirectoryCertificate =
                Invoke-CaStandbyRoleChange `
                    -ExecutablePath $mainExecutable `
                    -BackupPath $RequestedCaRestorePath `
                    -Operation $RequestedCaOperation
        } else {
            $installedDirectoryCertificate =
                Invoke-DirectoryCertificateInstall `
                    -ExecutablePath $mainExecutable
        }
        foreach ($address in $addresses) {
            Remove-OwnedUrlAcl -Prefix (Get-HttpPrefix -Value $address)
        }
        if ($null -ne $oldAddress `
            -and -not [StringComparer]::Ordinal.Equals($oldAddress, $NewAddress)) {
            Remove-OwnedUrlAcl `
                -Prefix (Get-RemoteHttpsPrefix -Address $oldAddress)
            Remove-OwnedHttpsBinding -Address $oldAddress
        }
        if ($null -ne $oldIdentity `
            -and -not [StringComparer]::Ordinal.Equals(
                [string]$oldIdentity.HostName,
                $newHostName)) {
            Remove-OwnedUrlAcl `
                -Prefix (Get-RemoteHttpsHostNamePrefix `
                    -HostName ([string]$oldIdentity.HostName))
        }
        Ensure-OwnedUrlAcl -Prefix $newRemotePrefix
        Ensure-OwnedUrlAcl -Prefix $newHostNamePrefix
        Ensure-OwnedUrlAcl -Prefix $loopbackPrefix
        Set-OwnedHttpsBinding `
            -Address $NewAddress `
            -Thumbprint $installedDirectoryCertificate.Thumbprint
        Set-OwnedFirewallRule `
            -NewAddress $NewAddress `
            -MainExecutablePath $mainExecutable
        Set-EventSource
        Set-ProductRegistration -NewAddress $NewAddress
        Set-OwnedTrayRunValue -InstallRoot $RequestedInstallRoot

        Start-ServiceAndWait -Name $watchdogServiceName
        Start-ServiceAndWait -Name $mainServiceName
    }
    catch {
        $failure = $_
        $rollbackFailures = New-Object `
            'System.Collections.Generic.List[System.Exception]'
        try { Stop-ServiceAndWait -Name $watchdogServiceName } `
            catch { [void]$rollbackFailures.Add($_.Exception) }
        try { Stop-ServiceAndWait -Name $mainServiceName } `
            catch { [void]$rollbackFailures.Add($_.Exception) }

        foreach ($snapshot in $httpsBindingSnapshots) {
            try { Restore-HttpsBindingSnapshot -Snapshot $snapshot } `
                catch { [void]$rollbackFailures.Add($_.Exception) }
        }
        if ($null -ne $installedDirectoryCertificate) {
            try {
                Invoke-DirectoryCertificateRemove `
                    -ExecutablePath $mainExecutable `
                    -Thumbprint $installedDirectoryCertificate.Thumbprint
            }
            catch { [void]$rollbackFailures.Add($_.Exception) }
        }
        foreach ($snapshot in $urlAclSnapshots) {
            try { Restore-UrlAclSnapshot -Snapshot $snapshot } `
                catch { [void]$rollbackFailures.Add($_.Exception) }
        }
        try { Restore-FirewallRuleSnapshot -Snapshot $firewallSnapshot } `
            catch { [void]$rollbackFailures.Add($_.Exception) }

        try {
            Restore-FileSnapshot `
                -Path $configPath `
                -Existed ([bool]$configSnapshot.Existed) `
                -Bytes $configSnapshot.Bytes
            Restore-FileSnapshot `
                -Path $configBackupPath `
                -Existed ([bool]$configBackupSnapshot.Existed) `
                -Bytes $configBackupSnapshot.Bytes
            Restore-FileSnapshot `
                -Path $directoryStatePath `
                -Existed ([bool]$directorySnapshot.Existed) `
                -Bytes $directorySnapshot.Bytes
            Restore-FileSnapshot `
                -Path $directoryStateBackupPath `
                -Existed ([bool]$directoryBackupSnapshot.Existed) `
                -Bytes $directoryBackupSnapshot.Bytes
            Restore-CertificateAuthorityStateSnapshots `
                -Snapshots $certificateAuthoritySnapshots
        }
        catch { [void]$rollbackFailures.Add($_.Exception) }

        try { Restore-ProductRegistrationSnapshot -Snapshot $productSnapshot } `
            catch { [void]$rollbackFailures.Add($_.Exception) }
        try { Restore-TrayRunValueSnapshot `
                -Snapshot $setupState.TrayRunValue `
                -InstallRoot $RequestedInstallRoot } `
            catch { [void]$rollbackFailures.Add($_.Exception) }

        if ((-not $eventSourceSnapshot.Exists `
                -or $eventSourceSnapshot.Recoverable) `
            -and (Test-Path -LiteralPath $eventSourceRegistryPath)) {
            try { Remove-OwnedEventSource }
            catch { [void]$rollbackFailures.Add($_.Exception) }
        }

        try { Restore-FileSystemAclSnapshot -Snapshots $aclSnapshots } `
            catch { [void]$rollbackFailures.Add($_.Exception) }

        if (-not $dataRootExisted `
            -and (Test-Path -LiteralPath $RequestedDataRoot)) {
            try {
                Assert-NoReparsePoint -Path $RequestedDataRoot
                [void](Get-FileSystemTreeItems -Path $RequestedDataRoot)
                Remove-Item -LiteralPath $RequestedDataRoot -Recurse -Force
            }
            catch { [void]$rollbackFailures.Add($_.Exception) }
        }
        elseif ($dataRootExisted) {
            foreach ($directoryPath in ($createdDirectories |
                    Sort-Object Length -Descending)) {
                try {
                    if ((Test-Path -LiteralPath $directoryPath `
                            -PathType Container) `
                        -and @(Get-ChildItem -LiteralPath $directoryPath `
                            -Force).Count -eq 0) {
                        Remove-Item -LiteralPath $directoryPath -Force
                    }
                }
                catch { [void]$rollbackFailures.Add($_.Exception) }
            }
        }

        try { Restore-ServiceDefinitionSnapshot `
                -Snapshot $setupState.WatchdogService } `
            catch { [void]$rollbackFailures.Add($_.Exception) }
        try { Restore-ServiceDefinitionSnapshot `
                -Snapshot $setupState.MainService } `
            catch { [void]$rollbackFailures.Add($_.Exception) }
        if (-not $groupSnapshot.Exists) {
            try { Remove-OperatorsGroup } `
                catch { [void]$rollbackFailures.Add($_.Exception) }
        }

        try {
            if (Test-Path -LiteralPath $StatePath -PathType Leaf) {
                Remove-Item -LiteralPath $StatePath -Force
            }
        }
        catch { [void]$rollbackFailures.Add($_.Exception) }

        if ($rollbackFailures.Count -ne 0) {
            $allFailures = New-Object `
                'System.Collections.Generic.List[System.Exception]'
            [void]$allFailures.Add($failure.Exception)
            foreach ($rollbackFailure in $rollbackFailures) {
                [void]$allFailures.Add($rollbackFailure)
            }
            throw [System.AggregateException]::new(
                'Installation failed and rollback was incomplete.',
                $allFailures)
        }

        throw $failure
    }
}

function Invoke-Uninstall {
    param(
        [Parameter(Mandatory = $true)][string]$RequestedInstallRoot,
        [Parameter(Mandatory = $true)][string]$RequestedDataRoot,
        [bool]$DeleteData
    )

    $paths = Assert-ExactInstallationPaths `
        -RequestedInstallRoot $RequestedInstallRoot `
        -RequestedDataRoot $RequestedDataRoot
    $RequestedInstallRoot = $paths[0]
    $RequestedDataRoot = $paths[1]
    $configPath = Join-Path $RequestedDataRoot 'config.xml'
    $installedIdentity = $null
    $installedAddress = $null
    try {
        $installedIdentity = Read-ConfigurationIdentity `
            -ConfigPath $configPath
        if ($null -ne $installedIdentity) {
            $installedAddress = [string]$installedIdentity.Address
        }
    }
    catch {
        $installedAddress = Get-RegisteredAddress
    }
    if ($null -eq $installedAddress) {
        $installedAddress = Get-RegisteredAddress
    }
    if ($null -eq $installedAddress `
        -and ((Test-ServiceExists -Name $mainServiceName) `
            -or (Test-Path -LiteralPath $configPath))) {
        throw 'The installed ListenAddress is unavailable; exact URL ACL cleanup cannot proceed safely.'
    }
    if ($null -ne $installedAddress -and $null -eq $installedIdentity) {
        throw 'The installed Directory hostname is unavailable; exact URL ACL cleanup cannot proceed safely.'
    }

    $mainSnapshot = Get-ServiceSnapshot -Name $mainServiceName
    $watchdogSnapshot = Get-ServiceSnapshot -Name $watchdogServiceName
    Assert-OwnedServiceSnapshot -Snapshot $mainSnapshot `
        -ExpectedExecutablePath (Join-Path $RequestedInstallRoot `
            'DEEPAi.ServiceDirectory.Service.exe')
    Assert-OwnedServiceSnapshot -Snapshot $watchdogSnapshot `
        -ExpectedExecutablePath (Join-Path $RequestedInstallRoot `
            'DEEPAi.ServiceDirectory.Watchdog.exe')
    $groupSnapshot = Get-OperatorsGroupSnapshot
    Assert-OperatorsGroupCanBeManaged -Snapshot $groupSnapshot
    $eventSourceSnapshot = Get-EventSourceSnapshot
    if ($eventSourceSnapshot.Exists `
        -and (-not $eventSourceSnapshot.Owned `
            -or (-not $eventSourceSnapshot.Valid `
                -and -not $eventSourceSnapshot.Recoverable))) {
        throw 'The security Event Log source registration is foreign or conflicting.'
    }
    $productSnapshot = Get-ProductRegistrationSnapshot
    if ($null -ne $installedAddress `
        -and $productSnapshot.Exists `
        -and $null -ne $productSnapshot.Address `
        -and -not [StringComparer]::Ordinal.Equals(
            $installedAddress,
            [string]$productSnapshot.Address)) {
        throw 'config.xml and the owned product registration disagree on ListenAddress.'
    }
    $trayRunSnapshot = Get-TrayRunValueSnapshot `
        -InstallRoot $RequestedInstallRoot
    Assert-TrayRunValueCanBeManaged -Snapshot $trayRunSnapshot
    $firewallSnapshot = Get-FirewallRuleSnapshot
    Assert-FirewallRuleCanBeManaged -Snapshot $firewallSnapshot
    $urlAclSnapshots = @()
    $httpsBindingSnapshot = $null
    if ($null -ne $installedAddress) {
        $urlAclSnapshots += Get-UrlAclSnapshot `
            -Prefix (Get-HttpPrefix -Value $installedAddress)
        $urlAclSnapshots += Get-UrlAclSnapshot `
            -Prefix (Get-RemoteHttpsPrefix -Address $installedAddress)
        $urlAclSnapshots += Get-UrlAclSnapshot `
            -Prefix (Get-RemoteHttpsHostNamePrefix `
                -HostName ([string]$installedIdentity.HostName))
        $httpsBindingSnapshot = Get-HttpsBindingSnapshot `
            -Address $installedAddress
        Assert-HttpsBindingCanBeManaged -Snapshot $httpsBindingSnapshot
    }
    $urlAclSnapshots += Get-UrlAclSnapshot `
        -Prefix "http://127.0.0.1:$servicePort/"
    foreach ($snapshot in $urlAclSnapshots) {
        Assert-UrlAclCanBeManaged -Snapshot $snapshot
    }

    Stop-ServiceAndWait -Name $watchdogServiceName
    Stop-ServiceAndWait -Name $mainServiceName
    if ($null -ne $installedAddress) {
        Remove-OwnedHttpsBinding -Address $installedAddress
        Remove-OwnedUrlAcl -Prefix (Get-HttpPrefix -Value $installedAddress)
        Remove-OwnedUrlAcl `
            -Prefix (Get-RemoteHttpsPrefix -Address $installedAddress)
        Remove-OwnedUrlAcl `
            -Prefix (Get-RemoteHttpsHostNamePrefix `
                -HostName ([string]$installedIdentity.HostName))
        if ($httpsBindingSnapshot.Exists) {
            Invoke-DirectoryCertificateRemove `
                -ExecutablePath (Join-Path $RequestedInstallRoot `
                    'DEEPAi.ServiceDirectory.Service.exe') `
                -Thumbprint ([string]$httpsBindingSnapshot.Thumbprint)
        }
    }
    Remove-OwnedUrlAcl -Prefix "http://127.0.0.1:$servicePort/"
    Remove-OwnedFirewallRule
    Remove-ServiceDefinition -Name $watchdogServiceName
    Remove-ServiceDefinition -Name $mainServiceName
    foreach ($serviceName in @($watchdogServiceName, $mainServiceName)) {
        if (Test-ServiceRegistrationExists -Name $serviceName) {
            throw "Windows Service '$serviceName' registration remains after uninstall."
        }
    }
    Remove-OperatorsGroup

    Remove-OwnedEventSource
    Remove-OwnedProductRegistration
    Remove-OwnedTrayRunValue -InstallRoot $RequestedInstallRoot

    if ($DeleteData -and (Test-Path -LiteralPath $RequestedDataRoot)) {
        Assert-NoReparsePoint -Path $RequestedDataRoot
        [void](Get-FileSystemTreeItems -Path $RequestedDataRoot)
        Remove-Item -LiteralPath $RequestedDataRoot -Recurse -Force
    }
}

function Invoke-Prepare {
    param(
        [Parameter(Mandatory = $true)][string]$RequestedInstallRoot,
        [Parameter(Mandatory = $true)][string]$RequestedDataRoot,
        [Parameter(Mandatory = $true)][string]$NewAddress,
        [Parameter(Mandatory = $true)][string]$StatePath
    )

    $paths = Assert-ExactInstallationPaths `
        -RequestedInstallRoot $RequestedInstallRoot `
        -RequestedDataRoot $RequestedDataRoot
    Assert-NoReparsePoint -Path $paths[0] -AllowMissing
    Assert-NoReparsePoint -Path $paths[1] -AllowMissing
    foreach ($root in $paths) {
        if (Test-Path -LiteralPath $root -PathType Container) {
            [void](Get-FileSystemTreeItems -Path $root)
        }
    }
    Write-SetupState -Path $StatePath -InstallRoot $paths[0] `
        -DataRoot $paths[1]
    Assert-InstallResourceOwnership -RequestedDataRoot $paths[1] `
        -NewAddress $NewAddress `
        -NewHostName (Get-LocalDirectoryHostName)
    $state = Read-SetupState -Path $StatePath -InstallRoot $paths[0] `
        -DataRoot $paths[1]
    try {
        Stop-ServiceAndWait -Name $watchdogServiceName
        Stop-ServiceAndWait -Name $mainServiceName
    }
    catch {
        $failure = $_
        try {
            Restore-ServiceRunningStates -State $state
            Remove-Item -LiteralPath $StatePath -Force
        }
        catch {
            throw [System.AggregateException]::new(
                'Setup preparation failed and service state restoration was incomplete.',
                @($failure.Exception, $_.Exception))
        }
        throw $failure
    }
}

function Invoke-Resume {
    param(
        [Parameter(Mandatory = $true)][string]$RequestedInstallRoot,
        [Parameter(Mandatory = $true)][string]$RequestedDataRoot,
        [Parameter(Mandatory = $true)][string]$StatePath
    )

    $paths = Assert-ExactInstallationPaths `
        -RequestedInstallRoot $RequestedInstallRoot `
        -RequestedDataRoot $RequestedDataRoot
    $state = Read-SetupState -Path $StatePath -InstallRoot $paths[0] `
        -DataRoot $paths[1]
    Restore-ServiceRunningStates -State $state
    Remove-Item -LiteralPath $StatePath -Force
}

switch ($Mode) {
    'ListAddresses' {
        if ([string]::IsNullOrWhiteSpace($OutputPath)) {
            throw 'ListAddresses requires OutputPath.'
        }

        $eligible = @(Get-EligibleAddresses)
        Write-Utf8Lines -Path $OutputPath -Lines $eligible
        if ($eligible.Count -eq 0) {
            throw 'No active Domain or Private interface has a supported IP address.'
        }
    }
    'ValidateAddress' {
        if ([string]::IsNullOrWhiteSpace($Address)) {
            throw 'ValidateAddress requires Address.'
        }

        $validated = Assert-AddressIsEligible -Value $Address
        if (-not [string]::IsNullOrWhiteSpace($OutputPath)) {
            Write-Utf8Lines -Path $OutputPath -Lines @($validated)
        }
    }
    'ReadAddress' {
        if ([string]::IsNullOrWhiteSpace($DataRoot) `
            -or [string]::IsNullOrWhiteSpace($OutputPath)) {
            throw 'ReadAddress requires DataRoot and OutputPath.'
        }

        $expectedDataRoot = [System.IO.Path]::GetFullPath(
            (Join-Path ([Environment]::GetFolderPath('CommonApplicationData')) `
                'DEEPAi\ServiceDirectory'))
        $actualDataRoot = [System.IO.Path]::GetFullPath($DataRoot.TrimEnd('\'))
        if (-not [StringComparer]::OrdinalIgnoreCase.Equals(
                $expectedDataRoot,
                $actualDataRoot)) {
            throw "The data root must be exactly '$expectedDataRoot'."
        }

        $currentAddress = Read-ConfigurationAddress `
            -ConfigPath (Join-Path $actualDataRoot 'config.xml')
        if ($null -eq $currentAddress) {
            $currentAddress = Get-RegisteredAddress
        }
        if ($null -ne $currentAddress) {
            Write-Utf8Lines -Path $OutputPath -Lines @($currentAddress)
        }
        else {
            Write-Utf8Lines -Path $OutputPath -Lines @()
        }
    }
    'Prepare' {
        if ([string]::IsNullOrWhiteSpace($Address) `
            -or [string]::IsNullOrWhiteSpace($InstallRoot) `
            -or [string]::IsNullOrWhiteSpace($DataRoot) `
            -or [string]::IsNullOrWhiteSpace($SetupStatePath)) {
            throw 'Prepare requires Address, InstallRoot, DataRoot and SetupStatePath.'
        }
        Invoke-Prepare -RequestedInstallRoot $InstallRoot `
            -RequestedDataRoot $DataRoot -NewAddress $Address `
            -StatePath $SetupStatePath
    }
    'Resume' {
        if ([string]::IsNullOrWhiteSpace($InstallRoot) `
            -or [string]::IsNullOrWhiteSpace($DataRoot) `
            -or [string]::IsNullOrWhiteSpace($SetupStatePath)) {
            throw 'Resume requires InstallRoot, DataRoot and SetupStatePath.'
        }
        Invoke-Resume -RequestedInstallRoot $InstallRoot `
            -RequestedDataRoot $DataRoot -StatePath $SetupStatePath
    }
    'Install' {
        if ([string]::IsNullOrWhiteSpace($Address) `
            -or [string]::IsNullOrWhiteSpace($InstallRoot) `
            -or [string]::IsNullOrWhiteSpace($DataRoot) `
            -or [string]::IsNullOrWhiteSpace($SetupStatePath)) {
            throw 'Install requires Address, InstallRoot, DataRoot and SetupStatePath.'
        }

        Invoke-Install `
            -RequestedInstallRoot $InstallRoot `
            -RequestedDataRoot $DataRoot `
            -NewAddress $Address `
            -StatePath $SetupStatePath `
            -RequestedCaRestorePath $CaRestorePath `
            -RequestedCaOperation $CaOperation
    }
    'Uninstall' {
        if ([string]::IsNullOrWhiteSpace($InstallRoot) `
            -or [string]::IsNullOrWhiteSpace($DataRoot)) {
            throw 'Uninstall requires InstallRoot and DataRoot.'
        }

        Invoke-Uninstall `
            -RequestedInstallRoot $InstallRoot `
            -RequestedDataRoot $DataRoot `
            -DeleteData $PurgeData.IsPresent
    }
}
