param($Request)
$ErrorActionPreference = 'Stop'
Assert-CdPrivateRoot $Request.RemoteRoot
$parent = Join-Path $Request.RemoteRoot ([guid]$Request.PreparationId).ToString('N')
$publish = Join-Path $parent 'publish'
if ($publish -cne $Request.SourcePath -or !$publish.StartsWith($Request.RemoteRoot.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'CD staging escaped its root.' }
Assert-CdTree $publish
Assert-CdTree $Request.TargetPath
Add-Type -Path "$env:windir\System32\inetsrv\Microsoft.Web.Administration.dll"
$manager = New-Object Microsoft.Web.Administration.ServerManager
try {
    $site = $manager.Sites[$Request.SiteName]
    $applicationPath = if ($Request.ApplicationName -eq '/') { '/' } else { '/' + $Request.ApplicationName }
    $app = if ($site) { $site.Applications[$applicationPath] } else { $null }
    if (!$app -or !$app.VirtualDirectories['/'].PhysicalPath.Equals($Request.TargetPath, [StringComparison]::OrdinalIgnoreCase)) { throw 'CD supports existing applications at their registered physical path only.' }
    if (!$manager.ApplicationPools[$app.ApplicationPoolName]) { throw 'CD requires the existing application pool; pool creation is not supported.' }
    $references = @($manager.Sites | ForEach-Object { $_.Applications } | Where-Object ApplicationPoolName -EQ $app.ApplicationPoolName)
    if ($references.Count -ne 1) { throw 'CD requires a dedicated application pool.' }
    if ($Request.AppPoolName -and $Request.AppPoolName -ne $app.ApplicationPoolName) { throw 'Registered application pool differs.' }
} finally { $manager.Dispose() }
$actual = Get-CdManifestJson $publish | ConvertFrom-Json
$expected = @($Request.Files)
if (@($actual).Count -ne $expected.Count) { throw 'CD received an unexpected file set.' }
foreach ($file in $expected) {
    $relative = $file.Path.Replace('/', '\')
    Assert-CdRelativePath $relative
    $found = @($actual | Where-Object Path -CEQ $relative)
    if ($found.Count -ne 1 -or $found[0].Size -ne $file.Size -or $found[0].Sha256 -cne $file.Sha256) { throw 'CD transfer integrity validation failed.' }
}
$configuration = Preserve-CdTargetConfiguration $publish $Request.TargetPath $Request.PreservedConfigurationFiles
return [pscustomobject]@{ Success=$true; Message='Private CD package verified; existing target configuration preserved.'; DeploymentMode='Update';
    EffectiveManifestJson=(Get-CdManifestJson $publish); ConfigurationManifestJson=$configuration; ConfigurationDigest=(Get-CdHash $configuration) }
