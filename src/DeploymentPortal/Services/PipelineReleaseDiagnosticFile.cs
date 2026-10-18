using System.Text.Json;

namespace DeploymentPortal.Services;

public interface IPipelineReleaseDiagnosticFile
{
    void Write(string stage, string endpoint, int? httpStatus, string exceptionType);
    void WriteArtifactMatching(JsonElement candidates, string failureReason);
}

// Temporary Demo-only diagnostics. The caller supplies constant labels, never URLs,
// response content or exception messages. Provision the directory using the admin script.
public sealed class PipelineReleaseDiagnosticFile(string? directory = null) : IPipelineReleaseDiagnosticFile
{
    private static readonly object Sync = new();
    private readonly string root = directory ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "DeploymentPortal", "Diagnostics");

    public void Write(string stage, string endpoint, int? httpStatus, string exceptionType)
        => Append(new { stage, endpoint, httpStatus, exceptionType });

    // Candidates are the sanitized ID/shape projection, never upstream JSON.
    public void WriteArtifactMatching(JsonElement candidates, string failureReason)
        => Append(new { candidates, failureReason });

    private void Append(object entry)
    {
        try
        {
            lock (Sync)
            {
                // Do not create a broadly inherited directory from the IIS worker.
                if (!Directory.Exists(root)) return;
                for (var current = new DirectoryInfo(root); current is not null; current = current.Parent)
                    if ((current.Attributes & FileAttributes.ReparsePoint) != 0) return;
                var file = Path.Combine(root, "demo-release-discovery.jsonl");
                if (File.Exists(file) && (File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) return;
                // Bound this temporary file; diagnostics must not fill the server disk.
                if (File.Exists(file) && new FileInfo(file).Length >= 1_048_576) return;
                File.AppendAllText(file, JsonSerializer.Serialize(entry) + Environment.NewLine);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            // Diagnostic storage must never change the existing status response.
        }
    }
}
