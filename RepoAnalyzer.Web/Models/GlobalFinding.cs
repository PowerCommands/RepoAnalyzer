using RepoAnalyzer.Web.Models.Enums;

namespace RepoAnalyzer.Web.Models;

public sealed class GlobalFinding
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Key { get; set; } = string.Empty;
    public FindingType Type { get; set; }
    public string Ecosystem { get; set; } = string.Empty;
    public string PackageName { get; set; } = string.Empty;
    public string InstalledVersion { get; set; } = string.Empty;
    public string? FixedVersion { get; set; }
    public string Severity { get; set; } = "Unknown";
    public string? Advisory { get; set; }
    public string SourceTool { get; set; } = string.Empty;
    public DateTimeOffset AnalyzedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public List<AffectedLocation> AffectedLocations { get; set; } = new();
}
