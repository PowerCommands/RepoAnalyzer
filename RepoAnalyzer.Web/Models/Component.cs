namespace RepoAnalyzer.Web.Models;

public sealed class Component
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Ecosystem { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string ProjectId { get; set; } = string.Empty;
    public string RepositoryId { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public DateTimeOffset CapturedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}
