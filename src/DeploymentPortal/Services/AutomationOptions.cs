namespace DeploymentPortal.Services;
public class AutomationOptions
{
    public string BackupRoot { get; set; } = @"C:\DeploymentBackups";
    public int ConnectionTimeoutSeconds { get; set; } = 30;
    public int DeploymentTimeoutSeconds { get; set; } = 900;
}
public static class DeploymentPaths
{
    public static string Normalize(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || !System.Text.RegularExpressions.Regex.IsMatch(value, @"^[A-Za-z]:\\") || value.IndexOfAny(['*', '?', '"', '<', '>', '|', '/', '\0']) >= 0 || value[2..].Contains(':'))
            throw new ArgumentException("Use an absolute local Windows path without wildcards.");
        var parts = value[3..].TrimEnd('\\').Split('\\');
        if (value.Any(char.IsControl) || parts.Any(x => x.Length == 0 || x is "." or ".." || x.EndsWith(' ') || x.EndsWith('.') || System.Text.RegularExpressions.Regex.IsMatch(x, @"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\.|$)", System.Text.RegularExpressions.RegexOptions.IgnoreCase)))
            throw new ArgumentException("Path segments cannot be empty, relative, or end in a space or period.");
        return value.TrimEnd('\\');
    }
    public static bool IsChild(string path, string root) => Normalize(path).StartsWith(Normalize(root) + "\\", StringComparison.OrdinalIgnoreCase);
    public static void Validate(string source, string target, AutomationOptions options)
    {
        var paths = new[] { Normalize(source), Normalize(target), Normalize(options.BackupRoot) };
        for (var i = 0; i < paths.Length; i++)
            for (var j = i + 1; j < paths.Length; j++)
                if (paths[i].Equals(paths[j], StringComparison.OrdinalIgnoreCase) || IsChild(paths[i], paths[j]) || IsChild(paths[j], paths[i]))
                    throw new ArgumentException("Source, target and backup directories must not be equal or nested inside one another.");
        foreach (var path in paths)
        {
            var relative = path[3..];
            if (new[] { "Windows", "Program Files", "Program Files (x86)", "ProgramData", "$Recycle.Bin", "System Volume Information" }
                .Any(folder => relative.Equals(folder, StringComparison.OrdinalIgnoreCase) || relative.StartsWith(folder + "\\", StringComparison.OrdinalIgnoreCase)) ||
                (relative.StartsWith("Users", StringComparison.OrdinalIgnoreCase) && relative.Split('\\').Length <= 2))
                throw new ArgumentException("Use an application directory, not a protected Windows or user-profile directory.");
        }
    }
}
