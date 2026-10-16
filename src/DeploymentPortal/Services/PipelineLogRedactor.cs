using System.Text.RegularExpressions;

namespace DeploymentPortal.Services;

// Supplemental redaction; TFS must still mask pipeline secrets at their source.
public static partial class PipelineLogRedactor
{
    public static string Redact(string value) => UrlCredentials().Replace(
        Authorization().Replace(Assignments().Replace(value, "$1[redacted]"), "$1[redacted]"), "$1[redacted]@");

    [GeneratedRegex(@"(?im)(\b(?:password|passwd|pwd|token|secret|api[_-]?key|access[_-]?token)\s*[:=]\s*)(?:""[^""\r\n]*""|'[^'\r\n]*'|[^\s;]+)", RegexOptions.CultureInvariant, 100)]
    private static partial Regex Assignments();
    [GeneratedRegex(@"(?im)(\b(?:authorization\s*:\s*)?(?:Bearer|Basic)\s+)[A-Za-z0-9+/=._~-]+", RegexOptions.CultureInvariant, 100)]
    private static partial Regex Authorization();
    [GeneratedRegex(@"(?i)(https?://)[^\s/@]+:[^\s/@]+@", RegexOptions.CultureInvariant, 100)]
    private static partial Regex UrlCredentials();
}
