$httpsBindingAppId = "{$installerOwnerId}"

function Get-RemoteHttpsPrefix {
    param([Parameter(Mandatory = $true)][string]$Address)

    $canonical = ConvertTo-CanonicalAddress -Value $Address
    return "https://$canonical`:$servicePort/"
}

function ConvertTo-CanonicalDirectoryHostName {
    param([Parameter(Mandatory = $true)][string]$Value)

    if (-not [StringComparer]::Ordinal.Equals($Value, $Value.Trim())) {
        throw 'The Directory hostname must not contain surrounding whitespace.'
    }

    $canonical = $Value.ToLowerInvariant()
    if ($canonical.Length -lt 1 `
        -or $canonical.Length -gt 253 `
        -or $canonical.StartsWith('.') `
        -or $canonical.EndsWith('.') `
        -or $canonical.Contains('*') `
        -or $canonical -notmatch '[a-z]') {
        throw "The Directory hostname '$Value' is not canonical DNS syntax."
    }

    foreach ($label in $canonical.Split('.')) {
        if ($label.Length -lt 1 `
            -or $label.Length -gt 63 `
            -or $label.StartsWith('-') `
            -or $label.EndsWith('-') `
            -or $label -cnotmatch '^[a-z0-9-]+$') {
            throw "The Directory hostname '$Value' is not canonical DNS syntax."
        }
    }

    return $canonical
}

function Get-RemoteHttpsHostNamePrefix {
    param([Parameter(Mandatory = $true)][string]$HostName)

    $canonical = ConvertTo-CanonicalDirectoryHostName -Value $HostName
    return "https://$canonical`:$servicePort/"
}

function Get-HttpsBindingEndpoint {
    param([Parameter(Mandatory = $true)][string]$Address)

    $canonical = ConvertTo-CanonicalAddress -Value $Address
    return "$canonical`:$servicePort"
}

function Get-HttpsBindingOutputValues {
    param([Parameter(Mandatory = $true)][string[]]$Lines)

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
    return $values.ToArray()
}

function Get-HttpsBindingSnapshot {
    param([Parameter(Mandatory = $true)][string]$Address)

    $endpoint = Get-HttpsBindingEndpoint -Address $Address
    $netshPath = "$env:SystemRoot\System32\netsh.exe"
    $output = @(& $netshPath http show sslcert "ipport=$endpoint" 2>&1)
    $exitCode = $LASTEXITCODE
    if ($exitCode -ne 0) {
        $allOutput = @(& $netshPath http show sslcert 2>&1)
        $allExitCode = $LASTEXITCODE
        if ($allExitCode -ne 0) {
            throw "HTTPS binding query failed with exit codes $exitCode and $allExitCode."
        }

        $listed = @($allOutput | Where-Object {
                $line = ([string]$_).Trim()
                $line.EndsWith(
                    $endpoint,
                    [System.StringComparison]::OrdinalIgnoreCase)
            }).Count -ne 0
        if ($listed) {
            throw "HTTPS binding '$endpoint' was listed but its exact state could not be read."
        }

        return [pscustomobject]@{
            Address = $Address
            Endpoint = $endpoint
            Exists = $false
            Owned = $false
            Valid = $false
            Thumbprint = $null
        }
    }

    $values = @(Get-HttpsBindingOutputValues `
        -Lines @($output | ForEach-Object { [string]$_ }))

    $endpointMatches = @($values | Where-Object {
            [StringComparer]::OrdinalIgnoreCase.Equals($_, $endpoint)
        })
    if ($endpointMatches.Count -eq 0) {
        return [pscustomobject]@{
            Address = $Address
            Endpoint = $endpoint
            Exists = $false
            Owned = $false
            Valid = $false
            Thumbprint = $null
        }
    }
    if ($endpointMatches.Count -ne 1) {
        throw "HTTPS binding '$endpoint' was returned more than once."
    }

    $thumbprints = @($values | ForEach-Object {
            $candidate = $_ -replace '\s', ''
            if ($candidate -cmatch '^[0-9a-fA-F]{40}$') {
                $candidate.ToUpperInvariant()
            }
        } | Where-Object { $null -ne $_ } | Sort-Object -Unique)
    $applicationIds = @($values | Where-Object {
            $_ -match '^\{[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\}$'
        } | ForEach-Object { $_.ToUpperInvariant() } | Sort-Object -Unique)
    if ($applicationIds.Count -ne 1) {
        throw "HTTPS binding '$endpoint' does not expose one canonical application ID."
    }

    $owned = [StringComparer]::OrdinalIgnoreCase.Equals(
        $applicationIds[0],
        $httpsBindingAppId)
    $certificateValid = $false
    if ($thumbprints.Count -eq 1) {
        $certificatePath = "Cert:\LocalMachine\My\$($thumbprints[0])"
        $certificate = Get-Item -LiteralPath $certificatePath `
            -ErrorAction SilentlyContinue
        $certificateValid = $null -ne $certificate `
            -and [bool]$certificate.HasPrivateKey
    }

    return [pscustomobject]@{
        Address = $Address
        Endpoint = $endpoint
        Exists = $true
        Owned = $owned
        Valid = $owned -and $thumbprints.Count -eq 1 -and $certificateValid
        Thumbprint = if ($thumbprints.Count -eq 1) { $thumbprints[0] } else { $null }
    }
}

function Assert-HttpsBindingCanBeManaged {
    param([Parameter(Mandatory = $true)][object]$Snapshot)

    if ($Snapshot.Exists -and -not $Snapshot.Owned) {
        throw "HTTPS binding '$($Snapshot.Endpoint)' is owned by another application."
    }
    if ($Snapshot.Exists -and -not $Snapshot.Valid) {
        throw "Owned HTTPS binding '$($Snapshot.Endpoint)' is not in a valid managed state."
    }
}

function Remove-OwnedHttpsBinding {
    param([Parameter(Mandatory = $true)][string]$Address)

    $snapshot = Get-HttpsBindingSnapshot -Address $Address
    Assert-HttpsBindingCanBeManaged -Snapshot $snapshot
    if (-not $snapshot.Exists) {
        return
    }

    [void](Invoke-NativeCommand `
        -FilePath "$env:SystemRoot\System32\netsh.exe" `
        -Arguments @(
            'http', 'delete', 'sslcert',
            "ipport=$($snapshot.Endpoint)"))
    if ((Get-HttpsBindingSnapshot -Address $Address).Exists) {
        throw "Owned HTTPS binding '$($snapshot.Endpoint)' was not removed."
    }
}

function Set-OwnedHttpsBinding {
    param(
        [Parameter(Mandatory = $true)][string]$Address,
        [Parameter(Mandatory = $true)][string]$Thumbprint
    )

    if ($Thumbprint -cnotmatch '^[0-9A-F]{40}$') {
        throw 'Directory certificate thumbprint is not canonical SHA-1 hexadecimal.'
    }

    $snapshot = Get-HttpsBindingSnapshot -Address $Address
    Assert-HttpsBindingCanBeManaged -Snapshot $snapshot
    if ($snapshot.Exists) {
        Remove-OwnedHttpsBinding -Address $Address
    }

    $endpoint = Get-HttpsBindingEndpoint -Address $Address
    [void](Invoke-NativeCommand `
        -FilePath "$env:SystemRoot\System32\netsh.exe" `
        -Arguments @(
            'http', 'add', 'sslcert',
            "ipport=$endpoint",
            "certhash=$Thumbprint",
            "appid=$httpsBindingAppId",
            'certstorename=MY'))
    $verified = Get-HttpsBindingSnapshot -Address $Address
    if (-not $verified.Exists `
        -or -not $verified.Owned `
        -or -not $verified.Valid `
        -or -not [StringComparer]::Ordinal.Equals(
            [string]$verified.Thumbprint,
            $Thumbprint)) {
        throw "Owned HTTPS binding '$endpoint' could not be verified."
    }
}

function Restore-HttpsBindingSnapshot {
    param([Parameter(Mandatory = $true)][object]$Snapshot)

    $current = Get-HttpsBindingSnapshot -Address ([string]$Snapshot.Address)
    Assert-HttpsBindingCanBeManaged -Snapshot $current
    if ($current.Exists) {
        Remove-OwnedHttpsBinding -Address ([string]$Snapshot.Address)
    }
    if ($Snapshot.Exists) {
        if (-not $Snapshot.Owned -or -not $Snapshot.Valid) {
            throw 'A foreign or invalid HTTPS binding was unexpectedly selected for rollback.'
        }
        Set-OwnedHttpsBinding `
            -Address ([string]$Snapshot.Address) `
            -Thumbprint ([string]$Snapshot.Thumbprint)
    }
}

function Invoke-DirectoryCertificateInstall {
    param([Parameter(Mandatory = $true)][string]$ExecutablePath)

    $output = @(Invoke-NativeCommand `
        -FilePath $ExecutablePath `
        -Arguments @('--repair-directory-certificate-install'))
    $lines = @($output | ForEach-Object { ([string]$_).Trim() } |
        Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
    if ($lines.Count -ne 1 `
        -or $lines[0] -cnotmatch '^DIRECTORY_CERTIFICATE_INSTALLED (?<Thumbprint>[0-9A-F]{40}) (?<Serial>[0-9A-F]{32}) (?<Expiry>[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9:.]+Z)$') {
        throw 'Directory certificate installation returned an unexpected result.'
    }

    return [pscustomobject]@{
        Thumbprint = $Matches['Thumbprint']
        Serial = $Matches['Serial']
        ExpiryUtc = $Matches['Expiry']
    }
}

function Invoke-DirectoryCertificateRemove {
    param(
        [Parameter(Mandatory = $true)][string]$ExecutablePath,
        [Parameter(Mandatory = $true)][string]$Thumbprint
    )

    if ($Thumbprint -cnotmatch '^[0-9A-F]{40}$') {
        throw 'Directory certificate thumbprint is not canonical SHA-1 hexadecimal.'
    }
    $output = @(Invoke-NativeCommand `
        -FilePath $ExecutablePath `
        -Arguments @(
            '--repair-directory-certificate-remove',
            $Thumbprint))
    $lines = @($output | ForEach-Object { ([string]$_).Trim() } |
        Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
    if ($lines.Count -ne 1 `
        -or -not [StringComparer]::Ordinal.Equals(
            $lines[0],
            "DIRECTORY_CERTIFICATE_REMOVED $Thumbprint")) {
        throw 'Directory certificate removal returned an unexpected result.'
    }
}
