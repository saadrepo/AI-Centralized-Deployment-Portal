param([string]$Root, [string]$Scripts)
$ErrorActionPreference = 'Stop'
function Assert-Test($Condition, [string]$Message) { if (!$Condition) { throw $Message } }
function Assert-Rejected([scriptblock]$Action) { $rejected=$false; try { & $Action } catch { $rejected=$true }; Assert-Test $rejected 'Expected a security rejection.' }
foreach ($file in Get-ChildItem -LiteralPath $Scripts -Filter '*.ps1') {
    $tokens=$null; $errors=$null
    [void][Management.Automation.Language.Parser]::ParseFile($file.FullName,[ref]$tokens,[ref]$errors)
    Assert-Test (!$errors) 'PowerShell parse failed.'
}
. (Join-Path $Scripts 'CdPackageSecurity.ps1')
foreach ($path in @('..\escape', '\\server\share', 'C:\file', 'a:stream', 'CON.json', 'a.\file', 'a%2fb')) { Assert-Rejected { Assert-CdRelativePath $path } }
Assert-CdRelativePath 'content\safe.dll'
$target=Join-Path $Root 'target'; $package=Join-Path $Root 'package'
New-Item -ItemType Directory -Path $target,$package | Out-Null
$web='<configuration><system.webServer><handlers><add name="aspNetCore" path="*" verb="*" modules="AspNetCoreModuleV2" resourceType="Unspecified" /></handlers><aspNetCore processPath="dotnet" arguments=".\app.dll" hostingModel="inprocess"><environmentVariables><environmentVariable name="ASPNETCORE_ENVIRONMENT" value="Production" /></environmentVariables></aspNetCore></system.webServer></configuration>'
foreach ($directory in @($target,$package)) {
    [IO.File]::WriteAllText((Join-Path $directory 'web.config'),$web)
    [IO.File]::WriteAllText((Join-Path $directory 'app.dll'),'binary')
    [IO.File]::WriteAllText((Join-Path $directory 'appsettings.json'),'{}')
    [IO.File]::WriteAllText((Join-Path $directory 'app.runtimeconfig.json'),'{"runtimeOptions":{"tfm":"net10.0","framework":{"name":"Microsoft.NETCore.App","version":"10.0.0"}}}')
}
[IO.File]::WriteAllText((Join-Path $target 'appsettings.json'),'{"Secret":"target-only-secret"}')
[IO.File]::WriteAllText((Join-Path $target 'appsettings.Production.json'),'{"Environment":"Production"}')
New-Item -ItemType Directory -Path (Join-Path $target 'config') | Out-Null
[IO.File]::WriteAllText((Join-Path $target 'config\settings.json'),'{"Secret":"extra-secret"}')
$configuration = Preserve-CdTargetConfiguration $package $target @('config\settings.json')
Assert-Test (!$configuration.Contains('target-only-secret') -and !$configuration.Contains('extra-secret')) 'Configuration contents leaked into hashes.'
Assert-Test ([IO.File]::ReadAllText((Join-Path $package 'appsettings.json')).Contains('target-only-secret')) 'Target settings were not preserved.'
Assert-Test ([IO.File]::ReadAllText((Join-Path $package 'web.config')) -ceq $web) 'web.config changed.'
Assert-Test (Test-Path -LiteralPath (Join-Path $package 'appsettings.Production.json')) 'Environment settings were dropped.'
Assert-Test (Test-Path -LiteralPath (Join-Path $package 'config\settings.json')) 'Registered configuration was dropped.'
$before=Get-CdConfigurationJson $target @('config\settings.json')
[IO.File]::WriteAllText((Join-Path $package 'app.runtimeconfig.json'),'{"runtimeOptions":{"tfm":"net11.0"}}')
Assert-Rejected { Assert-CdWebCompatibility $package $target }
Copy-Item -LiteralPath (Join-Path $target 'app.runtimeconfig.json') -Destination (Join-Path $package 'app.runtimeconfig.json') -Force
[IO.File]::WriteAllText((Join-Path $target 'appsettings.json'),'changed')
Assert-Test ((Get-CdConfigurationJson $target @('config\settings.json')) -cne $before) 'Configuration changes were not detected.'
[IO.File]::WriteAllText((Join-Path $target 'unknown-data.txt'),'keep')
Assert-Rejected { Preserve-CdTargetConfiguration $package $target @('config\settings.json') }
Remove-Item -LiteralPath (Join-Path $target 'unknown-data.txt')
[IO.File]::WriteAllText((Join-Path $package 'web.config'),$web.Replace('.\app.dll','.\other.dll'))
Assert-Rejected { Assert-CdWebCompatibility $package $target }
[IO.File]::WriteAllText((Join-Path $package 'web.config'),'<!DOCTYPE configuration [<!ENTITY x SYSTEM "file:///C:/secret">]><configuration>&x;</configuration>')
Assert-Rejected { Read-CdWebConfig (Join-Path $package 'web.config') }
# Compose trusted helper + preparation exactly as the remoting wrapper does; never invoke IIS here.
$body=[IO.File]::ReadAllText((Join-Path $Scripts 'Prepare-CdPackage.ps1')) -replace '^param\(\$Request\)\s*',''
$combined='param($Request)' + "`n" + [IO.File]::ReadAllText((Join-Path $Scripts 'CdPackageSecurity.ps1')) + "`n" + $body
[void][scriptblock]::Create($combined)
Write-Output 'Local CD preservation, compatibility, traversal and XML checks passed. No remoting or IIS operation executed.'
