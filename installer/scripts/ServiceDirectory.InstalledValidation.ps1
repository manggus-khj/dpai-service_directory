#requires -Version 5.1

[CmdletBinding()]
param(
    [string]$OutputPath,
    [switch]$AllowStoppedServices
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$script:InstallerOwnerId = 'B44C6547-15D5-421A-88D7-3D2293BEE48C'
$script:MainServiceName = 'DEEPAi.ServiceDirectory'
$script:WatchdogServiceName = 'DEEPAi.ServiceDirectory.Watchdog'
$script:OperatorsGroupName = 'DEEPAi-ServiceDirectory-Operators'
$script:OperatorsGroupDescription =
    'Operators authorized to administer DEEPAi Service Directory.'
$script:FirewallRuleName = 'DEEPAi-ServiceDirectory-TCP-21000'
$script:FirewallRuleGroup = 'DEEPAi Service Directory'
$script:FirewallOwnerDescription =
    "Managed by DEEPAi Service Directory installer ($script:InstallerOwnerId)."
$script:ServicePort = 21000
$script:ValidationResults = New-Object `
    'System.Collections.Generic.List[object]'

function Add-InstalledValidationResult {
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)]
        [ValidateSet('PASS', 'FAIL', 'WARN')]
        [string]$Status,
        [Parameter(Mandatory = $true)][string]$Detail
    )

    [void]$script:ValidationResults.Add([pscustomobject][ordered]@{
            Name = $Name
            Status = $Status
            Detail = $Detail
        })
}

function Invoke-InstalledValidationCheck {
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][scriptblock]$Action
    )

    try {
        $detail = & $Action
        if ([string]::IsNullOrWhiteSpace([string]$detail)) {
            $detail = 'Validated.'
        }
        Add-InstalledValidationResult -Name $Name -Status PASS `
            -Detail ([string]$detail)
    }
    catch {
        Add-InstalledValidationResult -Name $Name -Status FAIL `
            -Detail $_.Exception.Message
    }
}

function Test-CanonicalInstalledIpv4 {
    param([string]$Value)

    return $null -ne $Value -and $Value -cmatch `
        '^(?:[0-9]|[1-9][0-9]|1[0-9]{2}|2[0-4][0-9]|25[0-5])(?:\.(?:[0-9]|[1-9][0-9]|1[0-9]{2}|2[0-4][0-9]|25[0-5])){3}$'
}

function Test-CanonicalInstalledHostName {
    param([string]$Value)

    if ($null -eq $Value `
        -or $Value.Length -lt 1 `
        -or $Value.Length -gt 253 `
        -or $Value -cne $Value.ToLowerInvariant() `
        -or $Value.EndsWith('.', [StringComparison]::Ordinal) `
        -or $Value -notmatch '[a-z]') {
        return $false
    }

    foreach ($label in $Value.Split('.')) {
        if ($label.Length -lt 1 `
            -or $label.Length -gt 63 `
            -or $label -cnotmatch '^[a-z0-9](?:[a-z0-9-]*[a-z0-9])?$') {
            return $false
        }
    }
    return $true
}

function Get-SingleInstalledXmlElementValue {
    param(
        [Parameter(Mandatory = $true)][System.Xml.XmlElement]$Root,
        [Parameter(Mandatory = $true)][string]$Name
    )

    $matches = @($Root.ChildNodes | Where-Object {
            $_.NodeType -eq [System.Xml.XmlNodeType]::Element `
                -and [StringComparer]::Ordinal.Equals($_.Name, $Name)
        })
    if ($matches.Count -ne 1 `
        -or $matches[0].Attributes.Count -ne 0 `
        -or $matches[0].ChildNodes.Count -ne 1 `
        -or $matches[0].FirstChild.NodeType `
            -ne [System.Xml.XmlNodeType]::Text) {
        throw "The XML document must contain one simple '$Name' element."
    }
    return [string]$matches[0].InnerText
}

