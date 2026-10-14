# Fixed package security helpers shared by remote preparation and the existing IIS engine.
# Dot-sourcing this file performs no deployment or IIS changes.
function Assert-CdRelativePath([string]$Path) {
    if (!$Path -or $Path.Length -gt 500 -or $Path -match '[/:*?"<>|%\x00-\x1F]' -or $Path.StartsWith('\')) { throw 'Unsafe CD relative path.' }
    foreach ($part in $Path.Split('\')) {
        if (!$part -or $part -in @('.', '..') -or $part.EndsWith('.') -or $part.EndsWith(' ') -or $part -match '^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\.|$)') { throw 'Unsafe CD filename.' }
    }
}
function Assert-CdNoLinks([string]$Path) {
    $cursor = $Path
    while ($cursor) {
        if ((Test-Path -LiteralPath $cursor) -and ((Get-Item -LiteralPath $cursor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'CD links and junctions are forbidden.' }
        $cursor = Split-Path -Parent $cursor
    }
}
function Assert-CdTree([string]$Root) {
    Assert-CdNoLinks $Root
    foreach ($entry in Get-ChildItem -LiteralPath $Root -Force) {
        Assert-CdNoLinks $entry.FullName
        if ($entry.PSIsContainer) { Assert-CdTree $entry.FullName }
    }
}
function Assert-CdPrivateRoot([string]$Root) {
    if ($Root -notmatch '^[A-Za-z]:\\' -or !(Test-Path -LiteralPath $Root -PathType Container) -or $Root.TrimEnd('\').Length -le 3) { throw 'Provision a private local CD staging root first.' }
    Assert-CdNoLinks $Root
    $acl = Get-Acl -LiteralPath $Root
    if (!$acl.AreAccessRulesProtected) { throw 'CD staging requires protected ACL inheritance.' }
    $runtime = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    $allowed = @($runtime, 'S-1-5-18', 'S-1-5-32-544')
    $runtimeModify = $false
    foreach ($rule in $acl.Access) {
        $sid = $rule.IdentityReference.Translate([Security.Principal.SecurityIdentifier]).Value
        if ($rule.AccessControlType -eq 'Allow' -and $sid -notin $allowed) { throw 'CD staging permits only the remoting account, SYSTEM and administrators.' }
        if ($rule.AccessControlType -eq 'Allow' -and $sid -eq $runtime -and (($rule.FileSystemRights -band [Security.AccessControl.FileSystemRights]::Modify) -eq [Security.AccessControl.FileSystemRights]::Modify) -and (($rule.InheritanceFlags -band 3) -eq 3) -and (($rule.PropagationFlags -band 2) -eq 0)) { $runtimeModify = $true }
    }
    if (!$runtimeModify) { throw 'CD remoting account needs inheritable Modify access.' }
}
function Get-CdHash([string]$Text) {
    $sha = [Security.Cryptography.SHA256]::Create()
    try { return ([BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($Text)))).Replace('-', '') } finally { $sha.Dispose() }
}
function Get-CdManifestJson([string]$Root) {
    Assert-CdTree $Root
    $files = @(Get-ChildItem -LiteralPath $Root -Recurse -File -Force | Sort-Object FullName)
    if ($files.Count -gt 2000) { throw 'CD file count exceeds policy.' }
    $manifest = @($files | ForEach-Object {
        $relative = $_.FullName.Substring($Root.TrimEnd('\').Length + 1)
        Assert-CdRelativePath $relative
        [pscustomobject][ordered]@{ Path=$relative; Size=$_.Length; Sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
    })
    return ConvertTo-Json -InputObject $manifest -Compress -Depth 4
}
function Get-CdConfigurationJson([string]$Target, $Additional = @()) {
    Assert-CdTree $Target
    foreach ($required in @('appsettings.json', 'web.config')) {
        if (!(Test-Path -LiteralPath (Join-Path $Target $required) -PathType Leaf)) { throw 'CD requires existing target appsettings.json and web.config.' }
    }
    foreach ($path in @($Additional)) {
        Assert-CdRelativePath $path
        if (!(Test-Path -LiteralPath (Join-Path $Target $path) -PathType Leaf)) { throw 'Registered configuration file is missing.' }
    }
    $files = @(Get-ChildItem -LiteralPath $Target -Recurse -File -Force | Where-Object {
        $relative = $_.FullName.Substring($Target.TrimEnd('\').Length + 1)
        ($_.Extension -ieq '.json' -and $_.Name -notlike '*.deps.json' -and $_.Name -notlike '*.runtimeconfig.json') -or $_.Extension -in @('.config', '.xml', '.yaml', '.yml', '.ini', '.toml', '.settings', '.pfx', '.pem', '.key', '.p12') -or $_.Name -like '.env*' -or $relative -in @($Additional)
    } | Sort-Object FullName)
    $manifest = @($files | ForEach-Object { $relative = $_.FullName.Substring($Target.TrimEnd('\').Length + 1); Assert-CdRelativePath $relative; [pscustomobject][ordered]@{ Path=$relative; Size=$_.Length; Sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash } })
    if ($files.Count -gt 1000 -or @($files | Where-Object Length -GT 67108864).Count -gt 0 -or ($files | Measure-Object Length -Sum).Sum -gt 268435456) { throw 'Preserved configuration exceeds CD size limits.' }
    return ConvertTo-Json -InputObject $manifest -Compress -Depth 4
}
function Read-CdWebConfig([string]$Path) {
    if ((Get-Item -LiteralPath $Path).Length -gt 2097152) { throw 'CD web.config exceeds inspection policy.' }
    $settings = New-Object Xml.XmlReaderSettings
    $settings.DtdProcessing = [Xml.DtdProcessing]::Prohibit
    $settings.XmlResolver = $null
    $reader = [Xml.XmlReader]::Create($Path, $settings)
    try { $document = New-Object Xml.XmlDocument; $document.XmlResolver=$null; $document.Load($reader); return ,$document } finally { $reader.Dispose() }
}
function Assert-CdWebCompatibility([string]$Package, [string]$Target) {
    $new = Read-CdWebConfig (Join-Path $Package 'web.config')
    $old = Read-CdWebConfig (Join-Path $Target 'web.config')
    $newCore = @($new.SelectNodes('//system.webServer/aspNetCore'))
    $oldCore = @($old.SelectNodes('//system.webServer/aspNetCore'))
    if ($newCore.Count -ne 1 -or $oldCore.Count -ne 1) { throw 'CD requires one ASP.NET Core configuration.' }
    foreach ($attribute in @('processPath', 'arguments', 'hostingModel')) {
        if ($newCore[0].GetAttribute($attribute) -cne $oldCore[0].GetAttribute($attribute)) { throw 'Preserved web.config is incompatible with the package. No automatic merge is allowed.' }
    }
    if ($new.SelectSingleNode('//system.webServer/handlers').OuterXml -cne $old.SelectSingleNode('//system.webServer/handlers').OuterXml) { throw 'ASP.NET Core handlers differ. Review configuration outside CD.' }
    $process = $oldCore[0].GetAttribute('processPath')
    if ($process -ieq 'dotnet') {
        $argument = $oldCore[0].GetAttribute('arguments')
        if ($argument -notmatch '^\.\\[a-zA-Z0-9_.-]+\.dll$') { throw 'CD supports only a package-local dotnet DLL argument.' }
        if (!(Test-Path -LiteralPath (Join-Path $Package $argument.Substring(2)) -PathType Leaf)) { throw 'The preserved web.config DLL is missing from the package.' }
    } elseif ($process -match '^\.\\[a-zA-Z0-9_.-]+\.exe$') {
        if ($oldCore[0].GetAttribute('arguments')) { throw 'CD does not support custom executable arguments.' }
        if (!(Test-Path -LiteralPath (Join-Path $Package $process.Substring(2)) -PathType Leaf)) { throw 'The preserved web.config executable is missing from the package.' }
    } else { throw 'Unsupported ASP.NET Core process path for controlled CD.' }
    $entry = if ($process -ieq 'dotnet') { $argument.Substring(2) } else { $process.Substring(2) }
    $runtimeName = [IO.Path]::GetFileNameWithoutExtension($entry) + '.runtimeconfig.json'
    $runtimeOptions = @()
    foreach ($root in @($Package, $Target)) {
        $runtimePath = Join-Path $root $runtimeName
        if (!(Test-Path -LiteralPath $runtimePath -PathType Leaf) -or (Get-Item -LiteralPath $runtimePath).Length -gt 2097152) { throw 'CD requires bounded package and target runtime configuration.' }
        $runtime = [IO.File]::ReadAllText($runtimePath) | ConvertFrom-Json
        if (!$runtime.runtimeOptions) { throw 'Missing ASP.NET Core runtime options.' }
        $runtimeOptions += ($runtime.runtimeOptions | ConvertTo-Json -Compress -Depth 20)
    }
    if ($runtimeOptions[0] -cne $runtimeOptions[1]) { throw 'Runtime/framework options differ; runtime upgrades or configuration changes require operator review outside CD.' }
}
function Assert-CdApprovedPackage($Cd, [string]$Source, [string]$Target) {
    if ($Cd.Policy -cne 'PreserveExistingRequired.v1' -or $Source -cne $Cd.SourcePath -or $Target -cne $Cd.Provenance.TargetPath) { throw 'CD source or preservation policy changed.' }
    Assert-CdPrivateRoot $Cd.RemoteRoot
    $expected = Join-Path (Join-Path $Cd.RemoteRoot ([guid]$Cd.PreparationId).ToString('N')) 'publish'
    if ($Source -cne $expected) { throw 'CD source escaped its approved preparation.' }
    if ((Get-CdManifestJson $Source) -cne $Cd.EffectiveManifestJson) { throw 'CD package changed after approval.' }
    Assert-CdWebCompatibility $Source $Target
    $approvedFiles = ($Cd.EffectiveManifestJson | ConvertFrom-Json).Path
    foreach ($file in (Get-CdManifestJson $Target | ConvertFrom-Json)) {
        if ($file.Path -notin $approvedFiles) { throw 'Unexpected target-only files appeared after preparation.' }
    }
    $configuration = Get-CdConfigurationJson $Target $Cd.PreservedConfigurationFiles
    if ($configuration -cne $Cd.ConfigurationManifestJson -or (Get-CdHash $configuration) -cne $Cd.ConfigurationDigest) { throw 'Target configuration changed after Review. Prepare and confirm again.' }
}

function Preserve-CdTargetConfiguration([string]$Package, [string]$Target, $Additional = @()) {
$packageManifest = Get-CdManifestJson $Package | ConvertFrom-Json
Assert-CdWebCompatibility $Package $Target
$configuration = Get-CdConfigurationJson $Target $Additional
# Unknown target-only files would be deleted by an update; require explicit preservation instead.
foreach ($targetFile in (Get-CdManifestJson $Target | ConvertFrom-Json)) {
    if ($targetFile.Path -notin @(($packageManifest.Path)) -and $targetFile.Path -notin @(($configuration | ConvertFrom-Json).Path)) { throw 'Target-only files require registered preservation or operator cleanup outside CD.' }
}
# Package settings are replaced only within private staging; target files remain untouched.
foreach ($file in (Get-CdConfigurationJson $Package @() | ConvertFrom-Json)) {
    if ($file.Path -notin @(($configuration | ConvertFrom-Json).Path)) { throw 'Package introduces environment configuration not present on the target.' }
}
foreach ($file in ($configuration | ConvertFrom-Json)) {
    $destination = Join-Path $Package $file.Path
    $directory = Split-Path $destination -Parent
    if (!(Test-Path -LiteralPath $directory)) { New-Item -ItemType Directory -Path $directory -Force | Out-Null }
    Copy-Item -LiteralPath (Join-Path $Target $file.Path) -Destination $destination -Force
}
if ((Get-CdConfigurationJson $Target $Additional) -cne $configuration -or (Get-CdConfigurationJson $Package $Additional) -cne $configuration) { throw 'Target configuration changed during staging or preservation failed.' }
return $configuration
}
