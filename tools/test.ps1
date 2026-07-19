[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug',

    [ValidateSet('Latest', '2019', '2022')]
    [string]$VisualStudioVersion = 'Latest',

    [string]$MSBuildPath,

    [string]$VSTestPath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Read-Utf8XmlDocument {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    $text = [System.IO.File]::ReadAllText(
        $Path,
        [System.Text.Encoding]::UTF8)
    $settings = New-Object System.Xml.XmlReaderSettings
    $settings.DtdProcessing = [System.Xml.DtdProcessing]::Prohibit
    $settings.XmlResolver = $null
    $settings.MaxCharactersInDocument = 16MB
    $stringReader = [System.IO.StringReader]::new($text)
    $xmlReader = [System.Xml.XmlReader]::Create($stringReader, $settings)
    try {
        $document = New-Object System.Xml.XmlDocument
        $document.XmlResolver = $null
        $document.Load($xmlReader)
        return ,$document
    }
    finally {
        $xmlReader.Dispose()
        $stringReader.Dispose()
    }
}

function Add-VisualStudioVersionRange {
    param(
        [Parameter(Mandatory = $true)]
        [System.Collections.Generic.List[string]]$Arguments,

        [Parameter(Mandatory = $true)]
        [string]$RequestedVersion
    )

    if ($RequestedVersion -eq '2019') {
        [void]$Arguments.Add('-version')
        [void]$Arguments.Add('[16.0,17.0)')
    }
    elseif ($RequestedVersion -eq '2022') {
        [void]$Arguments.Add('-version')
        [void]$Arguments.Add('[17.0,18.0)')
    }
}

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$solutionPath = Join-Path $repositoryRoot 'DEEPAi.ServiceDirectory.sln'
$testProjectPath = Join-Path $repositoryRoot `
    'tests\DEEPAi.ServiceDirectory.Tests\DEEPAi.ServiceDirectory.Tests.csproj'
$testOutputDirectory = Join-Path $repositoryRoot `
    "artifacts\service-directory\bin\DEEPAi.ServiceDirectory.Tests\x64\$Configuration"
$testAssemblyPath = Join-Path $testOutputDirectory `
    'DEEPAi.ServiceDirectory.Tests.dll'

if (-not (Test-Path -LiteralPath $solutionPath -PathType Leaf)) {
    throw "Solution was not found at '$solutionPath'."
}

if (-not (Test-Path -LiteralPath $testProjectPath -PathType Leaf)) {
    throw "The registered test project was not found at '$testProjectPath'."
}

$projectRoots = @(
    (Join-Path $repositoryRoot 'src'),
    (Join-Path $repositoryRoot 'tests')
)
foreach ($projectRoot in $projectRoots) {
    if (-not (Test-Path -LiteralPath $projectRoot -PathType Container)) {
        continue
    }

    $projectFiles = Get-ChildItem `
        -LiteralPath $projectRoot `
        -Recurse `
        -File `
        -Filter '*.csproj' |
        Where-Object {
            $_.FullName -notmatch '[\\/](bin|obj)[\\/]'
        }
    foreach ($projectFile in $projectFiles) {
        [System.Xml.XmlDocument]$projectDocument =
            Read-Utf8XmlDocument -Path $projectFile.FullName
        $namespaceManager = [System.Xml.XmlNamespaceManager]::new(
            $projectDocument.NameTable)
        $namespaceManager.AddNamespace(
            'msb',
            'http://schemas.microsoft.com/developer/msbuild/2003')
        $packageReferences = $projectDocument.SelectNodes(
            '//msb:PackageReference',
            $namespaceManager)
        if ($packageReferences.Count -eq 0) {
            continue
        }

        $lockFilePath = Join-Path $projectFile.DirectoryName 'packages.lock.json'
        if (-not (Test-Path -LiteralPath $lockFilePath -PathType Leaf)) {
            throw "Package lock file is required for '$($projectFile.FullName)'. Run an explicitly approved initial restore, review the generated '$lockFilePath', and commit it before running tests."
        }
    }
}

$vswherePath = Join-Path ${env:ProgramFiles(x86)} `
    'Microsoft Visual Studio\Installer\vswhere.exe'
if (([string]::IsNullOrWhiteSpace($MSBuildPath) `
        -or [string]::IsNullOrWhiteSpace($VSTestPath)) `
    -and -not (Test-Path -LiteralPath $vswherePath -PathType Leaf)) {
    throw 'vswhere.exe was not found. Install Visual Studio Build Tools with the .NET desktop build tools and test tools components.'
}

if ([string]::IsNullOrWhiteSpace($MSBuildPath)) {
    $msbuildArguments = New-Object 'System.Collections.Generic.List[string]'
    [void]$msbuildArguments.Add('-latest')
    [void]$msbuildArguments.Add('-products')
    [void]$msbuildArguments.Add('*')
    [void]$msbuildArguments.Add('-requires')
    [void]$msbuildArguments.Add('Microsoft.Component.MSBuild')
    [void]$msbuildArguments.Add('Microsoft.Net.Component.4.8.SDK')
    [void]$msbuildArguments.Add('Microsoft.Net.Component.4.8.TargetingPack')
    [void]$msbuildArguments.Add('-find')
    [void]$msbuildArguments.Add('MSBuild\**\Bin\MSBuild.exe')
    Add-VisualStudioVersionRange `
        -Arguments $msbuildArguments `
        -RequestedVersion $VisualStudioVersion
    $MSBuildPath = & $vswherePath @msbuildArguments | Select-Object -First 1
}

if ([string]::IsNullOrWhiteSpace($MSBuildPath) `
    -or -not (Test-Path -LiteralPath $MSBuildPath -PathType Leaf)) {
    throw 'MSBuild.exe with the .NET Framework 4.8 SDK and Targeting Pack was not found. Install the .NET desktop build tools workload, select an installed toolset, or pass -MSBuildPath.'
}

$msbuildDirectory = Split-Path -Parent $MSBuildPath
$winFxTargetsPath = Join-Path $msbuildDirectory 'Microsoft.WinFX.targets'
if (-not (Test-Path -LiteralPath $winFxTargetsPath -PathType Leaf)) {
    throw "WPF build targets were not found at '$winFxTargetsPath'. Install the .NET desktop build tools workload."
}

if ([string]::IsNullOrWhiteSpace($VSTestPath)) {
    $vstestArguments = New-Object 'System.Collections.Generic.List[string]'
    [void]$vstestArguments.Add('-latest')
    [void]$vstestArguments.Add('-products')
    [void]$vstestArguments.Add('*')
    [void]$vstestArguments.Add('-requires')
    [void]$vstestArguments.Add(
        'Microsoft.VisualStudio.Component.TestTools.BuildTools')
    [void]$vstestArguments.Add('-find')
    [void]$vstestArguments.Add(
        'Common7\IDE\Extensions\TestPlatform\vstest.console.exe')
    Add-VisualStudioVersionRange `
        -Arguments $vstestArguments `
        -RequestedVersion $VisualStudioVersion
    $VSTestPath = & $vswherePath @vstestArguments | Select-Object -First 1

    if ([string]::IsNullOrWhiteSpace($VSTestPath)) {
        $fallbackArguments = New-Object 'System.Collections.Generic.List[string]'
        [void]$fallbackArguments.Add('-latest')
        [void]$fallbackArguments.Add('-products')
        [void]$fallbackArguments.Add('*')
        [void]$fallbackArguments.Add('-requires')
        [void]$fallbackArguments.Add(
            'Microsoft.VisualStudio.Component.TestTools.BuildTools')
        [void]$fallbackArguments.Add('-find')
        [void]$fallbackArguments.Add(
            'Common7\IDE\CommonExtensions\Microsoft\TestWindow\vstest.console.exe')
        Add-VisualStudioVersionRange `
            -Arguments $fallbackArguments `
            -RequestedVersion $VisualStudioVersion
        $VSTestPath = & $vswherePath @fallbackArguments |
            Select-Object -First 1
    }
}

if ([string]::IsNullOrWhiteSpace($VSTestPath) `
    -or -not (Test-Path -LiteralPath $VSTestPath -PathType Leaf)) {
    throw 'vstest.console.exe was not found. Install the Visual Studio test tools component, select an installed toolset, or pass -VSTestPath.'
}

$msbuildFileVersion = [System.Diagnostics.FileVersionInfo]::GetVersionInfo(
    $MSBuildPath).FileVersion
Write-Host "Using MSBuild '$MSBuildPath' (file version $msbuildFileVersion)."

$buildArguments = @(
    $solutionPath,
    '/nologo',
    '/m',
    '/restore',
    '/t:Build',
    "/p:Configuration=$Configuration",
    '/p:Platform=x64',
    '/p:RestoreLockedMode=true'
)
& $MSBuildPath @buildArguments
if ($LASTEXITCODE -ne 0) {
    throw "Locked restore or MSBuild failed with exit code $LASTEXITCODE."
}

if (-not (Test-Path -LiteralPath $testAssemblyPath -PathType Leaf)) {
    throw "The registered test assembly was not produced at '$testAssemblyPath'."
}

$runId = [DateTimeOffset]::Now.ToString('yyyyMMdd-HHmmss-fff') `
    + '-' `
    + [Guid]::NewGuid().ToString('N')
$resultsDirectory = Join-Path $repositoryRoot `
    "artifacts\service-directory\test-results\$Configuration\$runId"
[void](New-Item -ItemType Directory -Path $resultsDirectory -Force)
$trxFileName = 'DEEPAi.ServiceDirectory.Tests.trx'
$trxPath = Join-Path $resultsDirectory $trxFileName

$vstestFileVersion = [System.Diagnostics.FileVersionInfo]::GetVersionInfo(
    $VSTestPath).FileVersion
Write-Host "Using VSTest '$VSTestPath' (file version $vstestFileVersion)."

$testArguments = @(
    $testAssemblyPath,
    '/Platform:x64',
    "/TestAdapterPath:$testOutputDirectory",
    "/ResultsDirectory:$resultsDirectory",
    "/Logger:trx;LogFileName=$trxFileName"
)
& $VSTestPath @testArguments
if ($LASTEXITCODE -ne 0) {
    throw "VSTest failed with exit code $LASTEXITCODE."
}

if (-not (Test-Path -LiteralPath $trxPath -PathType Leaf)) {
    throw "VSTest did not produce the expected TRX result at '$trxPath'."
}

[System.Xml.XmlDocument]$trxDocument = Read-Utf8XmlDocument -Path $trxPath
$counters = $trxDocument.SelectSingleNode(
    "/*[local-name()='TestRun']/*[local-name()='ResultSummary']/*[local-name()='Counters']")
if ($null -eq $counters) {
    throw "TRX result '$trxPath' does not contain test counters."
}

[long]$totalTests = 0
$totalText = $counters.GetAttribute('total')
$parsedTotal = [long]::TryParse(
    $totalText,
    [System.Globalization.NumberStyles]::None,
    [System.Globalization.CultureInfo]::InvariantCulture,
    [ref]$totalTests)
if (-not $parsedTotal -or $totalTests -le 0) {
    throw "No tests were discovered or recorded in '$trxPath'."
}

Write-Host "$totalTests tests completed successfully. TRX: '$trxPath'."
