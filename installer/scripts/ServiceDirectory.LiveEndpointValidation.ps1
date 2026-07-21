#requires -Version 5.1

[CmdletBinding()]
param(
    [string]$OutputPath,
    [ValidateRange(1000, 30000)]
    [int]$TimeoutMilliseconds = 5000
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$requestedOutputPath = $OutputPath
$installedValidationHelper = Join-Path $PSScriptRoot `
    'ServiceDirectory.InstalledValidation.ps1'
if (-not (Test-Path -LiteralPath $installedValidationHelper -PathType Leaf)) {
    throw "Installed validation helper '$installedValidationHelper' was not found."
}
. $installedValidationHelper
$OutputPath = $requestedOutputPath

$script:LiveExternalNamespace =
    'urn:deepai:service-directory:external'
$script:LiveResults = New-Object 'System.Collections.Generic.List[object]'
$script:LiveEndpointEvidence =
    New-Object 'System.Collections.Generic.List[object]'
$script:LiveConfiguration = $null
$script:LiveHttpsBinding = $null
$script:LiveCaBytes = $null
$script:LiveCrlBytes = $null
$script:LiveCaSpkiSha256 = $null
$script:LiveBouncyCastleCertificate = $null

function Add-LiveValidationResult {
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)]
        [ValidateSet('PASS', 'FAIL')]
        [string]$Status,
        [Parameter(Mandatory = $true)][string]$Detail
    )

    [void]$script:LiveResults.Add([pscustomobject][ordered]@{
            Name = $Name
            Status = $Status
            Detail = $Detail
        })
}

function Invoke-LiveValidationCheck {
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][scriptblock]$Action
    )

    try {
        $detail = & $Action
        if ([string]::IsNullOrWhiteSpace([string]$detail)) {
            $detail = 'Validated.'
        }
        Add-LiveValidationResult -Name $Name -Status PASS `
            -Detail ([string]$detail)
    }
    catch {
        Add-LiveValidationResult -Name $Name -Status FAIL `
            -Detail $_.Exception.Message
    }
}

function Test-LiveByteArrayEqual {
    param(
        [byte[]]$Left,
        [byte[]]$Right
    )

    if ($null -eq $Left -or $null -eq $Right `
        -or $Left.Length -ne $Right.Length) {
        return $false
    }
    $difference = 0
    for ($index = 0; $index -lt $Left.Length; $index++) {
        $difference = $difference -bor ($Left[$index] -bxor $Right[$index])
    }
    return $difference -eq 0
}

