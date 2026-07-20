[CmdletBinding()]
param(
    [ValidateSet('Release')]
    [string]$Configuration = 'Release',

    [ValidateSet('Latest', '2019', '2022')]
    [string]$VisualStudioVersion = 'Latest',

    [string]$MSBuildPath,

    [string]$VSTestPath,

    [string]$InnoSetupCompilerPath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Read-Utf8XmlDocument {
    param([Parameter(Mandatory = $true)][string]$Path)

    $text = [System.IO.File]::ReadAllText(
        $Path,
        [System.Text.Encoding]::UTF8)
    $settings = [System.Xml.XmlReaderSettings]::new()
    $settings.DtdProcessing = [System.Xml.DtdProcessing]::Prohibit
    $settings.XmlResolver = $null
    $settings.MaxCharactersInDocument = 16MB
    $stringReader = [System.IO.StringReader]::new($text)
    $xmlReader = [System.Xml.XmlReader]::Create($stringReader, $settings)
    try {
        $document = [System.Xml.XmlDocument]::new()
        $document.XmlResolver = $null
        $document.Load($xmlReader)
        return ,$document
    }
    finally {
        $xmlReader.Dispose()
        $stringReader.Dispose()
    }
}

function Read-RepositoryVersion {
    param([Parameter(Mandatory = $true)][string]$Path)

    $text = [System.IO.File]::ReadAllText(
        $Path,
        [System.Text.Encoding]::UTF8)
    $match = [regex]::Match(
        $text,
        '\AVERSION=((0|[1-9][0-9]{0,4})\.(0|[1-9][0-9]{0,4})\.(0|[1-9][0-9]{0,4}))\r?\nBUILD=([1-9][0-9]{0,4})(?:\r?\n)?\z',
        [System.Text.RegularExpressions.RegexOptions]::CultureInvariant)
    if (-not $match.Success) {
        throw "VERSION file '$Path' is not in the required canonical form."
    }

    return [pscustomobject]@{
        ProductVersion = $match.Groups[1].Value
        BuildNumber = $match.Groups[5].Value
    }
}

function Assert-NoPackageReparsePoint {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [switch]$IncludeTree
    )

    $fullPath = [System.IO.Path]::GetFullPath($Path)
    $current = $fullPath
    while (-not [string]::IsNullOrEmpty($current)) {
        $pathExists = $true
        try {
            $attributes = [System.IO.File]::GetAttributes($current)
        }
        catch [System.IO.FileNotFoundException] {
            $pathExists = $false
        }
        catch [System.IO.DirectoryNotFoundException] {
            $pathExists = $false
        }
        if ($pathExists `
            -and ($attributes -band [System.IO.FileAttributes]::ReparsePoint) `
            -ne 0) {
            throw "A reparse point is not allowed in package path '$current'."
        }

        $parent = [System.IO.Path]::GetDirectoryName($current)
        if ([string]::IsNullOrEmpty($parent) `
            -or [StringComparer]::OrdinalIgnoreCase.Equals($parent, $current)) {
            break
        }
        $current = $parent
    }

    if (-not $IncludeTree `
        -or -not (Test-Path -LiteralPath $fullPath -PathType Container)) {
        return
    }

    foreach ($item in (Get-ChildItem -LiteralPath $fullPath -Recurse -Force `
            -ErrorAction Stop)) {
        if (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) `
            -ne 0) {
            throw "A reparse point is not allowed in package tree '$($item.FullName)'."
        }
    }
}

function Assert-ExactPackageDirectory {
    param(
        [Parameter(Mandatory = $true)][string]$ExpectedPath,
        [Parameter(Mandatory = $true)][string]$Path,
        [switch]$AllowMissing,
        [switch]$IncludeTree
    )

    $expected = [System.IO.Path]::GetFullPath($ExpectedPath).TrimEnd('\')
    $actual = [System.IO.Path]::GetFullPath($Path).TrimEnd('\')
    if (-not [StringComparer]::OrdinalIgnoreCase.Equals($expected, $actual)) {
        throw "Package path must be exactly '$expected'."
    }

    Assert-NoPackageReparsePoint -Path $actual -IncludeTree:$IncludeTree
    if (Test-Path -LiteralPath $actual) {
        if (-not (Test-Path -LiteralPath $actual -PathType Container)) {
            throw "Package directory '$actual' is not a directory."
        }
    }
    elseif (-not $AllowMissing) {
        throw "Required package directory '$actual' does not exist."
    }

    return $actual
}

function Remove-ExactPackageDirectoryTree {
    param(
        [Parameter(Mandatory = $true)][string]$ExpectedPath,
        [Parameter(Mandatory = $true)][string]$Path
    )

    $fullPath = Assert-ExactPackageDirectory `
        -ExpectedPath $ExpectedPath `
        -Path $Path `
        -AllowMissing `
        -IncludeTree
    if (Test-Path -LiteralPath $fullPath) {
        Remove-Item -LiteralPath $fullPath -Recurse -Force
    }
    if (Test-Path -LiteralPath $fullPath) {
        throw "Package directory '$fullPath' was not removed."
    }
}

function Assert-SafeStagingPath {
    param(
        [Parameter(Mandatory = $true)][string]$RepositoryRoot,
        [Parameter(Mandatory = $true)][string]$Path,
        [switch]$AllowMissing,
        [switch]$IncludeTree
    )

    return Assert-ExactPackageDirectory `
        -ExpectedPath (Join-Path $RepositoryRoot 'artifacts\service-directory\package') `
        -Path $Path `
        -AllowMissing:$AllowMissing `
        -IncludeTree:$IncludeTree
}

function Publish-ValidatedInstaller {
    param(
        [Parameter(Mandatory = $true)][string]$StagedInstallerPath,
        [Parameter(Mandatory = $true)][string]$FinalInstallerPath,
        [Parameter(Mandatory = $true)][string]$InstallerRoot,
        [Parameter(Mandatory = $true)][string]$StagingOutputRoot
    )

    $installerDirectory = Assert-ExactPackageDirectory `
        -ExpectedPath $InstallerRoot `
        -Path $InstallerRoot `
        -IncludeTree
    $stagingDirectory = Assert-ExactPackageDirectory `
        -ExpectedPath $StagingOutputRoot `
        -Path $StagingOutputRoot `
        -IncludeTree
    $stagedPath = [System.IO.Path]::GetFullPath($StagedInstallerPath)
    $finalPath = [System.IO.Path]::GetFullPath($FinalInstallerPath)
    $fileName = [System.IO.Path]::GetFileName($finalPath)
    $expectedStagedPath = Join-Path $stagingDirectory $fileName
    $expectedFinalPath = Join-Path $installerDirectory $fileName
    if (-not [StringComparer]::OrdinalIgnoreCase.Equals(
            $stagedPath,
            $expectedStagedPath) `
        -or -not [StringComparer]::OrdinalIgnoreCase.Equals(
            $finalPath,
            $expectedFinalPath)) {
        throw 'Installer publication paths do not match the validated package roots.'
    }
    if (-not (Test-Path -LiteralPath $stagedPath -PathType Leaf)) {
        throw "Validated staged installer '$stagedPath' does not exist."
    }
    Assert-NoPackageReparsePoint -Path $stagedPath
    $stagedLength = (Get-Item -LiteralPath $stagedPath -Force).Length
    if ($stagedLength -le 0) {
        throw "Validated staged installer '$stagedPath' is empty."
    }

    $stagingVolume = [System.IO.Path]::GetPathRoot($stagedPath)
    $installerVolume = [System.IO.Path]::GetPathRoot($finalPath)
    if (-not [StringComparer]::OrdinalIgnoreCase.Equals(
            $stagingVolume,
            $installerVolume)) {
        throw 'Staged and final installer paths must be on the same volume.'
    }
    if ((Test-Path -LiteralPath $finalPath) `
        -and -not (Test-Path -LiteralPath $finalPath -PathType Leaf)) {
        throw "Final installer path '$finalPath' is not a regular file path."
    }

    if (Test-Path -LiteralPath $finalPath -PathType Leaf) {
        Assert-NoPackageReparsePoint -Path $finalPath
        [System.IO.File]::Replace($stagedPath, $finalPath, $null, $true)
    }
    else {
        [System.IO.File]::Move($stagedPath, $finalPath)
    }

    Assert-NoPackageReparsePoint -Path $finalPath
    if (-not (Test-Path -LiteralPath $finalPath -PathType Leaf) `
        -or (Get-Item -LiteralPath $finalPath -Force).Length -ne $stagedLength) {
        throw "Published installer '$finalPath' could not be verified."
    }
}

function Test-FileBytesEqual {
    param(
        [Parameter(Mandatory = $true)][string]$Left,
        [Parameter(Mandatory = $true)][string]$Right
    )

    $leftInfo = Get-Item -LiteralPath $Left
    $rightInfo = Get-Item -LiteralPath $Right
    if ($leftInfo.Length -ne $rightInfo.Length) {
        return $false
    }

    $leftStream = [System.IO.File]::OpenRead($Left)
    $rightStream = [System.IO.File]::OpenRead($Right)
    try {
        $leftBuffer = New-Object byte[] 65536
        $rightBuffer = New-Object byte[] 65536
        while ($true) {
            $leftRead = $leftStream.Read($leftBuffer, 0, $leftBuffer.Length)
            $rightRead = $rightStream.Read($rightBuffer, 0, $rightBuffer.Length)
            if ($leftRead -ne $rightRead) {
                return $false
            }
            if ($leftRead -eq 0) {
                return $true
            }
            for ($index = 0; $index -lt $leftRead; $index++) {
                if ($leftBuffer[$index] -ne $rightBuffer[$index]) {
                    return $false
                }
            }
        }
    }
    finally {
        $leftStream.Dispose()
        $rightStream.Dispose()
    }
}

function Copy-RuntimeOutput {
    param(
        [Parameter(Mandatory = $true)][string]$SourceDirectory,
        [Parameter(Mandatory = $true)][string]$DestinationDirectory
    )

    if (-not (Test-Path -LiteralPath $SourceDirectory -PathType Container)) {
        throw "Release output directory '$SourceDirectory' does not exist."
    }

    $sourceRoot = [System.IO.Path]::GetFullPath($SourceDirectory).TrimEnd('\')
    Assert-NoPackageReparsePoint -Path $sourceRoot -IncludeTree

    $files = @(Get-ChildItem -LiteralPath $sourceRoot -Recurse -File `
        -ErrorAction Stop | Where-Object {
            $_.Extension -ine '.pdb' `
                -and $_.Name -notlike '*.vshost.*' `
                -and $_.Name -ne '.lastcodeanalysissucceeded'
        })
    if ($files.Count -eq 0) {
        throw "Release output directory '$SourceDirectory' is empty."
    }

    foreach ($file in $files) {
        $relativePath = $file.FullName.Substring($sourceRoot.Length).TrimStart('\')
        $destinationPath = Join-Path $DestinationDirectory $relativePath
        $destinationParent = Split-Path -Parent $destinationPath
        [void](New-Item -ItemType Directory -Path $destinationParent -Force)
        if (Test-Path -LiteralPath $destinationPath -PathType Leaf) {
            if (-not (Test-FileBytesEqual `
                    -Left $file.FullName `
                    -Right $destinationPath)) {
                throw "Conflicting runtime files map to '$relativePath'."
            }
            continue
        }

        Copy-Item -LiteralPath $file.FullName -Destination $destinationPath
    }
}

function Get-NuGetPackageRoot {
    if (-not [string]::IsNullOrWhiteSpace($env:NUGET_PACKAGES)) {
        return [System.IO.Path]::GetFullPath($env:NUGET_PACKAGES)
    }

    return [System.IO.Path]::GetFullPath(
        (Join-Path $env:USERPROFILE '.nuget\packages'))
}

function New-RuntimeDependencyNotices {
    param(
        [Parameter(Mandatory = $true)][string[]]$LockFilePaths,
        [Parameter(Mandatory = $true)][string]$RootNoticePath,
        [Parameter(Mandatory = $true)][string]$NoticesDirectory,
        [Parameter(Mandatory = $true)][string]$ProductVersion
    )

    [void](New-Item -ItemType Directory -Path $NoticesDirectory -Force)
    Copy-Item -LiteralPath $RootNoticePath `
        -Destination (Join-Path $NoticesDirectory 'THIRD-PARTY-NOTICES.md')
    $licenseDirectory = Join-Path $NoticesDirectory 'licenses'
    [void](New-Item -ItemType Directory -Path $licenseDirectory -Force)

    $packageRoot = Get-NuGetPackageRoot
    $components = New-Object 'System.Collections.Generic.List[object]'
    $noticeLines = New-Object 'System.Collections.Generic.List[string]'
    [void]$noticeLines.Add('# Resolved runtime package licenses')
    [void]$noticeLines.Add('')
    [void]$noticeLines.Add(
        'This list is generated from the locked .NET Framework 4.8 runtime dependency graph. It contains no package or installer hashes.')
    [void]$noticeLines.Add('')

    $resolvedPackages = @{}
    foreach ($lockFilePath in $LockFilePaths) {
        $lockText = [System.IO.File]::ReadAllText(
            $lockFilePath,
            [System.Text.Encoding]::UTF8)
        $lock = $lockText | ConvertFrom-Json
        $framework = $lock.dependencies.'.NETFramework,Version=v4.8'
        if ($null -eq $framework) {
            throw "Runtime dependency lock '$lockFilePath' has no .NET Framework 4.8 target."
        }

        foreach ($property in $framework.PSObject.Properties) {
            $dependency = $property.Value
            if ($dependency.type -eq 'Project') {
                continue
            }

            $packageName = $property.Name
            $packageVersion = [string]$dependency.resolved
            if ([string]::IsNullOrWhiteSpace($packageVersion)) {
                throw "Package '$packageName' has no resolved version in '$lockFilePath'."
            }
            $key = $packageName.ToLowerInvariant() + '|' `
                + $packageVersion.ToLowerInvariant()
            if (-not $resolvedPackages.ContainsKey($key)) {
                $resolvedPackages[$key] = [pscustomobject]@{
                    Name = $packageName
                    Version = $packageVersion
                }
            }
        }
    }

    foreach ($resolvedPackage in ($resolvedPackages.Values | Sort-Object Name, Version)) {
        $packageName = $resolvedPackage.Name
        $packageVersion = $resolvedPackage.Version
        $packageDirectory = Join-Path `
            (Join-Path $packageRoot $packageName.ToLowerInvariant()) `
            $packageVersion.ToLowerInvariant()
        if (-not (Test-Path -LiteralPath $packageDirectory -PathType Container)) {
            throw "Restored package '$packageName $packageVersion' was not found at '$packageDirectory'."
        }

        $nuspecPath = Join-Path $packageDirectory `
            ($packageName.ToLowerInvariant() + '.nuspec')
        if (-not (Test-Path -LiteralPath $nuspecPath -PathType Leaf)) {
            $nuspecPath = Get-ChildItem -LiteralPath $packageDirectory `
                -File -Filter '*.nuspec' | Select-Object -First 1 `
                -ExpandProperty FullName
        }
        if ([string]::IsNullOrWhiteSpace($nuspecPath)) {
            throw "Package '$packageName $packageVersion' has no nuspec metadata."
        }

        [System.Xml.XmlDocument]$nuspec = Read-Utf8XmlDocument -Path $nuspecPath
        $metadata = $nuspec.SelectSingleNode(
            "/*[local-name()='package']/*[local-name()='metadata']")
        if ($null -eq $metadata) {
            throw "Package '$packageName $packageVersion' has invalid nuspec metadata."
        }

        $authorsNode = $metadata.SelectSingleNode("*[local-name()='authors']")
        $projectUrlNode = $metadata.SelectSingleNode("*[local-name()='projectUrl']")
        $licenseNode = $metadata.SelectSingleNode("*[local-name()='license']")
        $licenseUrlNode = $metadata.SelectSingleNode("*[local-name()='licenseUrl']")
        $authors = if ($null -eq $authorsNode) { 'UNKNOWN' } else { $authorsNode.InnerText }
        $projectUrl = if ($null -eq $projectUrlNode) { '' } else { $projectUrlNode.InnerText }
        $licenseValue = if ($null -eq $licenseNode) { '' } else { $licenseNode.InnerText }
        $licenseType = if ($null -eq $licenseNode) { '' } else { $licenseNode.GetAttribute('type') }
        $licenseUrl = if ($null -eq $licenseUrlNode) { '' } else { $licenseUrlNode.InnerText }

        $copiedLicenseFiles = New-Object 'System.Collections.Generic.List[string]'
        $licenseCandidates = @(Get-ChildItem -LiteralPath $packageDirectory -File |
            Where-Object {
                $_.Name -match '^(LICENSE|LICENCE|COPYING|NOTICE|THIRD-PARTY-NOTICES)(\.|$)'
            } |
            Sort-Object Name)
        foreach ($candidate in $licenseCandidates) {
            $safePackageName = $packageName -replace '[^A-Za-z0-9._-]', '_'
            $destinationName = $safePackageName + '-' + $packageVersion `
                + '-' + $candidate.Name
            Copy-Item -LiteralPath $candidate.FullName `
                -Destination (Join-Path $licenseDirectory $destinationName)
            [void]$copiedLicenseFiles.Add('licenses/' + $destinationName)
        }

        if ([string]::IsNullOrWhiteSpace($licenseValue) `
            -and [string]::IsNullOrWhiteSpace($licenseUrl) `
            -and $copiedLicenseFiles.Count -eq 0) {
            throw "Package '$packageName $packageVersion' has no usable license metadata or notice file."
        }

        $licenseDescription = if (-not [string]::IsNullOrWhiteSpace($licenseValue)) {
            $licenseValue
        } elseif (-not [string]::IsNullOrWhiteSpace($licenseUrl)) {
            $licenseUrl
        } else {
            'See bundled package license files'
        }
        [void]$noticeLines.Add("## $packageName $packageVersion")
        [void]$noticeLines.Add('')
        [void]$noticeLines.Add("- Authors: $authors")
        [void]$noticeLines.Add("- License: $licenseDescription")
        if (-not [string]::IsNullOrWhiteSpace($projectUrl)) {
            [void]$noticeLines.Add("- Project: $projectUrl")
        }
        foreach ($copiedFile in $copiedLicenseFiles) {
            [void]$noticeLines.Add("- Bundled notice: $copiedFile")
        }
        [void]$noticeLines.Add('')

        $licenseObject = if ($licenseType -eq 'expression' `
            -and -not [string]::IsNullOrWhiteSpace($licenseValue)) {
            @{ license = @{ id = $licenseValue } }
        } else {
            @{ license = @{ name = $licenseDescription } }
        }
        [void]$components.Add([ordered]@{
            type = 'library'
            name = $packageName
            version = $packageVersion
            scope = 'required'
            purl = 'pkg:nuget/' + [Uri]::EscapeDataString($packageName) `
                + '@' + [Uri]::EscapeDataString($packageVersion)
            licenses = @($licenseObject)
        })
    }

    if ($components.Count -eq 0) {
        throw 'The runtime dependency lock did not contain any NuGet packages.'
    }

    [System.IO.File]::WriteAllLines(
        (Join-Path $NoticesDirectory 'RESOLVED-PACKAGE-LICENSES.md'),
        $noticeLines,
        [System.Text.UTF8Encoding]::new($false))
    $sbom = [ordered]@{
        bomFormat = 'CycloneDX'
        specVersion = '1.5'
        version = 1
        metadata = [ordered]@{
            component = [ordered]@{
                type = 'application'
                name = 'DEEPAi Service Directory'
                version = $ProductVersion
            }
        }
        components = $components.ToArray()
    }
    $sbomText = $sbom | ConvertTo-Json -Depth 12
    [System.IO.File]::WriteAllText(
        (Join-Path $NoticesDirectory 'sbom.cdx.json'),
        $sbomText + [Environment]::NewLine,
        [System.Text.UTF8Encoding]::new($false))
}

function Find-InnoSetupCompiler {
    param([string]$RequestedPath)

    if (-not [string]::IsNullOrWhiteSpace($RequestedPath)) {
        if (-not (Test-Path -LiteralPath $RequestedPath -PathType Leaf)) {
            throw "Inno Setup compiler '$RequestedPath' was not found."
        }
        return [System.IO.Path]::GetFullPath($RequestedPath)
    }

    $candidates = @(
        (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
        (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe')
    )
    foreach ($candidate in $candidates) {
        if (Test-Path -LiteralPath $candidate -PathType Leaf) {
            return $candidate
        }
    }

    throw 'ISCC.exe was not found. Install Inno Setup 6.3 or later, or pass -InnoSetupCompilerPath.'
}

function Get-InnoSetupCompilerMajorVersion {
    param([Parameter(Mandatory = $true)][string]$Path)

    $versionInfo = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($Path)
    if ($versionInfo.FileMajorPart -gt 0) {
        return $versionInfo.FileMajorPart
    }

    $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $Path
    $startInfo.Arguments = '/?'
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $process = [System.Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    try {
        if (-not $process.Start()) {
            throw "Inno Setup compiler '$Path' could not be started."
        }
        $standardOutput = $process.StandardOutput.ReadToEnd()
        $standardError = $process.StandardError.ReadToEnd()
        $process.WaitForExit()
        $helpOutput = $standardOutput `
            + [Environment]::NewLine `
            + $standardError
    }
    finally {
        $process.Dispose()
    }

    foreach ($line in ($helpOutput -split '\r?\n')) {
        $match = [regex]::Match(
            $line,
            '\AInno Setup ([1-9][0-9]*) Command-Line Compiler\z',
            [System.Text.RegularExpressions.RegexOptions]::CultureInvariant)
        if ($match.Success) {
            return [int]::Parse(
                $match.Groups[1].Value,
                [System.Globalization.CultureInfo]::InvariantCulture)
        }
    }

    throw "Inno Setup compiler '$Path' did not report a valid version."
}

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$versionPath = Join-Path $repositoryRoot 'VERSION'
$installerRoot = Join-Path $repositoryRoot 'installer'
$installerSource = Join-Path $installerRoot 'ServiceDirectory.iss'
$rootNoticePath = Join-Path $repositoryRoot 'THIRD-PARTY-NOTICES.md'
$stagingRoot = Join-Path $repositoryRoot `
    'artifacts\service-directory\package'
$applicationStaging = Join-Path $stagingRoot 'application'
$noticesStaging = Join-Path $stagingRoot 'notices'
$installerOutputStaging = Join-Path $stagingRoot 'installer-output'
$installerAclRegressionTest = Join-Path $repositoryRoot `
    'tests\installer\ServiceDirectory.FileSystemSecurity.Tests.ps1'
$runtimeBuilds = @(
    [pscustomobject]@{
        ProjectPath = Join-Path $repositoryRoot `
            'src\DEEPAi.ServiceDirectory.Service\DEEPAi.ServiceDirectory.Service.csproj'
        OutputDirectory = Join-Path $repositoryRoot `
            'artifacts\service-directory\bin\DEEPAi.ServiceDirectory.Service\x64\Release'
        IntermediateDirectory = Join-Path $repositoryRoot `
            'artifacts\service-directory\obj\DEEPAi.ServiceDirectory.Service\x64\Release'
    },
    [pscustomobject]@{
        ProjectPath = Join-Path $repositoryRoot `
            'src\DEEPAi.ServiceDirectory.Watchdog\DEEPAi.ServiceDirectory.Watchdog.csproj'
        OutputDirectory = Join-Path $repositoryRoot `
            'artifacts\service-directory\bin\DEEPAi.ServiceDirectory.Watchdog\x64\Release'
        IntermediateDirectory = Join-Path $repositoryRoot `
            'artifacts\service-directory\obj\DEEPAi.ServiceDirectory.Watchdog\x64\Release'
    },
    [pscustomobject]@{
        ProjectPath = Join-Path $repositoryRoot `
            'src\DEEPAi.ServiceDirectory.Tray\DEEPAi.ServiceDirectory.Tray.csproj'
        OutputDirectory = Join-Path $repositoryRoot `
            'artifacts\service-directory\bin\DEEPAi.ServiceDirectory.Tray\x64\Release'
        IntermediateDirectory = Join-Path $repositoryRoot `
            'artifacts\service-directory\obj\DEEPAi.ServiceDirectory.Tray\x64\Release'
    }
)

foreach ($requiredFile in @(
        $versionPath,
        $installerSource,
        $rootNoticePath,
        (Join-Path $installerRoot 'scripts\ServiceDirectory.Setup.ps1'),
        (Join-Path $installerRoot 'scripts\ServiceDirectory.Network.ps1'),
        (Join-Path $installerRoot 'scripts\ServiceDirectory.InstallState.ps1'),
        (Join-Path $installerRoot 'scripts\ServiceDirectory.FileSystemSecurity.ps1'),
        $installerAclRegressionTest)) {
    if (-not (Test-Path -LiteralPath $requiredFile -PathType Leaf)) {
        throw "Required package input '$requiredFile' was not found."
    }
}

$version = Read-RepositoryVersion -Path $versionPath
$expectedFileName = 'DEEPAi-ServiceDirectory-' `
    + $version.ProductVersion `
    + '-build.' `
    + $version.BuildNumber `
    + '-x64.exe'
$expectedInstallerPath = Join-Path $installerRoot $expectedFileName
$isccPath = Find-InnoSetupCompiler -RequestedPath $InnoSetupCompilerPath
$isccVersionInfo = [System.Diagnostics.FileVersionInfo]::GetVersionInfo(
    $isccPath)
$isccMajorVersion = Get-InnoSetupCompilerMajorVersion -Path $isccPath
if ($isccMajorVersion -lt 6) {
    throw 'Inno Setup 6.3 or later is required; the installer source enforces the exact minimum version.'
}

[void](Assert-ExactPackageDirectory `
    -ExpectedPath (Join-Path $repositoryRoot 'installer') `
    -Path $installerRoot `
    -IncludeTree)
foreach ($runtimeBuild in $runtimeBuilds) {
    if (-not (Test-Path -LiteralPath $runtimeBuild.ProjectPath `
            -PathType Leaf)) {
        throw "Runtime project '$($runtimeBuild.ProjectPath)' was not found."
    }
    Assert-NoPackageReparsePoint -Path $runtimeBuild.ProjectPath
    Remove-ExactPackageDirectoryTree `
        -ExpectedPath $runtimeBuild.OutputDirectory `
        -Path $runtimeBuild.OutputDirectory
    Remove-ExactPackageDirectoryTree `
        -ExpectedPath $runtimeBuild.IntermediateDirectory `
        -Path $runtimeBuild.IntermediateDirectory
}

$testParameters = @{
    Configuration = $Configuration
    VisualStudioVersion = $VisualStudioVersion
}
if (-not [string]::IsNullOrWhiteSpace($MSBuildPath)) {
    $testParameters['MSBuildPath'] = $MSBuildPath
}
if (-not [string]::IsNullOrWhiteSpace($VSTestPath)) {
    $testParameters['VSTestPath'] = $VSTestPath
}
& (Join-Path $PSScriptRoot 'test.ps1') @testParameters
& $installerAclRegressionTest

Remove-ExactPackageDirectoryTree `
    -ExpectedPath (Join-Path $repositoryRoot 'artifacts\service-directory\package') `
    -Path $stagingRoot
[void](New-Item -ItemType Directory -Path $applicationStaging -Force)
[void](New-Item -ItemType Directory -Path $noticesStaging -Force)
[void](New-Item -ItemType Directory -Path $installerOutputStaging -Force)
[void](Assert-SafeStagingPath `
    -RepositoryRoot $repositoryRoot `
    -Path $stagingRoot `
    -IncludeTree)

try {
    $runtimeProjects = @($runtimeBuilds | ForEach-Object {
            $_.ProjectPath
        })
    $runtimeLockPaths = New-Object 'System.Collections.Generic.List[string]'
    foreach ($runtimeProject in $runtimeProjects) {
        if (-not (Test-Path -LiteralPath $runtimeProject -PathType Leaf)) {
            throw "Runtime project '$runtimeProject' was not found."
        }

        [System.Xml.XmlDocument]$project = Read-Utf8XmlDocument `
            -Path $runtimeProject
        $packageReferences = $project.SelectNodes(
            "//*[local-name()='PackageReference']")
        if ($packageReferences.Count -eq 0) {
            continue
        }

        $lockPath = Join-Path (Split-Path -Parent $runtimeProject) `
            'packages.lock.json'
        if (-not (Test-Path -LiteralPath $lockPath -PathType Leaf)) {
            throw "Runtime project '$runtimeProject' requires lock file '$lockPath'."
        }
        [void]$runtimeLockPaths.Add($lockPath)
    }
    if ($runtimeLockPaths.Count -eq 0) {
        throw 'No locked runtime NuGet dependency graph was found.'
    }

    foreach ($runtimeBuild in $runtimeBuilds) {
        Copy-RuntimeOutput `
            -SourceDirectory $runtimeBuild.OutputDirectory `
            -DestinationDirectory $applicationStaging
    }

    foreach ($expectedExecutable in @(
            'DEEPAi.ServiceDirectory.Service.exe',
            'DEEPAi.ServiceDirectory.Watchdog.exe',
            'DEEPAi.ServiceDirectory.Tray.exe')) {
        if (-not (Test-Path `
                -LiteralPath (Join-Path $applicationStaging $expectedExecutable) `
                -PathType Leaf)) {
            throw "Required runtime executable '$expectedExecutable' was not staged."
        }
    }

    $stagedPdbs = @(Get-ChildItem -LiteralPath $stagingRoot `
        -Recurse -File -Filter '*.pdb')
    if ($stagedPdbs.Count -ne 0) {
        throw 'Release PDB files must not be included in the installer payload.'
    }

    New-RuntimeDependencyNotices `
        -LockFilePaths $runtimeLockPaths `
        -RootNoticePath $rootNoticePath `
        -NoticesDirectory $noticesStaging `
        -ProductVersion $version.ProductVersion

    [void](Assert-SafeStagingPath `
        -RepositoryRoot $repositoryRoot `
        -Path $stagingRoot `
        -IncludeTree)
    [void](Assert-ExactPackageDirectory `
        -ExpectedPath (Join-Path $repositoryRoot 'installer') `
        -Path $installerRoot `
        -IncludeTree)
    $unmanagedInstallers = @(Get-ChildItem -LiteralPath $installerRoot `
        -File -Filter '*.exe' | Where-Object {
            $_.Name -notlike 'DEEPAi-ServiceDirectory-*-build.*-x64.exe'
        })
    if ($unmanagedInstallers.Count -ne 0) {
        throw "The installer output directory contains an unmanaged EXE '$($unmanagedInstallers[0].FullName)'."
    }
    $forbiddenOutputs = @(Get-ChildItem -LiteralPath $installerRoot -File |
        Where-Object {
            $_.Extension -ieq '.pdb' `
                -or $_.Extension -ieq '.sha256' `
                -or $_.Extension -ieq '.manifest'
        })
    if ($forbiddenOutputs.Count -ne 0) {
        throw 'The installer output directory contains a forbidden PDB, checksum or manifest file.'
    }

    $previousVersion = $env:DPAI_SD_PACKAGE_VERSION
    $previousBuild = $env:DPAI_SD_PACKAGE_BUILD
    $previousPayload = $env:DPAI_SD_PACKAGE_PAYLOAD
    $previousOutput = $env:DPAI_SD_PACKAGE_OUTPUT
    try {
        $env:DPAI_SD_PACKAGE_VERSION = $version.ProductVersion
        $env:DPAI_SD_PACKAGE_BUILD = $version.BuildNumber
        $env:DPAI_SD_PACKAGE_PAYLOAD = $stagingRoot
        $env:DPAI_SD_PACKAGE_OUTPUT = $installerOutputStaging

        $isccVersion = if ($isccVersionInfo.FileMajorPart -gt 0) {
            $isccVersionInfo.FileVersion
        }
        else {
            "major $isccMajorVersion (compiler banner)"
        }
        Write-Host "Using Inno Setup '$isccPath' ($isccVersion)."
        & $isccPath '/Qp' $installerSource
        if ($LASTEXITCODE -ne 0) {
            throw "Inno Setup compilation failed with exit code $LASTEXITCODE."
        }
    }
    finally {
        $env:DPAI_SD_PACKAGE_VERSION = $previousVersion
        $env:DPAI_SD_PACKAGE_BUILD = $previousBuild
        $env:DPAI_SD_PACKAGE_PAYLOAD = $previousPayload
        $env:DPAI_SD_PACKAGE_OUTPUT = $previousOutput
    }

    [void](Assert-ExactPackageDirectory `
        -ExpectedPath $installerOutputStaging `
        -Path $installerOutputStaging `
        -IncludeTree)
    $generatedOutputItems = @(Get-ChildItem `
        -LiteralPath $installerOutputStaging -Force)
    if ($generatedOutputItems.Count -ne 1 `
        -or $generatedOutputItems[0].PSIsContainer `
        -or -not [StringComparer]::OrdinalIgnoreCase.Equals(
            $generatedOutputItems[0].FullName,
            (Join-Path $installerOutputStaging $expectedFileName))) {
        throw "Packaging must stage only '$expectedFileName'."
    }
    Assert-NoPackageReparsePoint -Path $generatedOutputItems[0].FullName
    if ($generatedOutputItems[0].Length -le 0) {
        throw "Staged installer '$expectedFileName' is empty."
    }

    Publish-ValidatedInstaller `
        -StagedInstallerPath $generatedOutputItems[0].FullName `
        -FinalInstallerPath $expectedInstallerPath `
        -InstallerRoot $installerRoot `
        -StagingOutputRoot $installerOutputStaging

    $existingInstallers = @(Get-ChildItem -LiteralPath $installerRoot `
        -File -Filter 'DEEPAi-ServiceDirectory-*-build.*-x64.exe')
    foreach ($existingInstaller in $existingInstallers) {
        if ([StringComparer]::OrdinalIgnoreCase.Equals(
                $existingInstaller.FullName,
                $expectedInstallerPath)) {
            continue
        }
        Assert-NoPackageReparsePoint -Path $existingInstaller.FullName
        Remove-Item -LiteralPath $existingInstaller.FullName -Force
        if (Test-Path -LiteralPath $existingInstaller.FullName) {
            throw "Old installer '$($existingInstaller.FullName)' was not removed."
        }
    }

    [void](Assert-ExactPackageDirectory `
        -ExpectedPath (Join-Path $repositoryRoot 'installer') `
        -Path $installerRoot `
        -IncludeTree)
    $publishedInstallers = @(Get-ChildItem -LiteralPath $installerRoot `
        -File -Filter '*.exe')
    if ($publishedInstallers.Count -ne 1 `
        -or -not [StringComparer]::OrdinalIgnoreCase.Equals(
            $publishedInstallers[0].FullName,
            $expectedInstallerPath)) {
        throw "Packaging must publish only '$expectedInstallerPath'."
    }

    $forbiddenOutputs = @(Get-ChildItem -LiteralPath $installerRoot -File |
        Where-Object {
            $_.Extension -ieq '.pdb' `
                -or $_.Extension -ieq '.sha256' `
                -or $_.Extension -ieq '.manifest'
        })
    if ($forbiddenOutputs.Count -ne 0) {
        throw 'The installer output directory contains a forbidden PDB, checksum or manifest file.'
    }

    Write-Host "Installer created: '$expectedInstallerPath'."
}
finally {
    Remove-ExactPackageDirectoryTree `
        -ExpectedPath (Join-Path $repositoryRoot 'artifacts\service-directory\package') `
        -Path $stagingRoot
}