function ConvertFrom-InstalledConfigurationXml {
    param([Parameter(Mandatory = $true)][string]$Xml)

    if ($Xml.Length -gt 65536) {
        throw 'config.xml exceeds the validation size limit.'
    }

    $settings = [System.Xml.XmlReaderSettings]::new()
    $settings.DtdProcessing = [System.Xml.DtdProcessing]::Prohibit
    $settings.XmlResolver = $null
    $settings.MaxCharactersInDocument = 65536
    $document = [System.Xml.XmlDocument]::new()
    $document.PreserveWhitespace = $true
    $document.XmlResolver = $null
    $stringReader = [System.IO.StringReader]::new($Xml)
    $reader = $null
    try {
        $reader = [System.Xml.XmlReader]::Create($stringReader, $settings)
        $document.Load($reader)
    }
    finally {
        if ($null -ne $reader) {
            $reader.Dispose()
        }
        $stringReader.Dispose()
    }

    $root = $document.DocumentElement
    if ($null -eq $root `
        -or -not [StringComparer]::Ordinal.Equals($root.Name, 'Config') `
        -or $root.Attributes.Count -ne 1 `
        -or -not [StringComparer]::Ordinal.Equals(
            $root.GetAttribute('SchemaVersion'), '1')) {
        throw 'config.xml root or SchemaVersion is invalid.'
    }

    $listenAddress = Get-SingleInstalledXmlElementValue `
        -Root $root -Name 'ListenAddress'
    $directoryHostName = Get-SingleInstalledXmlElementValue `
        -Root $root -Name 'DirectoryHostName'
    $directoryIpv4Address = Get-SingleInstalledXmlElementValue `
        -Root $root -Name 'DirectoryIpv4Address'
    if (-not (Test-CanonicalInstalledIpv4 $listenAddress) `
        -or -not (Test-CanonicalInstalledIpv4 $directoryIpv4Address) `
        -or -not [StringComparer]::Ordinal.Equals(
            $listenAddress, $directoryIpv4Address) `
        -or -not (Test-CanonicalInstalledHostName $directoryHostName)) {
        throw 'config.xml Directory identity is non-canonical or inconsistent.'
    }

    return [pscustomobject][ordered]@{
        ListenAddress = $listenAddress
        DirectoryHostName = $directoryHostName
        DirectoryIpv4Address = $directoryIpv4Address
    }
}

function ConvertFrom-HttpsBindingEvidence {
    param(
        [Parameter(Mandatory = $true)][string[]]$Lines,
        [Parameter(Mandatory = $true)][string]$Address
    )

    if (-not (Test-CanonicalInstalledIpv4 $Address)) {
        throw 'HTTPS binding validation requires canonical IPv4.'
    }
    $endpoint = "$Address`:$script:ServicePort"
    $values = New-Object 'System.Collections.Generic.List[string]'
    foreach ($lineValue in $Lines) {
        $match = [regex]::Match(
            [string]$lineValue,
            '^\s*.+?\s+:\s+(?<Value>.*?)\s*$',
            [System.Text.RegularExpressions.RegexOptions]::CultureInvariant)
        if ($match.Success) {
            [void]$values.Add($match.Groups['Value'].Value)
        }
    }

    $endpointCount = @($values | Where-Object {
            [StringComparer]::OrdinalIgnoreCase.Equals($_, $endpoint)
        }).Count
    $thumbprints = @($values | ForEach-Object {
            $candidate = $_ -replace '\s', ''
            if ($candidate -cmatch '^[0-9a-fA-F]{40}$') {
                $candidate.ToUpperInvariant()
            }
        } | Where-Object { $null -ne $_ } | Sort-Object -Unique)
    $applicationIds = @($values | Where-Object {
            $_ -match '^\{[0-9a-fA-F-]{36}\}$'
        } | ForEach-Object { $_.Trim('{}').ToUpperInvariant() } `
        | Sort-Object -Unique)
    if ($endpointCount -ne 1 `
        -or $thumbprints.Count -ne 1 `
        -or $applicationIds.Count -ne 1) {
        throw 'HTTP.sys did not return one exact endpoint, thumbprint and application ID.'
    }
    if (-not [StringComparer]::Ordinal.Equals(
            $applicationIds[0], $script:InstallerOwnerId)) {
        throw 'The HTTPS binding is owned by another application.'
    }

    return [pscustomobject][ordered]@{
        Endpoint = $endpoint
        Thumbprint = $thumbprints[0]
        ApplicationId = $applicationIds[0]
    }
}

function Test-ExactUrlAclEvidence {
    param(
        [Parameter(Mandatory = $true)][string[]]$Lines,
        [Parameter(Mandatory = $true)][string]$ExpectedServiceSid
    )

    $text = $Lines -join [Environment]::NewLine
    $matches = [regex]::Matches(
        $text,
        '(?im)^\s*SDDL\s*:\s*(?<Sddl>\S+)\s*$',
        [System.Text.RegularExpressions.RegexOptions]::CultureInvariant)
    $expectedSddl = "D:(A;;GX;;;$ExpectedServiceSid)"
    if ($matches.Count -ne 1 `
        -or -not [StringComparer]::Ordinal.Equals(
            $matches[0].Groups['Sddl'].Value,
            $expectedSddl)) {
        throw 'The URL ACL does not grant exact execute access to the main service SID.'
    }
    return $expectedSddl
}

function Get-InstalledUrlAclPrefixes {
    param([Parameter(Mandatory = $true)][object]$Configuration)

    if ([string]::IsNullOrWhiteSpace(
            [string]$Configuration.ListenAddress) `
        -or [string]::IsNullOrWhiteSpace(
            [string]$Configuration.DirectoryHostName)) {
        throw 'The installed Directory identity is unavailable for URL ACL validation.'
    }

    return @(
        "https://$($Configuration.ListenAddress):$script:ServicePort/",
        "https://$($Configuration.DirectoryHostName):$script:ServicePort/",
        "http://127.0.0.1:$script:ServicePort/")
}

function Get-InstalledCertificateAuthorityRole {
    param([Parameter(Mandatory = $true)][string]$Path)

    $bytes = [System.IO.File]::ReadAllBytes($Path)
    if ($bytes.Length -gt 16777216) {
        throw 'pki/state.xml exceeds the validation size limit.'
    }
    $strictUtf8 = [System.Text.UTF8Encoding]::new($false, $true)
    $xml = $strictUtf8.GetString($bytes)
    $settings = [System.Xml.XmlReaderSettings]::new()
    $settings.DtdProcessing = [System.Xml.DtdProcessing]::Prohibit
    $settings.XmlResolver = $null
    $settings.MaxCharactersInDocument = 16777216
    $document = [System.Xml.XmlDocument]::new()
    $document.XmlResolver = $null
    $stringReader = [System.IO.StringReader]::new($xml)
    $reader = $null
    try {
        $reader = [System.Xml.XmlReader]::Create($stringReader, $settings)
        $document.Load($reader)
    }
    finally {
        if ($null -ne $reader) {
            $reader.Dispose()
        }
        $stringReader.Dispose()
    }
    $root = $document.DocumentElement
    if ($null -eq $root `
        -or -not [StringComparer]::Ordinal.Equals(
            $root.Name, 'CertificateAuthorityState') `
        -or $root.Attributes.Count -ne 1 `
        -or -not [StringComparer]::Ordinal.Equals(
            $root.GetAttribute('SchemaVersion'), '1')) {
        throw 'pki/state.xml root or SchemaVersion is invalid.'
    }
    $role = Get-SingleInstalledXmlElementValue -Root $root -Name 'Role'
    if ($role -cnotin @('ACTIVE_ISSUER', 'STANDBY')) {
        throw 'pki/state.xml contains an invalid role.'
    }
    return $role
}

function Get-InstalledExecutableFromServicePath {
    param([Parameter(Mandatory = $true)][string]$PathName)

    $trimmed = $PathName.Trim()
    if ($trimmed.StartsWith('"', [StringComparison]::Ordinal)) {
        $closing = $trimmed.IndexOf('"', 1)
        if ($closing -lt 2 `
            -or -not [string]::IsNullOrWhiteSpace(
                $trimmed.Substring($closing + 1))) {
            throw 'The service command line is not one quoted executable path.'
        }
        return $trimmed.Substring(1, $closing - 1)
    }
    if ($trimmed.IndexOf(' ') -ge 0) {
        throw 'An unquoted service executable path contains spaces.'
    }
    return $trimmed
}

function Get-SafeInstalledFileEvidence {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$EvidenceName,
        [switch]$Secret
    )

    $item = Get-Item -LiteralPath $Path -Force -ErrorAction Stop
    if ($item.PSIsContainer `
        -or ($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) `
            -ne 0) {
        throw "Installed state file '$Path' is not a regular file."
    }
    $evidence = [ordered]@{
        RelativeName = $EvidenceName
        Length = [long]$item.Length
        Sha256 = $null
    }
    if (-not $Secret) {
        $evidence.Sha256 = (Get-FileHash -LiteralPath $Path `
            -Algorithm SHA256).Hash
    }
    return [pscustomobject]$evidence
}

function Test-InstalledRootAcl {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string[]]$ExpectedSids
    )

    $acl = Get-Acl -LiteralPath $Path -ErrorAction Stop
    $owner = $acl.GetOwner(
        [System.Security.Principal.SecurityIdentifier]).Value
    if (-not $acl.AreAccessRulesProtected `
        -or -not [StringComparer]::Ordinal.Equals(
            $owner, 'S-1-5-32-544')) {
        throw "The root ACL for '$Path' is not protected or Administrators-owned."
    }
    $actualSids = @($acl.GetAccessRules(
            $true,
            $false,
            [System.Security.Principal.SecurityIdentifier]) `
        | ForEach-Object { $_.IdentityReference.Value } | Sort-Object -Unique)
    $expected = @($ExpectedSids | Sort-Object -Unique)
    if (-not [StringComparer]::Ordinal.Equals(
            ($actualSids -join '|'), ($expected -join '|'))) {
        throw "The root ACL identity set for '$Path' is not exact."
    }
    return $acl.GetSecurityDescriptorSddlForm(
        [System.Security.AccessControl.AccessControlSections]::Owner `
            -bor [System.Security.AccessControl.AccessControlSections]::Access)
}

function Get-ServiceSidValue {
    param([Parameter(Mandatory = $true)][string]$ServiceName)

    return ([System.Security.Principal.NTAccount]::new(
            "NT SERVICE\$ServiceName").Translate(
            [System.Security.Principal.SecurityIdentifier])).Value
}

function Resolve-InstalledValidationReportPath {
    param([Parameter(Mandatory = $true)][string]$Path)

    if ($Path -cnotmatch '^[A-Za-z]:[\\/]' `
        -or -not [StringComparer]::OrdinalIgnoreCase.Equals(
            [System.IO.Path]::GetExtension($Path), '.json')) {
        throw 'OutputPath must be an absolute local .json path.'
    }
    $fullPath = [System.IO.Path]::GetFullPath($Path)
    $reportDrive = [System.IO.DriveInfo]::new(
        [System.IO.Path]::GetPathRoot($fullPath))
    if ($reportDrive.DriveType -eq [System.IO.DriveType]::Network) {
        throw 'OutputPath must be on a local drive.'
    }
    if (Test-Path -LiteralPath $fullPath) {
        throw 'OutputPath already exists; validation evidence is never overwritten.'
    }
    $reportDirectory = [System.IO.Path]::GetDirectoryName($fullPath)
    if (-not (Test-Path -LiteralPath $reportDirectory -PathType Container)) {
        throw 'OutputPath parent directory does not exist.'
    }
    return $fullPath
}

