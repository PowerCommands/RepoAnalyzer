namespace RepoAnalyzer.Web.Models;

public sealed class ComponentFeedPackageLink
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string ComponentId { get; set; } = string.Empty;
    public string FeedPackageId { get; set; } = string.Empty;
}