function New-LiveDailyApiKey {
    param(
        [Parameter(Mandatory = $true)][string]$ProductCode,
        [Parameter(Mandatory = $true)][DateTime]$LocalNow,
        [byte[]]$InitializationVector
    )

    if ($ProductCode -cnotmatch '^[A-Z0-9]{4}$') {
        throw 'The live health ProductCode must be four canonical ASCII bytes.'
    }
    if ($null -ne $InitializationVector `
        -and $InitializationVector.Length -ne 16) {
        throw 'The daily API key IV must contain exactly 16 bytes.'
    }

    $date = $LocalNow.ToString(
        'yyyyMMdd',
        [Globalization.CultureInfo]::InvariantCulture)
    $strictAscii = [Text.ASCIIEncoding]::new()
    [byte[]]$plainText = $strictAscii.GetBytes($ProductCode + $date)
    [byte[]]$dateBytes = $strictAscii.GetBytes($date)
    [byte[]]$key = $null
    $sha256 = [Security.Cryptography.SHA256]::Create()
    $aes = [Security.Cryptography.Aes]::Create()
    try {
        [byte[]]$key = $sha256.ComputeHash($dateBytes)
        $aes.KeySize = 256
        $aes.BlockSize = 128
        $aes.Mode = [Security.Cryptography.CipherMode]::CBC
        $aes.Padding = [Security.Cryptography.PaddingMode]::PKCS7
        $aes.Key = $key
        if ($null -eq $InitializationVector) {
            $aes.GenerateIV()
        }
        else {
            $aes.IV = [byte[]]$InitializationVector.Clone()
        }
        $encryptor = $aes.CreateEncryptor()
        try {
            [byte[]]$cipherText = $encryptor.TransformFinalBlock(
                $plainText,
                0,
                $plainText.Length)
        }
        finally {
            $encryptor.Dispose()
        }
        [byte[]]$token = New-Object byte[] (16 + $cipherText.Length)
        [Array]::Copy($aes.IV, 0, $token, 0, 16)
        [Array]::Copy($cipherText, 0, $token, 16, $cipherText.Length)
        $encoded = [Convert]::ToBase64String($token)
        if ($encoded.Length -ne 44) {
            throw 'The generated daily API key is not canonical.'
        }
        return $encoded
    }
    finally {
        if ($null -ne $plainText) {
            [Array]::Clear($plainText, 0, $plainText.Length)
        }
        if ($null -ne $dateBytes) {
            [Array]::Clear($dateBytes, 0, $dateBytes.Length)
        }
        if ($null -ne $key) {
            [Array]::Clear($key, 0, $key.Length)
        }
        $aes.Dispose()
        $sha256.Dispose()
    }
}

function Get-LiveXmlDocument {
    param(
        [Parameter(Mandatory = $true)][byte[]]$Bytes,
        [Parameter(Mandatory = $true)][int]$MaximumBytes
    )

    if ($Bytes.Length -eq 0 -or $Bytes.Length -gt $MaximumBytes) {
        throw 'The live XML response size is outside the contract.'
    }
    $strictUtf8 = [Text.UTF8Encoding]::new($false, $true)
    $xml = $strictUtf8.GetString($Bytes)
    $settings = [Xml.XmlReaderSettings]::new()
    $settings.DtdProcessing = [Xml.DtdProcessing]::Prohibit
    $settings.XmlResolver = $null
    $settings.MaxCharactersInDocument = $MaximumBytes
    $document = [Xml.XmlDocument]::new()
    $document.PreserveWhitespace = $true
    $document.XmlResolver = $null
    $textReader = [IO.StringReader]::new($xml)
    $reader = $null
    try {
        $reader = [Xml.XmlReader]::Create($textReader, $settings)
        $document.Load($reader)
    }
    finally {
        if ($null -ne $reader) {
            $reader.Dispose()
        }
        $textReader.Dispose()
    }
    return $document
}

function Get-LiveExactElementChildren {
    param(
        [Parameter(Mandatory = $true)][Xml.XmlElement]$Parent,
        [Parameter(Mandatory = $true)][string[]]$Names
    )

    $elements = New-Object 'System.Collections.Generic.List[object]'
    foreach ($child in $Parent.ChildNodes) {
        if ($child.NodeType -eq [Xml.XmlNodeType]::Element) {
            [void]$elements.Add($child)
        }
        elseif ($child.NodeType -ne [Xml.XmlNodeType]::Whitespace `
            -and $child.NodeType -ne [Xml.XmlNodeType]::SignificantWhitespace) {
            throw "Element '$($Parent.LocalName)' contains a non-element node."
        }
    }
    if ($elements.Count -ne $Names.Count) {
        throw "Element '$($Parent.LocalName)' has an unexpected child count."
    }
    for ($index = 0; $index -lt $Names.Count; $index++) {
        $element = [Xml.XmlElement]$elements[$index]
        if (-not [StringComparer]::Ordinal.Equals(
                $element.LocalName, $Names[$index]) `
            -or -not [StringComparer]::Ordinal.Equals(
                $element.NamespaceURI, $script:LiveExternalNamespace)) {
            throw "Element '$($Parent.LocalName)' has an unexpected child."
        }
    }
    return $elements.ToArray()
}

function Get-LiveSimpleElementValue {
    param(
        [Parameter(Mandatory = $true)][Xml.XmlElement]$Element,
        [switch]$AllowEmpty
    )

    if ($Element.Attributes.Count -ne 0) {
        throw "Element '$($Element.LocalName)' must not contain attributes."
    }
    if ($Element.ChildNodes.Count -eq 0 -and $AllowEmpty) {
        return ''
    }
    if ($Element.ChildNodes.Count -ne 1 `
        -or $Element.FirstChild.NodeType -ne [Xml.XmlNodeType]::Text) {
        throw "Element '$($Element.LocalName)' must contain simple text."
    }
    $value = [string]$Element.InnerText
    if (-not $AllowEmpty -and [string]::IsNullOrEmpty($value)) {
        throw "Element '$($Element.LocalName)' must not be empty."
    }
    return $value
}

function Assert-LiveSuccessEnvelope {
    param([Parameter(Mandatory = $true)][object[]]$Children)

    if (-not [StringComparer]::Ordinal.Equals(
            (Get-LiveSimpleElementValue $Children[0]), 'OK') `
        -or -not [StringComparer]::Ordinal.Equals(
            (Get-LiveSimpleElementValue $Children[1]), '0') `
        -or -not [StringComparer]::Ordinal.Equals(
            (Get-LiveSimpleElementValue $Children[2] -AllowEmpty), '')) {
        throw 'The live External response is not a canonical success envelope.'
    }
}

