function New-FileSystemGrant {
    param(
        [Parameter(Mandatory = $true)][string]$Sid,
        [Parameter(Mandatory = $true)]
        [System.Security.AccessControl.FileSystemRights]$Rights
    )

    return [pscustomobject]@{
        Sid = [System.Security.Principal.SecurityIdentifier]::new($Sid)
        Rights = $Rights
    }
}

function Get-FileSystemTreeItems {
    param([Parameter(Mandatory = $true)][string]$Path)

    $root = Get-Item -LiteralPath $Path -Force -ErrorAction Stop
    if (-not $root.PSIsContainer) {
        throw "ACL root '$Path' is not a directory."
    }

    $items = @($root) + @(Get-ChildItem -LiteralPath $Path -Recurse -Force `
        -ErrorAction Stop)
    foreach ($item in $items) {
        if (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) `
            -ne 0) {
            throw "A reparse point is not allowed in ACL scope '$($item.FullName)'."
        }
    }

    return $items
}

function Set-DirectoryAcl {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][object[]]$Grants
    )

    $items = @(Get-FileSystemTreeItems -Path $Path)
    $administratorsSid = [System.Security.Principal.SecurityIdentifier]::new(
        'S-1-5-32-544')
    $rootSecurity = [System.Security.AccessControl.DirectorySecurity]::new()
    $rootSecurity.SetOwner($administratorsSid)
    $rootSecurity.SetAccessRuleProtection($true, $false)
    foreach ($grant in $Grants) {
        $rule = [System.Security.AccessControl.FileSystemAccessRule]::new(
            $grant.Sid,
            $grant.Rights,
            [System.Security.AccessControl.InheritanceFlags]::ContainerInherit `
                -bor [System.Security.AccessControl.InheritanceFlags]::ObjectInherit,
            [System.Security.AccessControl.PropagationFlags]::None,
            [System.Security.AccessControl.AccessControlType]::Allow)
        [void]$rootSecurity.AddAccessRule($rule)
    }

    Set-Acl -LiteralPath $Path -AclObject $rootSecurity -ErrorAction Stop

    foreach ($item in ($items | Select-Object -Skip 1 | Sort-Object {
                $_.FullName.Length })) {
        $security = Get-Acl -LiteralPath $item.FullName -ErrorAction Stop
        $security.SetOwner($administratorsSid)
        $explicitRules = @($security.GetAccessRules(
                $true,
                $false,
                [System.Security.Principal.SecurityIdentifier]))
        foreach ($rule in $explicitRules) {
            [void]$security.RemoveAccessRuleSpecific($rule)
        }
        $security.SetAccessRuleProtection($false, $false)
        Set-Acl -LiteralPath $item.FullName -AclObject $security `
            -ErrorAction Stop
    }

    $actualRoot = Get-Acl -LiteralPath $Path -ErrorAction Stop
    $expectedSddl = $rootSecurity.GetSecurityDescriptorSddlForm(
        [System.Security.AccessControl.AccessControlSections]::Owner `
            -bor [System.Security.AccessControl.AccessControlSections]::Access)
    $actualSddl = $actualRoot.GetSecurityDescriptorSddlForm(
        [System.Security.AccessControl.AccessControlSections]::Owner `
            -bor [System.Security.AccessControl.AccessControlSections]::Access)
    if (-not $actualRoot.AreAccessRulesProtected `
        -or -not [StringComparer]::Ordinal.Equals($expectedSddl, $actualSddl)) {
        throw "Protected exact DACL and owner verification failed for '$Path'."
    }

    foreach ($item in ($items | Select-Object -Skip 1)) {
        $security = Get-Acl -LiteralPath $item.FullName -ErrorAction Stop
        $owner = $security.GetOwner(
            [System.Security.Principal.SecurityIdentifier])
        $explicitRules = @($security.GetAccessRules(
                $true,
                $false,
                [System.Security.Principal.SecurityIdentifier]))
        if ($owner -ne $administratorsSid `
            -or $security.AreAccessRulesProtected `
            -or $explicitRules.Count -ne 0) {
            throw "Inherited-only DACL and owner verification failed for '$($item.FullName)'."
        }
    }
}

function Set-PeerSecretFileAcl {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$MainServiceSid
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        return
    }
    Assert-NoReparsePoint -Path $Path
    $ownerSid = [System.Security.Principal.SecurityIdentifier]::new(
        $MainServiceSid)
    $security = [System.Security.AccessControl.FileSecurity]::new()
    $security.SetOwner($ownerSid)
    $security.SetAccessRuleProtection($true, $false)
    foreach ($sidText in @('S-1-5-18', 'S-1-5-32-544', $MainServiceSid)) {
        $sid = [System.Security.Principal.SecurityIdentifier]::new($sidText)
        [void]$security.AddAccessRule(
            [System.Security.AccessControl.FileSystemAccessRule]::new(
                $sid,
                [System.Security.AccessControl.FileSystemRights]::FullControl,
                [System.Security.AccessControl.AccessControlType]::Allow))
    }
    Set-Acl -LiteralPath $Path -AclObject $security -ErrorAction Stop

    $actual = Get-Acl -LiteralPath $Path -ErrorAction Stop
    $sections = [System.Security.AccessControl.AccessControlSections]::Owner `
        -bor [System.Security.AccessControl.AccessControlSections]::Access
    if (-not $actual.AreAccessRulesProtected `
        -or -not [StringComparer]::Ordinal.Equals(
            $security.GetSecurityDescriptorSddlForm($sections),
            $actual.GetSecurityDescriptorSddlForm($sections))) {
        throw "Protected exact peer secret DACL verification failed for '$Path'."
    }
}

function Set-InstallationAcls {
    param(
        [Parameter(Mandatory = $true)][string]$RequestedInstallRoot,
        [Parameter(Mandatory = $true)][string]$RequestedDataRoot
    )

    $mainSid = (Get-AccountSid -AccountName "NT SERVICE\$mainServiceName").Value
    $watchdogSid =
        (Get-AccountSid -AccountName "NT SERVICE\$watchdogServiceName").Value
    $peerSecretPath = Join-Path $RequestedDataRoot 'secrets\peer.dat'
    if (Test-Path -LiteralPath ($peerSecretPath + '.bak') -PathType Leaf) {
        throw 'peer.dat.bak is not an allowed normal installation state.'
    }
    Set-DirectoryAcl -Path $RequestedInstallRoot -Grants @(
        (New-FileSystemGrant -Sid 'S-1-5-18' -Rights FullControl),
        (New-FileSystemGrant -Sid 'S-1-5-32-544' -Rights FullControl),
        (New-FileSystemGrant -Sid 'S-1-5-32-545' -Rights ReadAndExecute),
        (New-FileSystemGrant -Sid $mainSid -Rights ReadAndExecute),
        (New-FileSystemGrant -Sid $watchdogSid -Rights ReadAndExecute))
    Set-DirectoryAcl -Path $RequestedDataRoot -Grants @(
        (New-FileSystemGrant -Sid 'S-1-5-18' -Rights FullControl),
        (New-FileSystemGrant -Sid 'S-1-5-32-544' -Rights FullControl),
        (New-FileSystemGrant -Sid $mainSid -Rights Modify))
    Set-DirectoryAcl -Path (Join-Path $RequestedDataRoot 'secrets') -Grants @(
        (New-FileSystemGrant -Sid 'S-1-5-18' -Rights FullControl),
        (New-FileSystemGrant -Sid 'S-1-5-32-544' -Rights FullControl),
        (New-FileSystemGrant -Sid $mainSid -Rights Modify))
    Set-PeerSecretFileAcl -Path $peerSecretPath -MainServiceSid $mainSid
}

function Get-FileSystemAclSnapshot {
    param([Parameter(Mandatory = $true)][string[]]$Roots)

    $snapshots = New-Object 'System.Collections.Generic.List[object]'
    foreach ($root in $Roots) {
        if (-not (Test-Path -LiteralPath $root)) {
            continue
        }

        foreach ($item in (Get-FileSystemTreeItems -Path $root)) {
            $security = Get-Acl -LiteralPath $item.FullName -ErrorAction Stop
            [void]$snapshots.Add([pscustomobject]@{
                    Path = $item.FullName
                    Sddl = $security.GetSecurityDescriptorSddlForm(
                        [System.Security.AccessControl.AccessControlSections]::Owner `
                            -bor [System.Security.AccessControl.AccessControlSections]::Group `
                            -bor [System.Security.AccessControl.AccessControlSections]::Access)
                })
        }
    }
    return $snapshots.ToArray()
}

function Restore-FileSystemAclSnapshot {
    param([Parameter(Mandatory = $true)][object[]]$Snapshots)

    foreach ($snapshot in ($Snapshots | Sort-Object { $_.Path.Length } `
            -Descending)) {
        if (-not (Test-Path -LiteralPath $snapshot.Path)) {
            throw "ACL rollback target '$($snapshot.Path)' is missing."
        }
        Assert-NoReparsePoint -Path $snapshot.Path
        $item = Get-Item -LiteralPath $snapshot.Path -Force
        $security = if ($item.PSIsContainer) {
            [System.Security.AccessControl.DirectorySecurity]::new()
        }
        else {
            [System.Security.AccessControl.FileSecurity]::new()
        }
        $security.SetSecurityDescriptorSddlForm(
            [string]$snapshot.Sddl,
            [System.Security.AccessControl.AccessControlSections]::Owner `
                -bor [System.Security.AccessControl.AccessControlSections]::Group `
                -bor [System.Security.AccessControl.AccessControlSections]::Access)
        Set-Acl -LiteralPath $snapshot.Path -AclObject $security `
            -ErrorAction Stop
    }

    $sections = [System.Security.AccessControl.AccessControlSections]::Owner `
        -bor [System.Security.AccessControl.AccessControlSections]::Group `
        -bor [System.Security.AccessControl.AccessControlSections]::Access
    foreach ($snapshot in $Snapshots) {
        $actual = (Get-Acl -LiteralPath $snapshot.Path -ErrorAction Stop).
            GetSecurityDescriptorSddlForm($sections)
        if (-not [StringComparer]::Ordinal.Equals(
                [string]$snapshot.Sddl,
                $actual)) {
            throw "ACL rollback verification failed for '$($snapshot.Path)'."
        }
    }
}
