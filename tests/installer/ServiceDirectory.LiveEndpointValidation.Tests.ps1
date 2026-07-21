#requires -Version 5.1

[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repositoryRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$helperPath = Join-Path $repositoryRoot `
    'installer\scripts\ServiceDirectory.LiveEndpointValidation.ps1'
if (-not (Test-Path -LiteralPath $helperPath -PathType Leaf)) {
    throw "Live endpoint validation helper '$helperPath' was not found."
}
. $helperPath

[byte[]]$fixedIv = 0..15
$knownKey = New-LiveDailyApiKey `
    -ProductCode 'ABCD' `
    -LocalNow ([DateTime]::new(2026, 7, 17, 12, 0, 0)) `
    -InitializationVector $fixedIv
if (-not [StringComparer]::Ordinal.Equals(
        $knownKey,
        'AAECAwQFBgcICQoLDA0OD37MVf5dYeif4Ss6OjGAC+g=')) {
    throw 'The live validator daily API key vector changed.'
}

[byte[]]$caBytes = 1..32
[byte[]]$pinBytes = 33..64
$trustXml = [Text.UTF8Encoding]::new($false).GetBytes(
    '<?xml version="1.0" encoding="utf-8"?>' `
        + '<Response xmlns="urn:deepai:service-directory:external">' `
        + '<Result>OK</Result><Code>0</Code><Message />' `
        + '<TrustInfo><SiteId>3d8ff138-4e9a-4e52-b108-e3af248b1787</SiteId>' `
        + '<CaCertificate>' + [Convert]::ToBase64String($caBytes) `
        + '</CaCertificate><CaSpkiSha256>' `
        + [Convert]::ToBase64String($pinBytes) `
        + '</CaSpkiSha256><CrlUri>/pki/crl</CrlUri></TrustInfo></Response>')
$trust = ConvertFrom-LiveTrustInfoResponse -Bytes $trustXml
if ($trust.SiteId -cne '3d8ff138-4e9a-4e52-b108-e3af248b1787' `
    -or -not (Test-LiveByteArrayEqual $trust.CaCertificate $caBytes) `
    -or -not (Test-LiveByteArrayEqual $trust.CaSpkiSha256 $pinBytes)) {
    throw 'The live validator trust response parser changed.'
}

$healthXml = [Text.UTF8Encoding]::new($false).GetBytes(
    '<Response xmlns="urn:deepai:service-directory:external">' `
        + '<Result>OK</Result><Code>0</Code><Message />' `
        + '<UtcNow>2026-07-21T03:04:05.1234567Z</UtcNow></Response>')
$healthUtc = ConvertFrom-LiveHealthResponse -Bytes $healthXml
if ($healthUtc.Kind -ne [DateTimeKind]::Utc `
    -or $healthUtc.Ticks -ne `
        ([DateTime]::new(
            2026, 7, 21, 3, 4, 5, 123,
            [DateTimeKind]::Utc).AddTicks(4567)).Ticks) {
    throw 'The live validator health response parser changed.'
}

$responseBody = [Text.UTF8Encoding]::new($false).GetBytes('<Response />')
$responseHeader = [Text.Encoding]::ASCII.GetBytes(
    "HTTP/1.1 200 OK`r`nContent-Length: $($responseBody.Length)`r`n" `
        + "Content-Type: application/xml; charset=utf-8`r`n`r`n")
[byte[]]$responseBytes = New-Object byte[] (
    $responseHeader.Length + $responseBody.Length)
[Array]::Copy($responseHeader, 0, $responseBytes, 0, $responseHeader.Length)
[Array]::Copy(
    $responseBody,
    0,
    $responseBytes,
    $responseHeader.Length,
    $responseBody.Length)
$response = ConvertFrom-LiveHttpResponseBytes `
    -Bytes $responseBytes -MaximumBodyBytes 16384
if ($response.StatusCode -ne 200 `
    -or $response.ContentType -cne 'application/xml; charset=utf-8' `
    -or -not (Test-LiveByteArrayEqual $response.Body $responseBody)) {
    throw 'The live validator HTTP/1.1 response parser changed.'
}

foreach ($invalidTrustXml in @(
        '<Response xmlns="urn:wrong"><Result>OK</Result><Code>0</Code><Message/><TrustInfo/></Response>',
        '<Response xmlns="urn:deepai:service-directory:external"><Result>OK</Result><Code>0</Code><Message/><TrustInfo><SiteId>3D8FF138-4E9A-4E52-B108-E3AF248B1787</SiteId><CaCertificate>AQ==</CaCertificate><CaSpkiSha256>AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=</CaSpkiSha256><CrlUri>/pki/crl</CrlUri></TrustInfo></Response>',
        '<!DOCTYPE Response [<!ENTITY x "OK">]><Response xmlns="urn:deepai:service-directory:external"><Result>&x;</Result><Code>0</Code><Message/><TrustInfo/></Response>')) {
    $rejected = $false
    try {
        [void](ConvertFrom-LiveTrustInfoResponse `
            -Bytes ([Text.UTF8Encoding]::new($false).GetBytes(
                $invalidTrustXml)))
    }
    catch {
        $rejected = $true
    }
    if (-not $rejected) {
        throw 'An invalid live trust response was accepted.'
    }
}

$duplicateHeader = [Text.Encoding]::ASCII.GetBytes(
    "HTTP/1.1 200 OK`r`nContent-Length: 0`r`ncontent-length: 0`r`n`r`n")
$duplicateRejected = $false
try {
    [void](ConvertFrom-LiveHttpResponseBytes `
        -Bytes $duplicateHeader -MaximumBodyBytes 16384)
}
catch {
    $duplicateRejected = $true
}
if (-not $duplicateRejected) {
    throw 'A duplicate live HTTP response header was accepted.'
}

Write-Host 'Live endpoint validation parser tests passed.'
