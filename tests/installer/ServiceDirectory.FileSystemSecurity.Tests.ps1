#requires -Version 5.1

[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repositoryRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$helperPath = Join-Path $repositoryRoot `
    'installer\scripts\ServiceDirectory.FileSystemSecurity.ps1'
if (-not (Test-Path -LiteralPath $helperPath -PathType Leaf)) {
    throw "File system security helper '$helperPath' was not found."
}

function Assert-NoReparsePoint {
    param([Parameter(Mandatory = $true)][string]$Path)

    $current = [System.IO.Path]::GetFullPath($Path)
    while (-not [string]::IsNullOrEmpty($current)) {
        if (-not (Test-Path -LiteralPath $current)) {
            throw "Required test path '$current' does not exist."
        }
        $item = Get-Item -LiteralPath $current -Force
        if (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) `
            -ne 0) {
            throw "A reparse point is not allowed in test path '$current'."
        }

        $parent = [System.IO.Path]::GetDirectoryName($current)
        if ([string]::IsNullOrEmpty($parent) `
            -or [StringComparer]::OrdinalIgnoreCase.Equals(
                $parent,
                $current)) {
            break
        }
        $current = $parent
    }
}

. $helperPath

$temporaryRoot = [System.IO.Path]::GetFullPath(
    [System.IO.Path]::GetTempPath()).TrimEnd('\')
$testRoot = [System.IO.Path]::GetFullPath(
    (Join-Path $temporaryRoot (
            'deepai-service-directory-acl-test-' `
                + [Guid]::NewGuid().ToString('N'))))
$temporaryPrefix = $temporaryRoot + '\'
if (-not $testRoot.StartsWith(
        $temporaryPrefix,
        [StringComparison]::OrdinalIgnoreCase)) {
    throw "ACL test path '$testRoot' is outside '$temporaryRoot'."
}

try {
    [void](New-Item -ItemType Directory -Path $testRoot)
    $childPath = Join-Path $testRoot 'child.txt'
    [System.IO.File]::WriteAllText(
        $childPath,
        'acl-round-trip',
        [System.Text.UTF8Encoding]::new($false))

    $before = @(Get-FileSystemAclSnapshot -Roots @($testRoot))
    if ($before.Count -ne 2) {
        throw "Expected two ACL snapshots, found $($before.Count)."
    }
    foreach ($snapshot in $before) {
        if ([string]::IsNullOrWhiteSpace([string]$snapshot.Sddl)) {
            throw "ACL snapshot '$($snapshot.Path)' has no SDDL descriptor."
        }
    }

    Restore-FileSystemAclSnapshot -Snapshots $before
    $after = @(Get-FileSystemAclSnapshot -Roots @($testRoot))
    if ($after.Count -ne $before.Count) {
        throw 'ACL snapshot count changed after rollback round-trip.'
    }

    $afterByPath = @{}
    foreach ($snapshot in $after) {
        $afterByPath[[string]$snapshot.Path] = [string]$snapshot.Sddl
    }
    foreach ($snapshot in $before) {
        $actual = $afterByPath[[string]$snapshot.Path]
        if (-not [StringComparer]::Ordinal.Equals(
                [string]$snapshot.Sddl,
                $actual)) {
            throw "ACL descriptor changed for '$($snapshot.Path)'."
        }
    }

    Write-Host 'Installer file system ACL round-trip test passed.'
}
finally {
    if (Test-Path -LiteralPath $testRoot) {
        $resolvedTestRoot = [System.IO.Path]::GetFullPath($testRoot)
        if (-not [StringComparer]::OrdinalIgnoreCase.Equals(
                $resolvedTestRoot,
                $testRoot) `
            -or -not $resolvedTestRoot.StartsWith(
                $temporaryPrefix,
                [StringComparison]::OrdinalIgnoreCase)) {
            throw "Refusing to remove unexpected ACL test path '$testRoot'."
        }
        Assert-NoReparsePoint -Path $resolvedTestRoot
        Remove-Item -LiteralPath $resolvedTestRoot -Recurse -Force
    }
}
