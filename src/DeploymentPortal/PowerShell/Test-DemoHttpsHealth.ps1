# Executed on TARGET through the existing authenticated WinRM session.
# No request parameters, credentials, response bodies or exception messages are returned.
$ErrorActionPreference = 'Stop'
$response = $null
try {
    $request = [Net.HttpWebRequest]::Create('https://deployment-test.vitonta.com/')
    $request.Method = 'GET'
    $request.AllowAutoRedirect = $false
    $request.UseDefaultCredentials = $false
    $request.Timeout = 10000
    $request.ReadWriteTimeout = 10000
    # Normal Windows TLS chain/hostname validation; no certificate callback override.
    $response = $request.GetResponse()
    $status = [int]$response.StatusCode
    [pscustomobject]@{ Success=($status -eq 200); Verified=($status -eq 200); HealthHttpStatus=$status; Verification='http'; Message='Demo HTTPS health check completed.' }
} catch [Net.WebException] {
    $response = $_.Exception.Response
    $status = $null
    if ($null -ne $response) { $status = [int]$response.StatusCode }
    $reason = switch ($_.Exception.Status) {
        'TrustFailure' { 'tls' }
        'SecureChannelFailure' { 'tls' }
        'Timeout' { 'timeout' }
        default { 'network' }
    }
    [pscustomobject]@{ Success=$false; Verified=$false; HealthHttpStatus=$status; Verification=$reason; Message='Demo HTTPS health check failed.' }
} catch {
    [pscustomobject]@{ Success=$false; Verified=$false; HealthHttpStatus=$null; Verification='network'; Message='Demo HTTPS health check failed.' }
} finally {
    if ($null -ne $response) { $response.Close() }
}