function ConvertFrom-LiveTrustInfoResponse {
    param([Parameter(Mandatory = $true)][byte[]]$Bytes)

    $document = Get-LiveXmlDocument -Bytes $Bytes -MaximumBytes 32768
    $root = $document.DocumentElement
    if ($null -eq $root `
        -or -not [StringComparer]::Ordinal.Equals($root.LocalName, 'Response') `
        -or -not [StringComparer]::Ordinal.Equals(
            $root.NamespaceURI, $script:LiveExternalNamespace)) {
        throw 'The CA response root or namespace is invalid.'
    }
    foreach ($attribute in $root.Attributes) {
        if ($attribute.NamespaceURI -ne 'http://www.w3.org/2000/xmlns/') {
            throw 'The CA response root contains an unknown attribute.'
        }
    }
    $children = @(Get-LiveExactElementChildren -Parent $root `
            -Names @('Result', 'Code', 'Message', 'TrustInfo'))
    Assert-LiveSuccessEnvelope -Children $children
    $trustChildren = @(Get-LiveExactElementChildren `
            -Parent ([Xml.XmlElement]$children[3]) `
            -Names @('SiteId', 'CaCertificate', 'CaSpkiSha256', 'CrlUri'))
    if ($children[3].Attributes.Count -ne 0) {
        throw 'The CA response TrustInfo contains an unknown attribute.'
    }
    $siteIdText = Get-LiveSimpleElementValue $trustChildren[0]
    $siteId = [Guid]::Empty
    if (-not [Guid]::TryParseExact($siteIdText, 'D', [ref]$siteId) `
        -or $siteId -eq [Guid]::Empty `
        -or $siteId.ToString('D') -cne $siteIdText) {
        throw 'The CA response SiteId is not a canonical non-empty GUID.'
    }
    $caText = Get-LiveSimpleElementValue $trustChildren[1]
    $pinText = Get-LiveSimpleElementValue $trustChildren[2]
    try {
        [byte[]]$caBytes = [Convert]::FromBase64String($caText)
        [byte[]]$pinBytes = [Convert]::FromBase64String($pinText)
    }
    catch {
        throw 'The CA response contains invalid Base64.'
    }
    if ($caBytes.Length -eq 0 -or $caBytes.Length -gt 32768 `
        -or $pinBytes.Length -ne 32 `
        -or [Convert]::ToBase64String($caBytes) -cne $caText `
        -or [Convert]::ToBase64String($pinBytes) -cne $pinText `
        -or (Get-LiveSimpleElementValue $trustChildren[3]) -cne '/pki/crl') {
        throw 'The CA response trust information is non-canonical.'
    }
    return [pscustomobject][ordered]@{
        SiteId = $siteId.ToString('D')
        CaCertificate = $caBytes
        CaSpkiSha256 = $pinBytes
    }
}

function ConvertFrom-LiveHealthResponse {
    param([Parameter(Mandatory = $true)][byte[]]$Bytes)

    $document = Get-LiveXmlDocument -Bytes $Bytes -MaximumBytes 16384
    $root = $document.DocumentElement
    if ($null -eq $root `
        -or -not [StringComparer]::Ordinal.Equals($root.LocalName, 'Response') `
        -or -not [StringComparer]::Ordinal.Equals(
            $root.NamespaceURI, $script:LiveExternalNamespace)) {
        throw 'The health response root or namespace is invalid.'
    }
    foreach ($attribute in $root.Attributes) {
        if ($attribute.NamespaceURI -ne 'http://www.w3.org/2000/xmlns/') {
            throw 'The health response root contains an unknown attribute.'
        }
    }
    $children = @(Get-LiveExactElementChildren -Parent $root `
            -Names @('Result', 'Code', 'Message', 'UtcNow'))
    Assert-LiveSuccessEnvelope -Children $children
    $utcText = Get-LiveSimpleElementValue $children[3]
    $styles = [Globalization.DateTimeStyles]::AssumeUniversal `
        -bor [Globalization.DateTimeStyles]::AdjustToUniversal
    $utc = [DateTime]::MinValue
    if (-not [DateTime]::TryParseExact(
            $utcText,
            "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'",
            [Globalization.CultureInfo]::InvariantCulture,
            $styles,
            [ref]$utc) `
        -or $utc.ToString(
            "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'",
            [Globalization.CultureInfo]::InvariantCulture) -cne $utcText) {
        throw 'The health response UtcNow is not canonical UTC.'
    }
    return $utc
}

