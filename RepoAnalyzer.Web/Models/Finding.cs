using RepoAnalyzer.Web.Models.Enums;

namespace RepoAnalyzer.Web.Models;

public sealed class Finding
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public FindingType Type { get; set; }
    public string Ecosystem { get; set; } = string.Empty;
    public string PackageName { get; set; } = string.Empty;
    public string? InstalledVersion { get; set; }
    public string? FixedVersion { get; set; }
    public string? Severity { get; set; }
    public string? Advisory { get; set; }
    public string SourceTool { get; set; } = string.Empty;
    public string ProjectId { get; set; } = string.Empty;
    public string RepositoryId { get; set; } = string.Empty;
    public DateTimeOffset CapturedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}
