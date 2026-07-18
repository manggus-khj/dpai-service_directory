[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug',

    [ValidateSet('Latest', '2019', '2022')]
    [string]$VisualStudioVersion = 'Latest',

    [string]$MSBuildPath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$solutionPath = Join-Path $repositoryRoot 'DEEPAi.ServiceDirectory.sln'
if ([string]::IsNullOrWhiteSpace($MSBuildPath)) {
    $vswherePath = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (-not (Test-Path -LiteralPath $vswherePath -PathType Leaf)) {
        throw 'vswhere.exe was not found. Install Visual Studio Build Tools with the .NET Framework 4.8 targeting pack.'
    }

    $vswhereArguments = @(
        '-latest'
        '-products'
        '*'
        '-requires'
        'Microsoft.Component.MSBuild'
        'Microsoft.Net.Component.4.8.TargetingPack'
        '-find'
        'MSBuild\**\Bin\MSBuild.exe'
    )
    if ($VisualStudioVersion -eq '2019') {
        $vswhereArguments += @('-version', '[16.0,17.0)')
    }
    elseif ($VisualStudioVersion -eq '2022') {
        $vswhereArguments += @('-version', '[17.0,18.0)')
    }

    $MSBuildPath = & $vswherePath @vswhereArguments | Select-Object -First 1
}

if ([string]::IsNullOrWhiteSpace($MSBuildPath) -or -not (Test-Path -LiteralPath $MSBuildPath -PathType Leaf)) {
    throw 'MSBuild.exe was not found. Select an installed toolset or pass -MSBuildPath.'
}

$msbuildFileVersion = [System.Diagnostics.FileVersionInfo]::GetVersionInfo(
    $MSBuildPath).FileVersion
Write-Host "Using MSBuild '$MSBuildPath' (file version $msbuildFileVersion)."

& $MSBuildPath $solutionPath /nologo /m /t:Build "/p:Configuration=$Configuration" '/p:Platform=x64'
if ($LASTEXITCODE -ne 0) {
    throw "MSBuild failed with exit code $LASTEXITCODE."
}
