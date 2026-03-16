using RepoAnalyzer.Web.Models.Enums;

namespace RepoAnalyzer.Web.Dto;

public sealed class FeedPackageImportRequest
{
    public FeedType FeedType { get; set; } = FeedType.NuGet;
    public string PackageId { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string? ComponentId { get; set; }
}
