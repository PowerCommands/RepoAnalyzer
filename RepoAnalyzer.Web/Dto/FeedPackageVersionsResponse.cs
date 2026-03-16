using RepoAnalyzer.Web.Models.Enums;

namespace RepoAnalyzer.Web.Dto;

public sealed class FeedPackageVersionsResponse
{
    public FeedType FeedType { get; set; } = FeedType.NuGet;
    public string PackageId { get; set; } = string.Empty;
    public string? LatestVersion { get; set; }
    public List<string> Versions { get; set; } = new();
}
