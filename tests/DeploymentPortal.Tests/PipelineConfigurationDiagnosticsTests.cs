using DeploymentPortal.Services;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace DeploymentPortal.Tests;

public sealed class PipelineConfigurationDiagnosticsTests
{
    [Fact]
    public void DevelopmentSwitchOverridesRegistryAndReportsActualProvider()
    {
        var local = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> {
            ["TfsCd:Enabled"] = "true", ["TfsCd:ExecutionEnabled"] = "true"
        }).Build();
        var effective = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> {
            ["TfsCd:Enabled"] = "false", ["TfsCd:ExecutionEnabled"] = "false"
        }).AddConfiguration(local).Build();
        Assert.True(effective.GetValue<bool>("TfsCd:Enabled"));
        Assert.True(effective.GetValue<bool>("TfsCd:ExecutionEnabled"));
        Assert.Equal("MemoryConfigurationProvider", PipelineConfigurationDiagnostics.Source(effective, "TfsCd:ExecutionEnabled"));
    }

    [Fact]
    public void MissingExecutionSwitchDefaultsToDisabled()
    {
        var effective = new ConfigurationBuilder().Build();
        Assert.False(effective.GetValue<bool>("TfsCd:ExecutionEnabled"));
        Assert.Equal("TfsCdOptions default (false)", PipelineConfigurationDiagnostics.Source(effective, "TfsCd:ExecutionEnabled"));
    }

    [Fact]
    public void DiagnosticsRejectSecretKeys()
    {
        Assert.Throws<ArgumentException>(() => PipelineConfigurationDiagnostics.Source(new ConfigurationBuilder().Build(), "Tfs:Password"));
    }
}
