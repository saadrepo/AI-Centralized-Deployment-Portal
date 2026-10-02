param([string]$SiteName, [bool]$PreviewOnly = $true, [string]$ExpectedState = '')
$ErrorActionPreference = 'Stop'
$manager = $null
$mutex = $null
$locked = $false
try {
    if ($SiteName -notmatch '^[a-zA-Z0-9][a-zA-Z0-9 _.-]{0,79}$') { throw 'Invalid IIS site name.' }
    $mutex = New-Object Threading.Mutex($false, 'Global\DeploymentPortal-IIS')
    try { $locked = $mutex.WaitOne(0) } catch [Threading.AbandonedMutexException] { $locked = $true }
    if (!$locked) { throw 'Another deployment or site operation is active. Try again after it finishes.' }
    Add-Type -Path "$env:windir\System32\inetsrv\Microsoft.Web.Administration.dll"
    $manager = New-Object Microsoft.Web.Administration.ServerManager
    $site = $manager.Sites[$SiteName]
    if (!$site) { throw 'The IIS site no longer exists. Nothing was deleted.' }
    $configuration = [ordered]@{
        Id = $site.Id; Name = $site.Name
        Bindings = @($site.Bindings | ForEach-Object { "$($_.Protocol)|$($_.BindingInformation)|$($_.SslFlags)" })
        Applications = @($site.Applications | ForEach-Object {
            [ordered]@{ Path=$_.Path; Pool=$_.ApplicationPoolName; Protocols=$_.EnabledProtocols
                Directories=@($_.VirtualDirectories | ForEach-Object { [ordered]@{ Path=$_.Path; PhysicalPath=$_.PhysicalPath; UserName=$_.UserName } }) }
        })
    } | ConvertTo-Json -Depth 8 -Compress
    $hash = [Security.Cryptography.SHA256]::Create()
    try { $fingerprint = [BitConverter]::ToString($hash.ComputeHash([Text.Encoding]::UTF8.GetBytes($configuration))).Replace('-', '').ToLowerInvariant() } finally { $hash.Dispose() }
    if ($PreviewOnly) {
        $apps = ($site.Applications | ForEach-Object { $_.Path }) -join ', '
        [pscustomobject]@{ Success=$true; Message="Site '$SiteName' contains these application paths: $apps. All these mappings will be removed."; StateFingerprint=$fingerprint }
    } else {
        if (!$ExpectedState -or $ExpectedState -cne $fingerprint) { throw 'The IIS site changed after review. Review it again before deleting.' }
        $manager.Sites.Remove($site)
        $manager.CommitChanges()
        [pscustomobject]@{ Success=$true; Message="IIS site '$SiteName' deleted. Application files, pools, backups and deployment history were retained." }
    }
} catch {
    $message = if ($_.Exception.Message -match '^(Invalid IIS site name\.|Another deployment or site operation|The IIS site no longer exists\.|The IIS site changed after review\.)') { $_.Exception.Message } else { 'IIS site operation failed. Check IIS permissions and verify whether the site still exists before retrying.' }
    [pscustomobject]@{ Success=$false; Message=$message }
} finally {
    if ($manager) { $manager.Dispose() }
    if ($locked) { $mutex.ReleaseMutex() }
    if ($mutex) { $mutex.Dispose() }
}
