#requires -Version 5.1

[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repositoryRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$helperPath = Join-Path $repositoryRoot `
    'installer\scripts\ServiceDirectory.HttpsBinding.ps1'
if (-not (Test-Path -LiteralPath $helperPath -PathType Leaf)) {
    throw "HTTPS binding helper '$helperPath' was not found."
}

$installerOwnerId = 'B44C6547-15D5-421A-88D7-3D2293BEE48C'
$servicePort = 21000
$script:bindings = @{}

function ConvertTo-CanonicalAddress {
    param([Parameter(Mandatory = $true)][string]$Value)

    if ($Value -cnotmatch `
        '^(?:[0-9]|[1-9][0-9]|1[0-9]{2}|2[0-4][0-9]|25[0-5])(?:\.(?:[0-9]|[1-9][0-9]|1[0-9]{2}|2[0-4][0-9]|25[0-5])){3}$') {
        throw 'Test address is not canonical IPv4.'
    }
    return $Value
}

. $helperPath

function Get-HttpsBindingSnapshot {
    param([Parameter(Mandatory = $true)][string]$Address)

    $endpoint = Get-HttpsBindingEndpoint -Address $Address
    if (-not $script:bindings.ContainsKey($endpoint)) {
        return [pscustomobject]@{
            Address = $Address
            Endpoint = $endpoint
            Exists = $false
            Owned = $false
            Valid = $false
            Thumbprint = $null
        }
    }

    return [pscustomobject]@{
        Address = $Address
        Endpoint = $endpoint
        Exists = $true
        Owned = [bool]$script:bindings[$endpoint].Owned
        Valid = [bool]$script:bindings[$endpoint].Valid
        Thumbprint = [string]$script:bindings[$endpoint].Thumbprint
    }
}

function Invoke-NativeCommand {
    param(
        [Parameter(Mandatory = $true)][string]$FilePath,
        [Parameter(Mandatory = $true)][string[]]$Arguments,
        [int[]]$AllowedExitCodes = @(0)
    )

    if ($Arguments[0] -eq '--repair-directory-certificate-install') {
        return 'DIRECTORY_CERTIFICATE_INSTALLED ' `
            + ('A' * 40) + ' ' + ('B' * 32) `
            + ' 2027-07-21T00:00:00.0000000Z'
    }
    if ($Arguments[0] -eq '--repair-directory-certificate-remove') {
        return "DIRECTORY_CERTIFICATE_REMOVED $($Arguments[1])"
    }

    if ($Arguments[0] -ne 'http') {
        throw 'Unexpected native command in HTTPS binding test.'
    }
    $endpoint = ($Arguments | Where-Object {
            $_.StartsWith('ipport=', [StringComparison]::Ordinal)
        } | Select-Object -First 1).Substring('ipport='.Length)
    if ($Arguments[1] -eq 'delete') {
        [void]$script:bindings.Remove($endpoint)
        return @()
    }
    if ($Arguments[1] -eq 'add') {
        $thumbprint = ($Arguments | Where-Object {
                $_.StartsWith('certhash=', [StringComparison]::Ordinal)
            } | Select-Object -First 1).Substring('certhash='.Length)
        $script:bindings[$endpoint] = [pscustomobject]@{
            Owned = $true
            Valid = $true
            Thumbprint = $thumbprint
        }
        return @()
    }

    throw 'Unexpected HTTP.sys operation in HTTPS binding test.'
}

$address = '10.20.30.40'
$endpoint = "$address`:21000"
$oldThumbprint = '1' * 40
$newThumbprint = '2' * 40

if (-not [StringComparer]::Ordinal.Equals(
        (Get-RemoteHttpsPrefix -Address $address),
        'https://10.20.30.40:21000/')) {
    throw 'Remote HTTPS prefix formatting changed.'
}
if (-not [StringComparer]::Ordinal.Equals(
        (Get-RemoteHttpsHostNamePrefix `
            -HostName 'Management.Example.Local'),
        'https://management.example.local:21000/')) {
    throw 'Remote hostname HTTPS prefix formatting changed.'
}

foreach ($invalidHostName in @(
        ' management.example.local',
        '*.example.local',
        '10.20.30.40')) {
    $hostNameRejected = $false
    try {
        [void](Get-RemoteHttpsHostNamePrefix -HostName $invalidHostName)
    }
    catch {
        $hostNameRejected = $true
    }
    if (-not $hostNameRejected) {
        throw "Invalid Directory hostname '$invalidHostName' was accepted."
    }
}

$parsedValues = @(Get-HttpsBindingOutputValues -Lines @(
        '    IP:port                      : 10.20.30.40:21000',
        "    Certificate Hash             : $newThumbprint",
        '    Application ID               : {B44C6547-15D5-421A-88D7-3D2293BEE48C}'))
if ($parsedValues.Count -ne 3 `
    -or -not [StringComparer]::Ordinal.Equals(
        $parsedValues[0], '10.20.30.40:21000') `
    -or -not [StringComparer]::Ordinal.Equals(
        $parsedValues[1], $newThumbprint)) {
    throw 'Localized netsh value separation confused IP:port label or value colons.'
}

Set-OwnedHttpsBinding -Address $address -Thumbprint $newThumbprint
if (-not [StringComparer]::Ordinal.Equals(
        [string]$script:bindings[$endpoint].Thumbprint,
        $newThumbprint)) {
    throw 'Owned HTTPS binding installation did not publish the new thumbprint.'
}

$oldSnapshot = [pscustomobject]@{
    Address = $address
    Endpoint = $endpoint
    Exists = $true
    Owned = $true
    Valid = $true
    Thumbprint = $oldThumbprint
}
Restore-HttpsBindingSnapshot -Snapshot $oldSnapshot
if (-not [StringComparer]::Ordinal.Equals(
        [string]$script:bindings[$endpoint].Thumbprint,
        $oldThumbprint)) {
    throw 'HTTPS binding rollback did not restore the old thumbprint.'
}

$script:bindings[$endpoint] = [pscustomobject]@{
    Owned = $false
    Valid = $false
    Thumbprint = '3' * 40
}
$foreignRejected = $false
try {
    Set-OwnedHttpsBinding -Address $address -Thumbprint $newThumbprint
}
catch {
    $foreignRejected = $true
}
if (-not $foreignRejected) {
    throw 'A foreign HTTPS binding was not rejected.'
}

$installed = Invoke-DirectoryCertificateInstall -ExecutablePath 'fake.exe'
if (-not [StringComparer]::Ordinal.Equals(
        [string]$installed.Thumbprint,
        ('A' * 40))) {
    throw 'Directory certificate install result parsing changed.'
}
Invoke-DirectoryCertificateRemove `
    -ExecutablePath 'fake.exe' `
    -Thumbprint ([string]$installed.Thumbprint)

Write-Host 'Installer HTTPS binding rollback tests passed.'