function Invoke-ServiceDirectoryInstalledValidation {
    param(
        [Parameter(Mandatory = $true)][string]$ReportPath,
        [switch]$PermitStoppedServices
    )

    $fullReportPath = Resolve-InstalledValidationReportPath -Path $ReportPath

    $installRoot = Join-Path ${env:ProgramFiles} 'DEEPAi\ServiceDirectory'
    $dataRoot = Join-Path `
        ([Environment]::GetFolderPath('CommonApplicationData')) `
        'DEEPAi\ServiceDirectory'
    $configPath = Join-Path $dataRoot 'config.xml'
    $mainExecutable = Join-Path $installRoot `
        'DEEPAi.ServiceDirectory.Service.exe'
    $watchdogExecutable = Join-Path $installRoot `
        'DEEPAi.ServiceDirectory.Watchdog.exe'
    $script:ValidationResults.Clear()

    Invoke-InstalledValidationCheck -Name 'Process.Administrator' -Action {
        $principal = [System.Security.Principal.WindowsPrincipal]::new(
            [System.Security.Principal.WindowsIdentity]::GetCurrent())
        if (-not $principal.IsInRole(
                [System.Security.Principal.WindowsBuiltInRole]::Administrator)) {
            throw 'Run the validation tool from an elevated PowerShell session.'
        }
        'The validation process is elevated.'
    }

    $script:InstalledOperatingSystem = $null
    Invoke-InstalledValidationCheck -Name 'Platform.OperatingSystem' -Action {
        $script:InstalledOperatingSystem = Get-CimInstance `
            Win32_OperatingSystem
        if (-not [Environment]::Is64BitOperatingSystem) {
            throw 'The installed operating system is not x64.'
        }
        $version = [Version]$script:InstalledOperatingSystem.Version
        if ($version.Major -lt 10 `
            -or ($version.Major -eq 10 -and $version.Build -lt 14393)) {
            throw 'The Windows build is below the installer minimum.'
        }
        "$($script:InstalledOperatingSystem.Caption) " `
            + "$($script:InstalledOperatingSystem.Version)"
    }
    Invoke-InstalledValidationCheck -Name 'Platform.DotNet48' -Action {
        $release = (Get-ItemProperty -LiteralPath `
            'HKLM:\SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full' `
            -Name Release -ErrorAction Stop).Release
        if ([int]$release -lt 528040) {
            throw '.NET Framework 4.8 or later is not installed.'
        }
        "Release=$release"
    }

    $script:InstalledConfiguration = $null
    Invoke-InstalledValidationCheck -Name 'State.Configuration' -Action {
        $bytes = [System.IO.File]::ReadAllBytes($configPath)
        $strictUtf8 = [System.Text.UTF8Encoding]::new($false, $true)
        $script:InstalledConfiguration = ConvertFrom-InstalledConfigurationXml `
            -Xml $strictUtf8.GetString($bytes)
        "ListenAddress=$($script:InstalledConfiguration.ListenAddress); " `
            + "DirectoryHostName=$($script:InstalledConfiguration.DirectoryHostName)"
    }

    $script:InstalledMainServiceSid = $null
    $script:InstalledWatchdogServiceSid = $null
    Invoke-InstalledValidationCheck -Name 'Identity.ServiceSids' -Action {
        $script:InstalledMainServiceSid = Get-ServiceSidValue `
            $script:MainServiceName
        $script:InstalledWatchdogServiceSid = Get-ServiceSidValue `
            $script:WatchdogServiceName
        'Both service SIDs resolve.'
    }

    $services = @(
        [pscustomobject]@{
            Name = $script:MainServiceName
            Executable = $mainExecutable
            DelayedAutoStart = 1
        },
        [pscustomobject]@{
            Name = $script:WatchdogServiceName
            Executable = $watchdogExecutable
            DelayedAutoStart = 0
        })
    foreach ($expectedService in $services) {
        Invoke-InstalledValidationCheck `
            -Name "SCM.$($expectedService.Name)" -Action {
            $service = Get-CimInstance Win32_Service -Filter `
                "Name='$($expectedService.Name)'"
            if ($null -eq $service) {
                throw 'The Windows Service registration is missing.'
            }
            $actualExecutable = Get-InstalledExecutableFromServicePath `
                -PathName ([string]$service.PathName)
            if (-not (Test-Path -LiteralPath $expectedService.Executable `
                    -PathType Leaf) `
                -or -not [StringComparer]::OrdinalIgnoreCase.Equals(
                    [System.IO.Path]::GetFullPath($actualExecutable),
                    [System.IO.Path]::GetFullPath($expectedService.Executable)) `
                -or -not [StringComparer]::OrdinalIgnoreCase.Equals(
                    [string]$service.StartName,
                    "NT SERVICE\$($expectedService.Name)") `
                -or -not [StringComparer]::OrdinalIgnoreCase.Equals(
                    [string]$service.StartMode, 'Auto') `
                -or -not [StringComparer]::OrdinalIgnoreCase.Equals(
                    [string]$service.ServiceType, 'Own Process')) {
                throw 'The service executable, path, account, type or start mode is not exact.'
            }
            $serviceRegistryPath =
                "HKLM:\SYSTEM\CurrentControlSet\Services\$($expectedService.Name)"
            $serviceKey = Get-Item -LiteralPath $serviceRegistryPath `
                -ErrorAction Stop
            try {
                $valueNames = @($serviceKey.GetValueNames())
                $hasDelayedAutoStart = $valueNames -contains 'DelayedAutoStart'
                if ($expectedService.DelayedAutoStart -eq 1) {
                    if (-not $hasDelayedAutoStart `
                        -or $serviceKey.GetValueKind('DelayedAutoStart') `
                            -ne [Microsoft.Win32.RegistryValueKind]::DWord `
                        -or [int]$serviceKey.GetValue('DelayedAutoStart') -ne 1) {
                        throw 'The main service is not configured for delayed automatic start.'
                    }
                }
                elseif ($hasDelayedAutoStart `
                    -and ($serviceKey.GetValueKind('DelayedAutoStart') `
                        -ne [Microsoft.Win32.RegistryValueKind]::DWord `
                    -or [int]$serviceKey.GetValue('DelayedAutoStart') -ne 0)) {
                    throw 'The watchdog service has an invalid delayed-start value.'
                }
                if ($valueNames -notcontains 'ServiceSidType' `
                    -or $serviceKey.GetValueKind('ServiceSidType') `
                        -ne [Microsoft.Win32.RegistryValueKind]::DWord `
                    -or [int]$serviceKey.GetValue('ServiceSidType') -ne 1) {
                    throw 'The service SID type is not unrestricted.'
                }
            }
            finally {
                $serviceKey.Dispose()
            }
            if (-not $PermitStoppedServices `
                -and -not [StringComparer]::OrdinalIgnoreCase.Equals(
                    [string]$service.State, 'Running')) {
                throw 'The service is not running.'
            }
            "State=$($service.State); StartMode=$($service.StartMode)"
        }
    }

    Invoke-InstalledValidationCheck -Name 'Registry.Product' -Action {
        $productKey = Get-Item -LiteralPath `
            'HKLM:\SOFTWARE\DEEPAi\ServiceDirectory' -ErrorAction Stop
        try {
            $valueNames = @($productKey.GetValueNames() | Sort-Object)
            if ($null -eq $script:InstalledConfiguration `
                -or -not [StringComparer]::Ordinal.Equals(
                    ($valueNames -join '|'),
                    'InstallerOwnerId|ListenAddress') `
                -or $productKey.GetValueKind('InstallerOwnerId') `
                    -ne [Microsoft.Win32.RegistryValueKind]::String `
                -or $productKey.GetValueKind('ListenAddress') `
                    -ne [Microsoft.Win32.RegistryValueKind]::String `
                -or -not [StringComparer]::Ordinal.Equals(
                    [string]$productKey.GetValue('InstallerOwnerId'),
                    $script:InstallerOwnerId) `
                -or -not [StringComparer]::Ordinal.Equals(
                    [string]$productKey.GetValue('ListenAddress'),
                    [string]$script:InstalledConfiguration.ListenAddress)) {
                throw 'The product registration is foreign, non-canonical or disagrees with config.xml.'
            }
            "ListenAddress=$($productKey.GetValue('ListenAddress'))"
        }
        finally {
            $productKey.Dispose()
        }
    }
    Invoke-InstalledValidationCheck -Name 'Registry.SecurityEventSource' -Action {
        $eventKey = Get-Item -LiteralPath `
            'HKLM:\SYSTEM\CurrentControlSet\Services\EventLog\Application\DEEPAi.ServiceDirectory.Security' `
            -ErrorAction Stop
        try {
            $valueNames = @($eventKey.GetValueNames() | Sort-Object)
            if (-not [StringComparer]::Ordinal.Equals(
                    ($valueNames -join '|'),
                    'EventMessageFile|InstallerOwnerId|TypesSupported') `
                -or $eventKey.GetValueKind('InstallerOwnerId') `
                    -ne [Microsoft.Win32.RegistryValueKind]::String `
                -or $eventKey.GetValueKind('EventMessageFile') `
                    -ne [Microsoft.Win32.RegistryValueKind]::ExpandString `
                -or $eventKey.GetValueKind('TypesSupported') `
                    -ne [Microsoft.Win32.RegistryValueKind]::DWord `
                -or -not [StringComparer]::Ordinal.Equals(
                    [string]$eventKey.GetValue('InstallerOwnerId'),
                    $script:InstallerOwnerId) `
                -or -not [StringComparer]::Ordinal.Equals(
                    [string]$eventKey.GetValue(
                        'EventMessageFile',
                        $null,
                        [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames),
                    '%SystemRoot%\Microsoft.NET\Framework64\v4.0.30319\EventLogMessages.dll') `
                -or [int]$eventKey.GetValue('TypesSupported') -ne 7) {
                throw 'The security Event Log source registration is not exact or installer-owned.'
            }
        }
        finally {
            $eventKey.Dispose()
        }
        'The exact security Event Log source exists.'
    }
    Invoke-InstalledValidationCheck -Name 'Identity.OperatorsGroup' -Action {
        $group = Get-CimInstance Win32_Group -Filter `
            "LocalAccount=True AND Name='$script:OperatorsGroupName'"
        if ($null -eq $group `
            -or -not [StringComparer]::Ordinal.Equals(
                [string]$group.Description,
                $script:OperatorsGroupDescription)) {
            throw 'The local operators group is missing or is not installer-owned.'
        }
        "SID=$($group.SID)"
    }

    Invoke-InstalledValidationCheck -Name 'Acl.InstallRoot' -Action {
        if ($null -eq $script:InstalledMainServiceSid `
            -or $null -eq $script:InstalledWatchdogServiceSid) {
            throw 'Service SIDs are unavailable.'
        }
        Test-InstalledRootAcl -Path $installRoot -ExpectedSids @(
            'S-1-5-18',
            'S-1-5-32-544',
            'S-1-5-32-545',
            $script:InstalledMainServiceSid,
            $script:InstalledWatchdogServiceSid)
    }
    Invoke-InstalledValidationCheck -Name 'Acl.DataRoot' -Action {
        if ($null -eq $script:InstalledMainServiceSid) {
            throw 'The main service SID is unavailable.'
        }
        Test-InstalledRootAcl -Path $dataRoot -ExpectedSids @(
            'S-1-5-18',
            'S-1-5-32-544',
            $script:InstalledMainServiceSid)
    }

    $stateEvidence = New-Object 'System.Collections.Generic.List[object]'
    $requiredNonSecretFiles = @(
        'directory.xml',
        'config.xml',
        'pki\state.xml',
        'pki\ca.der',
        'pki\crl.der')
    foreach ($relativePath in $requiredNonSecretFiles) {
        Invoke-InstalledValidationCheck -Name "File.$relativePath" -Action {
            $evidence = Get-SafeInstalledFileEvidence `
                -Path (Join-Path $dataRoot $relativePath) `
                -EvidenceName $relativePath
            [void]$stateEvidence.Add($evidence)
            "Length=$($evidence.Length); SHA256=$($evidence.Sha256)"
        }
    }
    foreach ($forbiddenPath in @(
            'pending.xml',
            'pending.xml.bak',
            'secrets\peer.dat.bak',
            'secrets\ca.key.bak')) {
        Invoke-InstalledValidationCheck -Name "Forbidden.$forbiddenPath" `
            -Action {
            if (Test-Path -LiteralPath (Join-Path $dataRoot $forbiddenPath)) {
                throw "Forbidden artifact '$forbiddenPath' exists."
            }
            'Absent.'
        }
    }
    Invoke-InstalledValidationCheck -Name 'State.CertificateAuthorityRole' `
        -Action {
        $role = Get-InstalledCertificateAuthorityRole `
            -Path (Join-Path $dataRoot 'pki\state.xml')
        $ledgerPath = Join-Path $dataRoot 'pki\ledger.xml'
        $peerCachePath = Join-Path $dataRoot 'pki\peer-cache.xml'
        $caKeyPath = Join-Path $dataRoot 'secrets\ca.key'
        if ($role -eq 'ACTIVE_ISSUER') {
            if (-not (Test-Path -LiteralPath $ledgerPath -PathType Leaf) `
                -or -not (Test-Path -LiteralPath $caKeyPath -PathType Leaf) `
                -or (Test-Path -LiteralPath $peerCachePath)) {
                throw 'Active issuer role files are incomplete or contain standby cache.'
            }
            $roleEvidence = Get-SafeInstalledFileEvidence `
                -Path $ledgerPath -EvidenceName 'pki\ledger.xml'
        }
        elseif (-not (Test-Path -LiteralPath $peerCachePath -PathType Leaf) `
            -or (Test-Path -LiteralPath $ledgerPath) `
            -or (Test-Path -LiteralPath $caKeyPath)) {
            throw 'Standby role files are incomplete or contain active issuer secrets.'
        }
        else {
            $roleEvidence = Get-SafeInstalledFileEvidence `
                -Path $peerCachePath -EvidenceName 'pki\peer-cache.xml'
        }
        [void]$stateEvidence.Add($roleEvidence)
        "Role=$role"
    }
    foreach ($secretPath in @('secrets\ca.key', 'secrets\peer.dat')) {
        $absoluteSecretPath = Join-Path $dataRoot $secretPath
        if (Test-Path -LiteralPath $absoluteSecretPath -PathType Leaf) {
            Invoke-InstalledValidationCheck -Name "Secret.$secretPath" `
                -Action {
                $evidence = Get-SafeInstalledFileEvidence `
                    -Path $absoluteSecretPath `
                    -EvidenceName $secretPath -Secret
                [void]$stateEvidence.Add($evidence)
                "Length=$($evidence.Length); content and hash not collected"
            }
        }
    }

    $script:InstalledHttpsBinding = $null
    Invoke-InstalledValidationCheck -Name 'HttpSys.HttpsBinding' -Action {
        if ($null -eq $script:InstalledConfiguration) {
            throw 'The installed ListenAddress is unavailable.'
        }
        $netshOutput = @(& "$env:SystemRoot\System32\netsh.exe" `
            http show sslcert `
            "ipport=$($script:InstalledConfiguration.ListenAddress):$script:ServicePort" 2>&1)
        if ($LASTEXITCODE -ne 0) {
            throw 'The exact HTTP.sys HTTPS binding could not be queried.'
        }
        $script:InstalledHttpsBinding = ConvertFrom-HttpsBindingEvidence `
            -Lines @($netshOutput | ForEach-Object { [string]$_ }) `
            -Address $script:InstalledConfiguration.ListenAddress
        "Endpoint=$($script:InstalledHttpsBinding.Endpoint); " `
            + "Thumbprint=$($script:InstalledHttpsBinding.Thumbprint)"
    }
    Invoke-InstalledValidationCheck -Name 'Certificate.DirectoryLeaf' -Action {
        if ($null -eq $script:InstalledHttpsBinding) {
            throw 'HTTPS binding evidence is unavailable.'
        }
        $certificate = Get-Item -LiteralPath `
            "Cert:\LocalMachine\My\$($script:InstalledHttpsBinding.Thumbprint)" `
            -ErrorAction Stop
        if (-not $certificate.HasPrivateKey `
            -or $certificate.NotBefore.ToUniversalTime() -gt [DateTime]::UtcNow `
            -or $certificate.NotAfter.ToUniversalTime() -le [DateTime]::UtcNow) {
            throw 'The bound Directory certificate is unusable.'
        }
        "Subject=$($certificate.Subject); NotAfterUtc=" `
            + $certificate.NotAfter.ToUniversalTime().ToString('o')
    }

    $installedUrlAclPrefixes = if (
        $null -eq $script:InstalledConfiguration) {
        @("http://127.0.0.1:$script:ServicePort/")
    } else {
        @(Get-InstalledUrlAclPrefixes `
            -Configuration $script:InstalledConfiguration)
    }
    foreach ($prefix in $installedUrlAclPrefixes) {
        Invoke-InstalledValidationCheck -Name "HttpSys.UrlAcl.$prefix" `
            -Action {
            if ($null -eq $script:InstalledMainServiceSid) {
                throw 'The main service SID is unavailable.'
            }
            $urlAclOutput = @(& "$env:SystemRoot\System32\netsh.exe" `
                http show urlacl "url=$prefix" 2>&1)
            if ($LASTEXITCODE -ne 0) {
                throw "The exact URL ACL '$prefix' could not be queried."
            }
            Test-ExactUrlAclEvidence `
                -Lines @($urlAclOutput | ForEach-Object { [string]$_ }) `
                -ExpectedServiceSid $script:InstalledMainServiceSid
        }
    }

    Invoke-InstalledValidationCheck -Name 'Firewall.RemoteHttps' -Action {
        if ($null -eq $script:InstalledConfiguration) {
            throw 'The installed ListenAddress is unavailable.'
        }
        $rules = @(Get-NetFirewallRule -Name $script:FirewallRuleName `
                -ErrorAction Stop)
        if ($rules.Count -ne 1) {
            throw 'The exact firewall rule is missing or duplicated.'
        }
        $rule = $rules[0]
        $portFilters = @($rule | Get-NetFirewallPortFilter)
        $addressFilters = @($rule | Get-NetFirewallAddressFilter)
        $applicationFilters = @($rule | Get-NetFirewallApplicationFilter)
        $serviceFilters = @($rule | Get-NetFirewallServiceFilter)
        if ($portFilters.Count -ne 1 `
            -or $addressFilters.Count -ne 1 `
            -or $applicationFilters.Count -ne 1 `
            -or $serviceFilters.Count -ne 1) {
            throw 'The firewall rule has an unexpected filter shape.'
        }
        $portFilter = $portFilters[0]
        $addressFilter = $addressFilters[0]
        $applicationFilter = $applicationFilters[0]
        $serviceFilter = $serviceFilters[0]
        if ([string]$rule.Enabled -ne 'True' `
            -or [string]$rule.Direction -ne 'Inbound' `
            -or [string]$rule.Action -ne 'Allow' `
            -or [int]$rule.Profile -ne 3 `
            -or [string]$rule.EdgeTraversalPolicy -ne 'Block' `
            -or -not [StringComparer]::Ordinal.Equals(
                [string]$rule.Description,
                $script:FirewallOwnerDescription) `
            -or -not [StringComparer]::Ordinal.Equals(
                [string]$rule.Group,
                $script:FirewallRuleGroup) `
            -or -not [StringComparer]::OrdinalIgnoreCase.Equals(
                [string]$portFilter.Protocol, 'TCP') `
            -or [string]$portFilter.LocalPort -ne '21000' `
            -or -not [StringComparer]::Ordinal.Equals(
                [string]$addressFilter.LocalAddress,
                [string]$script:InstalledConfiguration.ListenAddress) `
            -or [string]$addressFilter.RemoteAddress -ne 'Any' `
            -or [string]$portFilter.RemotePort -ne 'Any' `
            -or -not [StringComparer]::OrdinalIgnoreCase.Equals(
                [string]$applicationFilter.Program,
                [string]$mainExecutable) `
            -or -not [StringComparer]::Ordinal.Equals(
                [string]$serviceFilter.Service,
                $script:MainServiceName)) {
            throw 'The firewall rule does not match the exact HTTPS boundary.'
        }
        "Profiles=$($rule.Profile); LocalAddress=$($addressFilter.LocalAddress)"
    }

    $failedCount = @($script:ValidationResults | Where-Object {
            $_.Status -eq 'FAIL'
        }).Count
    $warningCount = @($script:ValidationResults | Where-Object {
            $_.Status -eq 'WARN'
        }).Count
    $report = [pscustomobject][ordered]@{
        SchemaVersion = 1
        Product = 'DEEPAi Service Directory'
        CapturedAt = [DateTimeOffset]::Now.ToString('o')
        MachineName = [Environment]::MachineName
        OverallStatus = if ($failedCount -eq 0) { 'PASS' } else { 'FAIL' }
        FailedCount = $failedCount
        WarningCount = $warningCount
        Checks = $script:ValidationResults.ToArray()
        FileEvidence = $stateEvidence.ToArray()
    }
    $json = $report | ConvertTo-Json -Depth 6
    $temporaryPath = $fullReportPath + '.preparing'
    if (Test-Path -LiteralPath $temporaryPath) {
        throw 'The validation report staging path already exists.'
    }
    try {
        [System.IO.File]::WriteAllText(
            $temporaryPath,
            $json + [Environment]::NewLine,
            [System.Text.UTF8Encoding]::new($false))
        Move-Item -LiteralPath $temporaryPath -Destination $fullReportPath
    }
    finally {
        if (Test-Path -LiteralPath $temporaryPath) {
            Remove-Item -LiteralPath $temporaryPath -Force
        }
    }

    Write-Host "Installed validation report: $fullReportPath"
    if ($failedCount -ne 0) {
        return 2
    }
    return 0
}

if ($MyInvocation.InvocationName -ne '.') {
    if ([string]::IsNullOrWhiteSpace($OutputPath)) {
        throw 'OutputPath is required.'
    }
    exit (Invoke-ServiceDirectoryInstalledValidation `
        -ReportPath $OutputPath `
        -PermitStoppedServices:$AllowStoppedServices)
}
