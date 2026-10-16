using Microsoft.Extensions.Configuration.Json;

namespace DeploymentPortal.Services;

public static class PipelineConfigurationDiagnostics
{
    // Inspect only these non-secret execution switches, never enumerate configuration values.
    public static string Source(IConfiguration configuration, string key)
    {
        if (key is not ("TfsCd:Enabled" or "TfsCd:ExecutionEnabled"))
            throw new ArgumentException("Only execution switches may be inspected.");
        if (configuration is IConfigurationRoot root)
            foreach (var provider in root.Providers.Reverse())
            {
                if (!provider.TryGet(key, out _)) continue;
                if (provider is ChainedConfigurationProvider chained) return Source(chained.Configuration, key);
                return provider is JsonConfigurationProvider json
                    ? System.IO.Path.GetFileName(json.Source.Path) ?? "JSON configuration" : provider.GetType().Name;
            }
        return "TfsCdOptions default (false)";
    }
}
