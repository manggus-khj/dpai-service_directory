#requires -Version 5.1

[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repositoryRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$helperPath = Join-Path $repositoryRoot `
    'installer\scripts\ServiceDirectory.InstalledValidation.ps1'
if (-not (Test-Path -LiteralPath $helperPath -PathType Leaf)) {
    throw "Installed validation helper '$helperPath' was not found."
}
. $helperPath

$config = ConvertFrom-InstalledConfigurationXml -Xml @'
<?xml version="1.0" encoding="utf-8"?>
<Config SchemaVersion="1"><ListenAddress>10.20.30.40</ListenAddress><DirectoryHostName>management.example.local</DirectoryHostName><DirectoryIpv4Address>10.20.30.40</DirectoryIpv4Address><InstanceId>4ed36c2a-84d0-4fdb-94ef-8e25a8ee0da1</InstanceId><LastPeerKeyEpoch>0</LastPeerKeyEpoch><LogRetentionDays>30</LogRetentionDays><Sync><State>Unpaired</State><LastResult>NOT_RUN</LastResult><LastPeerNotificationOperation>NONE</LastPeerNotificationOperation><LastPeerNotificationResult>NOT_RUN</LastPeerNotificationResult></Sync></Config>
'@
if (-not [StringComparer]::Ordinal.Equals(
        [string]$config.ListenAddress,
        '10.20.30.40') `
    -or -not [StringComparer]::Ordinal.Equals(
        [string]$config.DirectoryHostName,
        'management.example.local')) {
    throw 'Canonical installed configuration parsing changed.'
}

foreach ($invalidXml in @(
        '<!DOCTYPE Config [<!ENTITY x "bad">]><Config SchemaVersion="1"><ListenAddress>&x;</ListenAddress></Config>',
        '<Config SchemaVersion="1"><ListenAddress>10.20.30.40</ListenAddress><DirectoryHostName>Management.example.local</DirectoryHostName><DirectoryIpv4Address>10.20.30.40</DirectoryIpv4Address></Config>',
        '<Config SchemaVersion="1"><ListenAddress>10.20.30.40</ListenAddress><DirectoryHostName>management.example.local</DirectoryHostName><DirectoryIpv4Address>10.20.30.41</DirectoryIpv4Address></Config>')) {
    $rejected = $false
    try {
        [void](ConvertFrom-InstalledConfigurationXml -Xml $invalidXml)
    }
    catch {
        $rejected = $true
    }
    if (-not $rejected) {
        throw 'Invalid installed configuration evidence was accepted.'
    }
}

$thumbprint = 'A' * 40
$binding = ConvertFrom-HttpsBindingEvidence -Address '10.20.30.40' -Lines @(
    '    IP:port                      : 10.20.30.40:21000',
    "    Certificate Hash             : $thumbprint",
    '    Application ID               : {B44C6547-15D5-421A-88D7-3D2293BEE48C}')
if (-not [StringComparer]::Ordinal.Equals(
        [string]$binding.Thumbprint,
        $thumbprint)) {
    throw 'Canonical HTTPS binding evidence parsing changed.'
}

$foreignRejected = $false
try {
    [void](ConvertFrom-HttpsBindingEvidence `
        -Address '10.20.30.40' `
        -Lines @(
            'IP:port : 10.20.30.40:21000',
            "Certificate Hash : $thumbprint",
            'Application ID : {11111111-1111-1111-1111-111111111111}'))
}
catch {
    $foreignRejected = $true
}
if (-not $foreignRejected) {
    throw 'A foreign HTTPS binding owner was accepted.'
}

$serviceSid = 'S-1-5-80-1-2-3-4-5'
$urlAclSddl = Test-ExactUrlAclEvidence `
    -Lines @("    SDDL: D:(A;;GX;;;$serviceSid)") `
    -ExpectedServiceSid $serviceSid
if (-not [StringComparer]::Ordinal.Equals(
        $urlAclSddl,
        "D:(A;;GX;;;$serviceSid)")) {
    throw 'Canonical URL ACL evidence parsing changed.'
}

$expectedPrefixes = @(
    'https://10.20.30.40:21000/',
    'https://management.example.local:21000/',
    'http://127.0.0.1:21000/')
$actualPrefixes = @(Get-InstalledUrlAclPrefixes -Configuration $config)
if ($actualPrefixes.Count -ne $expectedPrefixes.Count) {
    throw 'Installed URL ACL prefix count changed.'
}
for ($index = 0; $index -lt $expectedPrefixes.Count; $index++) {
    if (-not [StringComparer]::Ordinal.Equals(
            $actualPrefixes[$index],
            $expectedPrefixes[$index])) {
        throw 'Installed URL ACL prefix ordering or identity changed.'
    }
}

$broadUrlAclRejected = $false
try {
    [void](Test-ExactUrlAclEvidence `
        -Lines @('SDDL: D:(A;;GX;;;WD)') `
        -ExpectedServiceSid $serviceSid)
}
catch {
    $broadUrlAclRejected = $true
}
if (-not $broadUrlAclRejected) {
    throw 'A broad URL ACL grant was accepted.'
}

if (-not [StringComparer]::OrdinalIgnoreCase.Equals(
        (Get-InstalledExecutableFromServicePath `
            -PathName '"C:\Program Files\DEEPAi\ServiceDirectory\service.exe"'),
        'C:\Program Files\DEEPAi\ServiceDirectory\service.exe')) {
    throw 'Quoted Windows Service executable parsing changed.'
}

$argumentRejected = $false
try {
    [void](Get-InstalledExecutableFromServicePath `
        -PathName '"C:\Program Files\service.exe" --unexpected')
}
catch {
    $argumentRejected = $true
}
if (-not $argumentRejected) {
    throw 'A Windows Service command line with arguments was accepted.'
}

Write-Host 'Installed validation parser tests passed.'
