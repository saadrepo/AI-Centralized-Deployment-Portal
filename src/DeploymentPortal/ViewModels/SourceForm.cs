using System.ComponentModel.DataAnnotations;
using DeploymentPortal.Models;
using DeploymentPortal.Services;

namespace DeploymentPortal.ViewModels;
public sealed class SourceForm
{
    public int Id { get; set; }
    [Required, StringLength(100), Display(Name = "Display name")] public string DisplayName { get; set; } = "";
    [EnumDataType(typeof(InfrastructureSourceProvider))] public InfrastructureSourceProvider Provider { get; set; }
    [Required, StringLength(400), Display(Name = "Organization / Collection URL")] public string Destination { get; set; } = "";
    [StringLength(64), Display(Name = "Credential reference")] public string? CredentialReference { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public IReadOnlyList<SourceCredentialMetadata> Credentials { get; set; } = [];
    public IReadOnlyList<ConfiguredSource> Configured { get; set; } = [];
    public static SourceForm From(InfrastructureSource source) => new() { Id = source.Id, DisplayName = source.DisplayName, Provider = source.Provider, Destination = source.Destination, CredentialReference = source.CredentialReference, Enabled = source.Enabled };
}
public sealed record SourcesPage(IReadOnlyList<InfrastructureSource> Sources, IReadOnlyList<ConfiguredSource> Configured);
