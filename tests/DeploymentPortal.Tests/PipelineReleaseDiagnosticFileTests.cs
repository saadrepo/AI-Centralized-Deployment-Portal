using System.Text.Json.Nodes;
using DeploymentPortal.Services;
using Xunit;

namespace DeploymentPortal.Tests;

public sealed class PipelineReleaseDiagnosticFileTests
{
    [Fact]
    public void AppendsOnlyFourFieldsAndBoundsFileSize()
    {
        var root = Path.Combine(Path.GetTempPath(), "pipeline-diagnostics-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var sink = new PipelineReleaseDiagnosticFile(root);
            sink.Write("release candidate retrieval", "release/releases", 403, "HttpRequestException");
            sink.Write("release artifact matching", "release/releases", null, "InvalidOperationException");
            var file = Path.Combine(root, "demo-release-discovery.jsonl");
            var lines = File.ReadAllLines(file);
            Assert.Equal(2, lines.Length);
            var entry = JsonNode.Parse(lines[0])!.AsObject();
            Assert.Equal(new[] { "stage", "endpoint", "httpStatus", "exceptionType" }, entry.Select(p => p.Key));
            Assert.Equal("release candidate retrieval", entry["stage"]!.GetValue<string>());
            Assert.Equal("release/releases", entry["endpoint"]!.GetValue<string>());
            Assert.Equal(403, entry["httpStatus"]!.GetValue<int>());
            Assert.Equal("HttpRequestException", entry["exceptionType"]!.GetValue<string>());
            Assert.Null(JsonNode.Parse(lines[1])!["httpStatus"]);
            File.WriteAllText(file, new string('x', 1_048_576));
            sink.Write("release discovery", "release/definitions", null, "InvalidOperationException");
            Assert.Equal(1_048_576, new FileInfo(file).Length);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void UnprovisionedDirectoryDoesNotChangeFailureBehavior()
    {
        var root = Path.Combine(Path.GetTempPath(), "pipeline-diagnostics-" + Guid.NewGuid().ToString("N"));
        new PipelineReleaseDiagnosticFile(root).Write("release discovery", "release/definitions", null, "InvalidOperationException");
        Assert.False(Directory.Exists(root));
    }

    [Fact]
    public void FileWriteFailureIsNonFatal()
    {
        var root = Path.Combine(Path.GetTempPath(), "pipeline-diagnostics-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "demo-release-discovery.jsonl"));
        try { new PipelineReleaseDiagnosticFile(root).Write("release discovery", "release/definitions", null, "InvalidOperationException"); }
        finally { Directory.Delete(root, true); }
    }
}
