using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace DeploymentPortal.ViewModels.Tfs;

public class TfsRunBuildViewModel
{
    [Range(1, int.MaxValue)] public int DefinitionId { get; set; }
    [Required, StringLength(10), RegularExpression(@"^[0-9]+$", ErrorMessage = "Enter a positive numeric TFVC changeset ID."), Display(Name = "ChangeSet (Src Version)")]
    public string SourceVersion { get; set; } = "";
    [BindNever] public TfsDefinitionViewModel? Definition { get; set; }
    [BindNever] public string? Error { get; set; }
    [BindNever] public string? ConfirmationToken { get; set; }
}
