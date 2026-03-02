namespace RepoAnalyzer.Web.Models;

public sealed class DetectedProject
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string RepositoryId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public string ProjectType { get; set; } = string.Empty;
    public string? Framework { get; set; }
    public string? Version { get; set; }
    public string Language { get; set; } = string.Empty;
}
