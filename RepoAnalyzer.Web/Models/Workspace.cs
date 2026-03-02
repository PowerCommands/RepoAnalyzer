namespace RepoAnalyzer.Web.Models;

public sealed class Workspace
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string ConnectionId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
}
