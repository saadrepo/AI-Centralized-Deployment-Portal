$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$session = $null
$exitCode = 1
$stage = 'request validation'
try {
    $request = [Console]::In.ReadLine() | ConvertFrom-Json
    if ($request.Operation -notin @('TestConnection', 'GetBasicServerInfo', 'PreviewDeployment', 'Deploy', 'StageCd', 'PreviewDeleteSite', 'DeleteSite', 'ListSites')) { throw 'Unsupported operation.' }
    $stage = 'credential preparation'
    $secure = ConvertTo-SecureString -String $request.Password -AsPlainText -Force
    $credential = New-Object System.Management.Automation.PSCredential($request.Username, $secure)
    $request.Password = $null
    $sessionOptions = New-PSSessionOption -OpenTimeout ($request.Options.ConnectionTimeoutSeconds * 1000) -OperationTimeout ($request.Options.DeploymentTimeoutSeconds * 1000)
    $parameters = @{
        ComputerName = $request.HostNameOrIp; Port = [int]$request.WinRmPort
        Credential = $credential; Authentication = 'Negotiate'; SessionOption = $sessionOptions
    }
    if ($request.UseSsl) { $parameters.UseSSL = $true }
    $stage = 'WinRM session authentication'
    $session = New-PSSession @parameters
    if ($request.Operation -in @('PreviewDeployment', 'Deploy')) {
        $stage = if ($request.Operation -eq 'PreviewDeployment') { 'deployment preview' } else { 'remote deployment' }
        $d = $request.Deployment
        $arguments = @($d.SiteName, $d.ApplicationName, $d.AppPoolName, $d.SourcePath, $d.TargetPath, $d.BackupPath,
            $request.Options.BackupRoot, $true, ($request.Operation -eq 'PreviewDeployment'), $d.ExpectedState, $d.SitePort, $d.SiteHostName,
            $d.CdPackage, $(if ($d.CdPackage) { [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'CdPackageSecurity.ps1')) } else { '' }))
        $result = Invoke-Command -Session $session -FilePath (Join-Path $PSScriptRoot 'Deploy-IISApplication.ps1') -ArgumentList $arguments
    } elseif ($request.Operation -eq 'StageCd') {
        $stage = 'private CD staging'
        $cd = $request.CdStage
        $helpers = [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'CdPackageSecurity.ps1'))
        $initialize = [scriptblock]::Create('param($Cd)' + "`n" + $helpers + @'

Assert-CdPrivateRoot $Cd.RemoteRoot
if (@(Get-ChildItem -LiteralPath $Cd.RemoteRoot -Directory -Force).Count -ge 10) { throw 'Remote preparation capacity reached; operator retention review required.' }
$expected = Join-Path (Join-Path $Cd.RemoteRoot ([guid]$Cd.PreparationId).ToString('N')) 'publish'
if ($expected -cne $Cd.SourcePath -or (Test-Path -LiteralPath (Split-Path $expected -Parent))) { throw 'Invalid or reused preparation.' }
if (@($Cd.Files).Count -lt 1 -or @($Cd.Files).Count -gt 1000 -or ($Cd.Files | Measure-Object Size -Sum).Sum -gt 268435456) { throw 'Package limit exceeded.' }
New-Item -ItemType Directory -Path $expected | Out-Null
foreach ($file in $Cd.Files) {
    $relative = $file.Path.Replace('/', '\')
    Assert-CdRelativePath $relative
    if ($file.Size -lt 0 -or $file.Size -gt 67108864 -or $file.Sha256 -notmatch '^[a-fA-F0-9]{64}$') { throw 'Invalid file manifest.' }
    $parent = Split-Path (Join-Path $expected $relative) -Parent
    if (!(Test-Path -LiteralPath $parent)) { New-Item -ItemType Directory -Path $parent -Force | Out-Null }
    Assert-CdNoLinks $parent
}
'@)
        Invoke-Command -Session $session -ScriptBlock $initialize -ArgumentList $cd | Out-Null
        . (Join-Path $PSScriptRoot 'CdPackageSecurity.ps1')
        foreach ($file in $cd.Files) {
            Assert-CdNoLinks $file.LocalPath
            $item = Get-Item -LiteralPath $file.LocalPath
            if ($item.PSIsContainer -or $item.Length -ne $file.Size -or (Get-FileHash -LiteralPath $file.LocalPath -Algorithm SHA256).Hash -ine $file.Sha256) { throw 'Local package integrity changed.' }
            Copy-Item -LiteralPath $file.LocalPath -Destination (Join-Path $cd.SourcePath $file.Path.Replace('/', '\')) -ToSession $session
        }
        $body = [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'Prepare-CdPackage.ps1'))
        $body = $body -replace '^param\(\$Request\)\s*', ''
        $prepare = [scriptblock]::Create('param($Request)' + "`n" + $helpers + "`n" + $body)
        $result = Invoke-Command -Session $session -ScriptBlock $prepare -ArgumentList $cd
    } elseif ($request.Operation -eq 'ListSites') {
        $stage = 'IIS site inventory'
        $result = Invoke-Command -Session $session -ScriptBlock {
            Add-Type -Path "$env:windir\System32\inetsrv\Microsoft.Web.Administration.dll"
            $manager = New-Object Microsoft.Web.Administration.ServerManager
            try {
                $sites = @($manager.Sites | ForEach-Object {
                    [pscustomobject]@{ Name=$_.Name; State=[string]$_.State; Bindings=(($_.Bindings | ForEach-Object { "$($_.Protocol) $($_.BindingInformation)" }) -join ', ') }
                })
                [pscustomobject]@{ Success=$true; Message='Live IIS sites loaded.'; Sites=$sites }
            } finally { $manager.Dispose() }
        }
    } elseif ($request.Operation -in @('PreviewDeleteSite', 'DeleteSite')) {
        $stage = 'IIS site deletion'
        $result = Invoke-Command -Session $session -FilePath (Join-Path $PSScriptRoot 'Remove-IISSite.ps1') -ArgumentList @($request.Deployment.SiteName, ($request.Operation -eq 'PreviewDeleteSite'), $request.Deployment.ExpectedState)
    } else {
        $stage = 'remote server information'
        $result = Invoke-Command -Session $session -ScriptBlock {
            $os = (Get-CimInstance Win32_OperatingSystem -ErrorAction Stop).Caption
            $memory = (Get-CimInstance Win32_ComputerSystem -ErrorAction Stop).TotalPhysicalMemory
            [pscustomobject]@{ Success = $true; Message = 'WinRM connection successful.'; ServerName = $env:COMPUTERNAME; OperatingSystem = $os; TotalMemoryBytes = [long]$memory }
        }
    }
    # Only the fixed result contract crosses the process boundary.
    [pscustomobject]@{ Success = [bool]$result.Success; Message = [string]$result.Message; ServerName = [string]$result.ServerName;
        OperatingSystem = [string]$result.OperatingSystem; BackupPath = [string]$result.BackupPath; TotalMemoryBytes = $result.TotalMemoryBytes; StateFingerprint = $result.StateFingerprint; DeploymentMode = $result.DeploymentMode; Sites = @($result.Sites | Where-Object { $null -ne $_ });
        EffectiveManifestJson = $result.EffectiveManifestJson; ConfigurationManifestJson = $result.ConfigurationManifestJson; ConfigurationDigest = $result.ConfigurationDigest; Verified = [bool]$result.Verified; Verification = $result.Verification } | ConvertTo-Json -Compress -Depth 8
    if ($result.Success) { $exitCode = 0 }
} catch {
    # Expose only a fixed stage and numeric error code; never raw exception text or request values.
    $failure = $_
    $code = $failure.Exception.HResult
    if ($failure.Exception.PSObject.Properties['ErrorCode']) { $code = $failure.Exception.ErrorCode }
    $message = "Remote operation failed during $stage (code $code)."
    if ($stage -eq 'WinRM session authentication') {
        if ($code -eq 5) { $message += ' Access was denied by WinRM.' }
        $message += ' Verify the saved username/password match the successful manual test and that the portal Windows account can use the configured WinRM authentication.'
    } elseif ($stage -eq 'remote server information') {
        $message += ' WinRM session opened, but reading operating system information failed. Check remote command and CIM permissions.'
    } elseif ($stage -eq 'deployment preview') {
        $message += ' Could not inspect the deployment plan. No deployment was started.'
    } elseif ($stage -eq 'remote deployment') {
        $message += ' Inspect the target and retained backup before retrying; remote deployment state is unconfirmed.'
    } else {
        $message += ' Check the dashboard automation and credential configuration.'
    }
    [pscustomobject]@{ Success = $false; Message = $message; ServerName = ''; OperatingSystem = '' } | ConvertTo-Json -Compress
} finally {
    if ($null -ne $session) { Remove-PSSession -Session $session -ErrorAction SilentlyContinue }
    $credential = $null
    if ($null -ne $secure) { $secure.Dispose() }
}
exit $exitCode
