function Invoke-CaPasswordCommand {
    param(
        [Parameter(Mandatory = $true)][string]$ExecutablePath,
        [Parameter(Mandatory = $true)][string]$Arguments,
        [Parameter(Mandatory = $true)][string]$Prompt,
        [Parameter(Mandatory = $true)][string]$FailureContext
    )

    $securePassword = Read-Host -Prompt $Prompt -AsSecureString
    $passwordPointer = [IntPtr]::Zero
    $password = $null
    $process = $null
    try {
        $passwordPointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR(
            $securePassword)
        $password = [Runtime.InteropServices.Marshal]::PtrToStringBSTR(
            $passwordPointer)
        $startInfo = [Diagnostics.ProcessStartInfo]::new()
        $startInfo.FileName = $ExecutablePath
        $startInfo.Arguments = $Arguments
        $startInfo.UseShellExecute = $false
        $startInfo.CreateNoWindow = $true
        $startInfo.RedirectStandardInput = $true
        $startInfo.RedirectStandardOutput = $true
        $startInfo.RedirectStandardError = $true
        $process = [Diagnostics.Process]::Start($startInfo)
        $process.StandardInput.WriteLine($password)
        $process.StandardInput.Close()
        $standardOutput = $process.StandardOutput.ReadToEnd()
        $standardError = $process.StandardError.ReadToEnd()
        $process.WaitForExit()
        if ($process.ExitCode -ne 0) {
            throw "$FailureContext failed. $standardError"
        }

        return $standardOutput.Trim()
    }
    finally {
        if ($null -ne $process) {
            $process.Dispose()
        }
        if ($passwordPointer -ne [IntPtr]::Zero) {
            [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($passwordPointer)
        }
        $password = $null
        $securePassword.Dispose()
    }
}

function Invoke-CaRestore {
    param(
        [Parameter(Mandatory = $true)][string]$ExecutablePath,
        [Parameter(Mandatory = $true)][string]$BackupPath
    )

    if ($BackupPath.IndexOf('"') -ge 0) {
        throw 'The CA restore path contains an unsupported quote character.'
    }

    $output = Invoke-CaPasswordCommand `
        -ExecutablePath $ExecutablePath `
        -Arguments ('--repair-pki-restore "' + $BackupPath + '"') `
        -Prompt 'CA backup password' `
        -FailureContext 'CA restore'
    if (-not [StringComparer]::Ordinal.Equals(
            $output,
            'PKI_RESTORED')) {
        throw 'CA restore returned an unexpected result.'
    }
}

function Invoke-CaStandbyRoleChange {
    param(
        [Parameter(Mandatory = $true)][string]$ExecutablePath,
        [Parameter(Mandatory = $true)][string]$BackupPath,
        [Parameter(Mandatory = $true)]
        [ValidateSet('ConfigureStandby', 'PromoteStandby')]
        [string]$Operation
    )

    if ($BackupPath.IndexOf('"') -ge 0) {
        throw 'The CA backup path contains an unsupported quote character.'
    }

    $promotion = [StringComparer]::Ordinal.Equals(
        $Operation,
        'PromoteStandby')
    $argument = if ($promotion) {
        '--repair-pki-standby-promote'
    } else {
        '--repair-pki-standby-configure'
    }
    $expectedPrefix = if ($promotion) {
        'PKI_STANDBY_PROMOTED'
    } else {
        'PKI_STANDBY_CONFIGURED'
    }
    $output = Invoke-CaPasswordCommand `
        -ExecutablePath $ExecutablePath `
        -Arguments ($argument + ' "' + $BackupPath + '"') `
        -Prompt 'CA backup password' `
        -FailureContext $expectedPrefix
    $pattern = '^' + $expectedPrefix +
        ' (?<Thumbprint>[0-9A-F]{40})' +
        ' (?<SerialNumber>[0-9A-F]{32})' +
        ' (?<NotAfterUtc>[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9:.]+Z)' +
        ' (?<FileName>site-ca-[0-9a-f-]{36}-[0-9]{8}T[0-9]{9}Z\.dpca)' +
        ' (?<CreatedUtc>[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9:.]+Z)' +
        ' (?<Sha256>[0-9A-F]{64})$'
    if ($output -cnotmatch $pattern) {
        throw "$expectedPrefix returned an unexpected result."
    }

    $backupPath = Join-Path `
        ([Environment]::GetFolderPath('CommonApplicationData')) `
        ('DEEPAi\ServiceDirectory\backups\ca\' + $Matches['FileName'])
    Write-Host "Encrypted CA backup: $backupPath"
    return [pscustomobject]@{
        Thumbprint = $Matches['Thumbprint']
        SerialNumber = $Matches['SerialNumber']
        NotAfterUtc = $Matches['NotAfterUtc']
    }
}

function Invoke-CaProvision {
    param([Parameter(Mandatory = $true)][string]$ExecutablePath)

    $output = Invoke-CaPasswordCommand `
        -ExecutablePath $ExecutablePath `
        -Arguments '--repair-pki-provision' `
        -Prompt 'Initial encrypted CA backup password' `
        -FailureContext 'Initial CA provisioning'
    if ($output -cnotmatch `
        '^PKI_PROVISIONED (?<FileName>site-ca-[0-9a-f-]{36}-[0-9]{8}T[0-9]{9}Z\.dpca) (?<CreatedUtc>[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9:.]+Z) (?<Sha256>[0-9A-F]{64})$') {
        throw 'Initial CA provisioning returned an unexpected result.'
    }

    $backupPath = Join-Path `
        ([Environment]::GetFolderPath('CommonApplicationData')) `
        ('DEEPAi\ServiceDirectory\backups\ca\' + $Matches['FileName'])
    Write-Host "Initial encrypted CA backup: $backupPath"
}

function Get-CertificateAuthorityStateSnapshots {
    param([Parameter(Mandatory = $true)][string]$DataRoot)

    $relativePaths = @(
        'pki\state.xml',
        'pki\ledger.xml',
        'pki\peer-cache.xml',
        'pki\crl.der',
        'pki\ca.der',
        'secrets\ca.key'
    )
    $snapshots = New-Object 'System.Collections.Generic.List[object]'
    foreach ($relativePath in $relativePaths) {
        $primaryPath = Join-Path $DataRoot $relativePath
        foreach ($path in @($primaryPath, $primaryPath + '.bak')) {
            $snapshot = Get-FileSnapshot -Path $path
            [void]$snapshots.Add([pscustomobject]@{
                    Path = $path
                    Existed = [bool]$snapshot.Existed
                    Bytes = $snapshot.Bytes
                })
        }
    }

    return $snapshots.ToArray()
}

function Restore-CertificateAuthorityStateSnapshots {
    param([Parameter(Mandatory = $true)][object[]]$Snapshots)

    foreach ($snapshot in $Snapshots) {
        Restore-FileSnapshot `
            -Path ([string]$snapshot.Path) `
            -Existed ([bool]$snapshot.Existed) `
            -Bytes $snapshot.Bytes
    }
}
