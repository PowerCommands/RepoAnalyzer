namespace RepoAnalyzer.Web.Models;

public sealed class AffectedLocation
{
    public string RepositoryId { get; set; } = string.Empty;
    public string ProjectId { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
}
