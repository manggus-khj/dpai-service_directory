function ConvertTo-CanonicalAddress {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Value
    )

    if ([string]::IsNullOrWhiteSpace($Value) `
        -or -not [StringComparer]::Ordinal.Equals($Value, $Value.Trim()) `
        -or $Value.Contains('%') `
        -or $Value.Contains('[') `
        -or $Value.Contains(']')) {
        throw 'ListenAddress must be an unadorned canonical IP literal.'
    }

    $parsed = $null
    if (-not [System.Net.IPAddress]::TryParse($Value, [ref]$parsed)) {
        throw 'ListenAddress is not an IPv4 or IPv6 literal.'
    }

    if ([System.Net.IPAddress]::IsLoopback($parsed) `
        -or $parsed.Equals([System.Net.IPAddress]::Any) `
        -or $parsed.Equals([System.Net.IPAddress]::IPv6Any)) {
        throw 'Loopback and wildcard ListenAddress values are not supported.'
    }

    if ($parsed.AddressFamily `
        -eq [System.Net.Sockets.AddressFamily]::InterNetwork) {
        if ($Value -notmatch '^([0-9]{1,3}\.){3}[0-9]{1,3}$') {
            throw 'IPv4 ListenAddress must use four decimal octets.'
        }

        foreach ($octet in $Value.Split('.')) {
            if (($octet.Length -gt 1 -and $octet[0] -eq '0') `
                -or [int]$octet -gt 255) {
                throw 'IPv4 ListenAddress must use canonical decimal octets.'
            }
        }

        $addressBytes = $parsed.GetAddressBytes()
        if ($addressBytes[0] -eq 0 -or $addressBytes[0] -ge 224) {
            throw 'IPv4 multicast, reserved and current-network addresses are not supported.'
        }

        $canonical = $parsed.ToString()
    }
    elseif ($parsed.AddressFamily `
        -eq [System.Net.Sockets.AddressFamily]::InterNetworkV6) {
        if ($parsed.IsIPv4MappedToIPv6 `
            -or $parsed.IsIPv6LinkLocal `
            -or $parsed.IsIPv6Multicast `
            -or $parsed.ScopeId -ne 0) {
            throw 'Mapped, link-local, multicast and scoped IPv6 addresses are not supported.'
        }

        $canonical = $parsed.ToString().ToLowerInvariant()
    }
    else {
        throw 'Only IPv4 and IPv6 ListenAddress values are supported.'
    }

    if (-not [StringComparer]::Ordinal.Equals($Value, $canonical)) {
        throw "ListenAddress must use canonical form '$canonical'."
    }

    return $canonical
}

function Get-EligibleAddresses {
    $profileStates = @{}
    $profiles = Get-NetConnectionProfile -ErrorAction Stop
    foreach ($profile in $profiles) {
        $connected = $profile.IPv4Connectivity -ne 'Disconnected' `
            -or $profile.IPv6Connectivity -ne 'Disconnected'
        if (-not $connected) {
            continue
        }

        $index = [int]$profile.InterfaceIndex
        if (-not $profileStates.ContainsKey($index)) {
            $profileStates[$index] = [pscustomobject]@{
                Trusted = $false
                Untrusted = $false
            }
        }

        $allowedProfile = $profile.NetworkCategory -eq 'DomainAuthenticated' `
            -or $profile.NetworkCategory -eq 'Private'
        if ($allowedProfile) {
            $profileStates[$index].Trusted = $true
        }
        else {
            $profileStates[$index].Untrusted = $true
        }
    }

    $eligibleInterfaceIndexes = New-Object 'System.Collections.Generic.HashSet[int]'
    foreach ($entry in $profileStates.GetEnumerator()) {
        if ($entry.Value.Trusted -and -not $entry.Value.Untrusted) {
            [void]$eligibleInterfaceIndexes.Add([int]$entry.Key)
        }
    }

    $addresses = New-Object 'System.Collections.Generic.HashSet[string]' `
        ([StringComparer]::Ordinal)
    foreach ($ip in (Get-NetIPAddress -ErrorAction Stop)) {
        if (-not $eligibleInterfaceIndexes.Contains([int]$ip.InterfaceIndex) `
            -or $ip.AddressState -ne 'Preferred') {
            continue
        }

        try {
            $canonical = ConvertTo-CanonicalAddress -Value $ip.IPAddress
            [void]$addresses.Add($canonical)
        }
        catch {
            continue
        }
    }

    return @($addresses | Sort-Object)
}

function Assert-AddressIsEligible {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Value
    )

    $canonical = ConvertTo-CanonicalAddress -Value $Value
    $eligible = @(Get-EligibleAddresses)
    if (-not ($eligible -ccontains $canonical)) {
        throw 'ListenAddress is not assigned to an active Domain or Private network interface.'
    }

    return $canonical
}

function Write-Utf8Lines {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,

        [Parameter(Mandatory = $true)]
        [AllowEmptyCollection()]
        [string[]]$Lines
    )

    $fullPath = [System.IO.Path]::GetFullPath($Path)
    $parent = [System.IO.Path]::GetDirectoryName($fullPath)
    if (-not (Test-Path -LiteralPath $parent -PathType Container)) {
        throw "Output directory '$parent' does not exist."
    }

    [System.IO.File]::WriteAllLines(
        $fullPath,
        $Lines,
        [System.Text.UTF8Encoding]::new($false))
}