function ConvertFrom-LiveHttpResponseBytes {
    param(
        [Parameter(Mandatory = $true)][byte[]]$Bytes,
        [Parameter(Mandatory = $true)][int]$MaximumBodyBytes
    )

    $headerEnd = -1
    for ($index = 0; $index -le $Bytes.Length - 4; $index++) {
        if ($Bytes[$index] -eq 13 -and $Bytes[$index + 1] -eq 10 `
            -and $Bytes[$index + 2] -eq 13 `
            -and $Bytes[$index + 3] -eq 10) {
            $headerEnd = $index
            break
        }
    }
    if ($headerEnd -lt 1 -or $headerEnd -gt 32768) {
        throw 'The live HTTP response header boundary is invalid.'
    }
    for ($index = 0; $index -lt $headerEnd; $index++) {
        $value = $Bytes[$index]
        if ($value -ne 9 -and $value -ne 10 -and $value -ne 13 `
            -and ($value -lt 32 -or $value -gt 126)) {
            throw 'The live HTTP response header is not ASCII.'
        }
    }
    $headerText = [Text.Encoding]::ASCII.GetString($Bytes, 0, $headerEnd)
    $lines = $headerText -split "`r`n"
    if ($lines.Count -lt 2 `
        -or $lines[0] -cnotmatch '^HTTP/1\.1 (?<Status>[0-9]{3})(?: .*)?$') {
        throw 'The live endpoint did not return an HTTP/1.1 response.'
    }
    $statusCode = [int]$Matches['Status']
    $headers = [Collections.Generic.Dictionary[string, string]]::new(
        [StringComparer]::OrdinalIgnoreCase)
    for ($index = 1; $index -lt $lines.Count; $index++) {
        if ($lines[$index] -cnotmatch `
            '^(?<Name>[!#$%&''*+\-.^_`|~0-9A-Za-z]+):[ \t]*(?<Value>.*?)[ \t]*$') {
            throw 'The live HTTP response contains an invalid header line.'
        }
        if ($headers.ContainsKey($Matches['Name'])) {
            throw 'The live HTTP response contains a duplicate header.'
        }
        $headers.Add($Matches['Name'], $Matches['Value'])
    }
    if ($headers.ContainsKey('Transfer-Encoding') `
        -or -not $headers.ContainsKey('Content-Length') `
        -or $headers['Content-Length'] -cnotmatch '^(?:0|[1-9][0-9]*)$') {
        throw 'The live HTTP response framing is not canonical Content-Length.'
    }
    $contentLength = [long]$headers['Content-Length']
    $bodyOffset = $headerEnd + 4
    if ($contentLength -gt $MaximumBodyBytes `
        -or $contentLength -ne $Bytes.Length - $bodyOffset) {
        throw 'The live HTTP response body length is invalid.'
    }
    [byte[]]$body = New-Object byte[] ([int]$contentLength)
    if ($body.Length -ne 0) {
        [Array]::Copy($Bytes, $bodyOffset, $body, 0, $body.Length)
    }
    return [pscustomobject][ordered]@{
        StatusCode = $statusCode
        ContentType = if ($headers.ContainsKey('Content-Type')) {
            $headers['Content-Type']
        }
        else { $null }
        Headers = $headers
        Body = $body
    }
}

function Invoke-LiveHttpsGet {
    param(
        [Parameter(Mandatory = $true)][string]$ConnectAddress,
        [Parameter(Mandatory = $true)][string]$ServerName,
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$ExpectedThumbprint,
        [Parameter(Mandatory = $true)][int]$MaximumBodyBytes,
        [Parameter(Mandatory = $true)][int]$Timeout,
        [string]$ApiKey
    )

    $script:LiveObservedCertificate = $null
    $script:LiveObservedPolicyErrors =
        [Net.Security.SslPolicyErrors]::RemoteCertificateNotAvailable
    $callback = [Net.Security.RemoteCertificateValidationCallback]{
        param($sender, $certificate, $chain, $policyErrors)

        $script:LiveObservedPolicyErrors = $policyErrors
        if ($null -eq $certificate) {
            return $false
        }
        $candidate = [Security.Cryptography.X509Certificates.X509Certificate2]::new(
            $certificate)
        $script:LiveObservedCertificate = $candidate
        $forbidden = [Net.Security.SslPolicyErrors]::RemoteCertificateNameMismatch `
            -bor [Net.Security.SslPolicyErrors]::RemoteCertificateNotAvailable
        return [StringComparer]::Ordinal.Equals(
                $candidate.Thumbprint.ToUpperInvariant(),
                $ExpectedThumbprint) `
            -and ($policyErrors -band $forbidden) -eq 0
    }

    $client = [Net.Sockets.TcpClient]::new()
    $sslStream = $null
    $memory = [IO.MemoryStream]::new()
    try {
        $connectTask = $client.ConnectAsync($ConnectAddress, 21000)
        if (-not $connectTask.Wait($Timeout)) {
            throw "TLS connection to '$ConnectAddress`:21000' timed out."
        }
        $connectTask.GetAwaiter().GetResult()
        $client.ReceiveTimeout = $Timeout
        $client.SendTimeout = $Timeout
        $networkStream = $client.GetStream()
        $networkStream.ReadTimeout = $Timeout
        $networkStream.WriteTimeout = $Timeout
        $sslStream = [Net.Security.SslStream]::new(
            $networkStream,
            $false,
            $callback)
        $sslStream.ReadTimeout = $Timeout
        $sslStream.WriteTimeout = $Timeout
        $sslStream.AuthenticateAsClient(
            $ServerName,
            $null,
            [Security.Authentication.SslProtocols]::None,
            $false)
        $protocolValue = [int]$sslStream.SslProtocol
        if ($protocolValue -ne 3072 -and $protocolValue -ne 12288) {
            throw "The negotiated TLS protocol '$($sslStream.SslProtocol)' is below TLS 1.2."
        }
        if ($null -eq $script:LiveObservedCertificate) {
            throw 'The TLS peer certificate was not captured.'
        }

        $request = "GET $Path HTTP/1.1`r`nHost: $ServerName`:21000`r`n"
        if (-not [string]::IsNullOrEmpty($ApiKey)) {
            $request += "X-DPAI-API-Key: $ApiKey`r`n"
        }
        $request += "Connection: close`r`n`r`n"
        [byte[]]$requestBytes = [Text.Encoding]::ASCII.GetBytes($request)
        $sslStream.Write($requestBytes, 0, $requestBytes.Length)
        $sslStream.Flush()

        [byte[]]$buffer = New-Object byte[] 8192
        $maximumResponseBytes = 32772 + $MaximumBodyBytes
        while (($read = $sslStream.Read($buffer, 0, $buffer.Length)) -gt 0) {
            if ($memory.Length + $read -gt $maximumResponseBytes) {
                throw 'The live HTTP response exceeds its bounded size.'
            }
            $memory.Write($buffer, 0, $read)
        }
        [byte[]]$responseBytes = $memory.ToArray()
        $parsed = ConvertFrom-LiveHttpResponseBytes `
            -Bytes $responseBytes -MaximumBodyBytes $MaximumBodyBytes
        if ($parsed.StatusCode -ne 200) {
            throw "The live endpoint returned HTTP $($parsed.StatusCode)."
        }
        return [pscustomobject][ordered]@{
            TlsProtocol = [string]$sslStream.SslProtocol
            PolicyErrors = [string]$script:LiveObservedPolicyErrors
            LeafSubject = $script:LiveObservedCertificate.Subject
            LeafRawData = $script:LiveObservedCertificate.RawData
            ContentType = $parsed.ContentType
            Body = $parsed.Body
        }
    }
    finally {
        $memory.Dispose()
        if ($null -ne $sslStream) {
            $sslStream.Dispose()
        }
        $client.Dispose()
        if ($null -ne $script:LiveObservedCertificate) {
            $script:LiveObservedCertificate.Dispose()
        }
    }
}

function Get-LiveCaSpkiSha256 {
    param([Parameter(Mandatory = $true)][byte[]]$CaBytes)

    $parser = [Org.BouncyCastle.X509.X509CertificateParser]::new()
    $certificate = $parser.ReadCertificate($CaBytes)
    $certificate.Verify($certificate.GetPublicKey())
    [byte[]]$spki = [Org.BouncyCastle.X509.SubjectPublicKeyInfoFactory]::
        CreateSubjectPublicKeyInfo($certificate.GetPublicKey()).GetDerEncoded()
    $sha256 = [Security.Cryptography.SHA256]::Create()
    try {
        [byte[]]$hash = $sha256.ComputeHash($spki)
        return ,$hash
    }
    finally {
        [Array]::Clear($spki, 0, $spki.Length)
        $sha256.Dispose()
    }
}

function Test-LiveTarget {
    param(
        [Parameter(Mandatory = $true)][string]$Kind,
        [Parameter(Mandatory = $true)][string]$ServerName,
        [Parameter(Mandatory = $true)][int]$Timeout
    )

    if ($null -eq $script:LiveConfiguration `
        -or $null -eq $script:LiveHttpsBinding `
        -or $null -eq $script:LiveCaBytes `
        -or $null -eq $script:LiveCrlBytes `
        -or $null -eq $script:LiveCaSpkiSha256 `
        -or $null -eq $script:LiveBouncyCastleCertificate) {
        throw 'Live endpoint prerequisites are unavailable.'
    }
    $connectAddress = [string]$script:LiveConfiguration.ListenAddress
    if ($Kind -eq 'HOSTNAME') {
        $resolvedIpv4 = @([Net.Dns]::GetHostAddresses($ServerName) |
            Where-Object {
                $_.AddressFamily -eq [Net.Sockets.AddressFamily]::InterNetwork
            } | ForEach-Object { $_.ToString() } | Sort-Object -Unique)
        if ($resolvedIpv4 -notcontains $connectAddress) {
            throw 'DirectoryHostName DNS does not resolve to the configured Directory IPv4.'
        }
    }

    $caResponse = Invoke-LiveHttpsGet `
        -ConnectAddress $connectAddress `
        -ServerName $ServerName `
        -Path '/pki/ca' `
        -ExpectedThumbprint $script:LiveHttpsBinding.Thumbprint `
        -MaximumBodyBytes 32768 `
        -Timeout $Timeout
    if (-not [StringComparer]::OrdinalIgnoreCase.Equals(
            [string]$caResponse.ContentType,
            'application/xml; charset=utf-8')) {
        throw 'The CA endpoint Content-Type is invalid.'
    }
    $trust = ConvertFrom-LiveTrustInfoResponse -Bytes $caResponse.Body
    if (-not (Test-LiveByteArrayEqual `
            $trust.CaCertificate $script:LiveCaBytes) `
        -or -not (Test-LiveByteArrayEqual `
            $trust.CaSpkiSha256 $script:LiveCaSpkiSha256)) {
        throw 'The CA endpoint disagrees with the installed CA certificate or SPKI pin.'
    }
    $leafParser = [Org.BouncyCastle.X509.X509CertificateParser]::new()
    $leaf = $leafParser.ReadCertificate([byte[]]$caResponse.LeafRawData)
    $leaf.Verify($script:LiveBouncyCastleCertificate.GetPublicKey())

    $crlResponse = Invoke-LiveHttpsGet `
        -ConnectAddress $connectAddress `
        -ServerName $ServerName `
        -Path '/pki/crl' `
        -ExpectedThumbprint $script:LiveHttpsBinding.Thumbprint `
        -MaximumBodyBytes 4194304 `
        -Timeout $Timeout
    if (-not [StringComparer]::OrdinalIgnoreCase.Equals(
            [string]$crlResponse.ContentType,
            'application/pkix-crl') `
        -or -not (Test-LiveByteArrayEqual `
            $crlResponse.Body $script:LiveCrlBytes)) {
        throw 'The CRL endpoint Content-Type or bytes disagree with installed state.'
    }
    $crlParser = [Org.BouncyCastle.X509.X509CrlParser]::new()
    $crl = $crlParser.ReadCrl([byte[]]$crlResponse.Body)
    $crl.Verify($script:LiveBouncyCastleCertificate.GetPublicKey())

    $apiKey = New-LiveDailyApiKey -ProductCode 'WDOG' `
        -LocalNow ([DateTime]::Now)
    $healthResponse = Invoke-LiveHttpsGet `
        -ConnectAddress $connectAddress `
        -ServerName $ServerName `
        -Path '/api/health' `
        -ExpectedThumbprint $script:LiveHttpsBinding.Thumbprint `
        -MaximumBodyBytes 16384 `
        -Timeout $Timeout `
        -ApiKey $apiKey
    if (-not [StringComparer]::OrdinalIgnoreCase.Equals(
            [string]$healthResponse.ContentType,
            'application/xml; charset=utf-8')) {
        throw 'The health endpoint Content-Type is invalid.'
    }
    $healthUtc = ConvertFrom-LiveHealthResponse -Bytes $healthResponse.Body
    if ([Math]::Abs(($healthUtc - [DateTime]::UtcNow).TotalSeconds) -gt 60) {
        throw 'The health response time differs from the local server by more than 60 seconds.'
    }

    [void]$script:LiveEndpointEvidence.Add([pscustomobject][ordered]@{
            Kind = $Kind
            ServerName = $ServerName
            ConnectAddress = $connectAddress
            TlsProtocol = $caResponse.TlsProtocol
            CertificateSubject = $caResponse.LeafSubject
            CertificateThumbprint = $script:LiveHttpsBinding.Thumbprint
            SiteId = $trust.SiteId
            CaSha256 = (Get-FileHash `
                -LiteralPath (Join-Path `
                    ([Environment]::GetFolderPath('CommonApplicationData')) `
                    'DEEPAi\ServiceDirectory\pki\ca-a.der') `
                -Algorithm SHA256).Hash
            CrlSha256 = (Get-FileHash `
                -LiteralPath (Join-Path `
                    ([Environment]::GetFolderPath('CommonApplicationData')) `
                    'DEEPAi\ServiceDirectory\pki\crl-a.der') `
                -Algorithm SHA256).Hash
            HealthUtc = $healthUtc.ToString(
                "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'",
                [Globalization.CultureInfo]::InvariantCulture)
        })
    return "TLS=$($caResponse.TlsProtocol); SiteId=$($trust.SiteId); " `
        + "HealthUtc=$($healthUtc.ToString('o'))"
}

function Invoke-ServiceDirectoryLiveEndpointValidation {
    param(
        [Parameter(Mandatory = $true)][string]$ReportPath,
        [Parameter(Mandatory = $true)][int]$Timeout
    )

    $fullReportPath = Resolve-InstalledValidationReportPath -Path $ReportPath
    $installRoot = Join-Path ${env:ProgramFiles} 'DEEPAi\ServiceDirectory'
    $dataRoot = Join-Path `
        ([Environment]::GetFolderPath('CommonApplicationData')) `
        'DEEPAi\ServiceDirectory'
    $script:LiveResults.Clear()
    $script:LiveEndpointEvidence.Clear()
    $script:LiveConfiguration = $null
    $script:LiveHttpsBinding = $null
    $script:LiveCaBytes = $null
    $script:LiveCrlBytes = $null
    $script:LiveCaSpkiSha256 = $null
    $script:LiveBouncyCastleCertificate = $null

    Invoke-LiveValidationCheck -Name 'Process.Administrator' -Action {
        $principal = [Security.Principal.WindowsPrincipal]::new(
            [Security.Principal.WindowsIdentity]::GetCurrent())
        if (-not $principal.IsInRole(
                [Security.Principal.WindowsBuiltInRole]::Administrator)) {
            throw 'Run the live validation tool from an elevated PowerShell session.'
        }
        'The live validation process is elevated.'
    }
    Invoke-LiveValidationCheck -Name 'Service.Main.Running' -Action {
        $service = Get-Service -Name $script:MainServiceName -ErrorAction Stop
        if ($service.Status -ne [ServiceProcess.ServiceControllerStatus]::Running) {
            throw 'The main Service Directory service is not running.'
        }
        'The main service is running.'
    }
    Invoke-LiveValidationCheck -Name 'Configuration.DirectoryIdentity' -Action {
        [byte[]]$configBytes = [IO.File]::ReadAllBytes(
            (Join-Path $dataRoot 'config.xml'))
        $script:LiveConfiguration = ConvertFrom-InstalledConfigurationXml `
            -Xml ([Text.UTF8Encoding]::new(
                $false, $true).GetString($configBytes))
        "DirectoryHostName=$($script:LiveConfiguration.DirectoryHostName); " `
            + "DirectoryIpv4Address=$($script:LiveConfiguration.DirectoryIpv4Address)"
    }
    Invoke-LiveValidationCheck -Name 'HttpSys.HttpsBinding' -Action {
        if ($null -eq $script:LiveConfiguration) {
            throw 'The Directory identity is unavailable.'
        }
        $output = @(& "$env:SystemRoot\System32\netsh.exe" `
            http show sslcert `
            "ipport=$($script:LiveConfiguration.ListenAddress):21000" 2>&1)
        if ($LASTEXITCODE -ne 0) {
            throw 'The live Directory HTTPS binding could not be queried.'
        }
        $script:LiveHttpsBinding = ConvertFrom-HttpsBindingEvidence `
            -Lines @($output | ForEach-Object { [string]$_ }) `
            -Address $script:LiveConfiguration.ListenAddress
        "Thumbprint=$($script:LiveHttpsBinding.Thumbprint)"
    }
    Invoke-LiveValidationCheck -Name 'Pki.LocalArtifacts' -Action {
        $bouncyCastlePath = Join-Path $installRoot `
            'BouncyCastle.Cryptography.dll'
        if (-not (Test-Path -LiteralPath $bouncyCastlePath -PathType Leaf)) {
            throw 'The installed Bouncy Castle runtime is missing.'
        }
        Add-Type -LiteralPath $bouncyCastlePath
        $caPath = Join-Path $dataRoot 'pki\ca-a.der'
        $crlPath = Join-Path $dataRoot 'pki\crl-a.der'
        [void](Get-SafeInstalledFileEvidence `
            -Path $caPath -EvidenceName 'pki\ca-a.der')
        [void](Get-SafeInstalledFileEvidence `
            -Path $crlPath -EvidenceName 'pki\crl-a.der')
        $script:LiveCaBytes = [IO.File]::ReadAllBytes($caPath)
        $script:LiveCrlBytes = [IO.File]::ReadAllBytes($crlPath)
        $script:LiveCaSpkiSha256 = Get-LiveCaSpkiSha256 `
            -CaBytes $script:LiveCaBytes
        $parser = [Org.BouncyCastle.X509.X509CertificateParser]::new()
        $script:LiveBouncyCastleCertificate =
            $parser.ReadCertificate($script:LiveCaBytes)
        $crlParser = [Org.BouncyCastle.X509.X509CrlParser]::new()
        $localCrl = $crlParser.ReadCrl($script:LiveCrlBytes)
        $localCrl.Verify(
            $script:LiveBouncyCastleCertificate.GetPublicKey())
        "CABytes=$($script:LiveCaBytes.Length); CRLBytes=$($script:LiveCrlBytes.Length)"
    }

    Invoke-LiveValidationCheck -Name 'Endpoint.IPv4' -Action {
        if ($null -eq $script:LiveConfiguration) {
            throw 'The Directory identity is unavailable.'
        }
        Test-LiveTarget -Kind 'IPV4' `
            -ServerName $script:LiveConfiguration.DirectoryIpv4Address `
            -Timeout $Timeout
    }
    Invoke-LiveValidationCheck -Name 'Endpoint.HostName' -Action {
        if ($null -eq $script:LiveConfiguration) {
            throw 'The Directory identity is unavailable.'
        }
        Test-LiveTarget -Kind 'HOSTNAME' `
            -ServerName $script:LiveConfiguration.DirectoryHostName `
            -Timeout $Timeout
    }

    $failedCount = @($script:LiveResults | Where-Object {
            $_.Status -eq 'FAIL'
        }).Count
    $report = [pscustomobject][ordered]@{
        SchemaVersion = 1
        Product = 'DEEPAi Service Directory'
        ValidationKind = 'LIVE_EXTERNAL_ENDPOINT'
        CapturedAt = [DateTimeOffset]::Now.ToString('o')
        MachineName = [Environment]::MachineName
        OverallStatus = if ($failedCount -eq 0) { 'PASS' } else { 'FAIL' }
        FailedCount = $failedCount
        Checks = $script:LiveResults.ToArray()
        EndpointEvidence = $script:LiveEndpointEvidence.ToArray()
    }
    $json = $report | ConvertTo-Json -Depth 6
    $temporaryPath = $fullReportPath + '.preparing'
    if (Test-Path -LiteralPath $temporaryPath) {
        throw 'The live validation report staging path already exists.'
    }
    try {
        [IO.File]::WriteAllText(
            $temporaryPath,
            $json + [Environment]::NewLine,
            [Text.UTF8Encoding]::new($false))
        Move-Item -LiteralPath $temporaryPath -Destination $fullReportPath
    }
    finally {
        if (Test-Path -LiteralPath $temporaryPath) {
            Remove-Item -LiteralPath $temporaryPath -Force
        }
        if ($null -ne $script:LiveCaBytes) {
            [Array]::Clear(
                $script:LiveCaBytes, 0, $script:LiveCaBytes.Length)
        }
        if ($null -ne $script:LiveCrlBytes) {
            [Array]::Clear(
                $script:LiveCrlBytes, 0, $script:LiveCrlBytes.Length)
        }
        if ($null -ne $script:LiveCaSpkiSha256) {
            [Array]::Clear(
                $script:LiveCaSpkiSha256,
                0,
                $script:LiveCaSpkiSha256.Length)
        }
    }
    Write-Host "Live endpoint validation report: $fullReportPath"
    if ($failedCount -ne 0) {
        return 2
    }
    return 0
}

if ($MyInvocation.InvocationName -ne '.') {
    if ([string]::IsNullOrWhiteSpace($OutputPath)) {
        throw 'OutputPath is required.'
    }
    exit (Invoke-ServiceDirectoryLiveEndpointValidation `
        -ReportPath $OutputPath `
        -Timeout $TimeoutMilliseconds)
}
