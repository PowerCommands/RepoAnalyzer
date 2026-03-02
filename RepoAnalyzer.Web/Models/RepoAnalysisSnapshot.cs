namespace RepoAnalyzer.Web.Models;

public sealed class RepoAnalysisSnapshot
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string RepositoryId { get; set; } = string.Empty;
    public DateTimeOffset AnalyzedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public List<DetectedProject> DetectedProjects { get; set; } = new();
    public List<Component> Components { get; set; } = new();
    public List<Finding> Vulnerabilities { get; set; } = new();
    public List<Finding> Outdated { get; set; } = new();
}
