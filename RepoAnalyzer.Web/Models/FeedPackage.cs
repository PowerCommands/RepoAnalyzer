using RepoAnalyzer.Web.Models.Enums;

namespace RepoAnalyzer.Web.Models;

public sealed class FeedPackage
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public FeedType FeedType { get; set; } = FeedType.NuGet;
    public string PackageId { get; set; } = string.Empty;
    public string NormalizedPackageId { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string? MetadataJson { get; set; }
    public string FilePath { get; set; } = string.Empty;
    public string Sha256 { get; set; } = string.Empty;
    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastScanUtc { get; set; }
    public string VulnerabilityStatus { get; set; } = "Unknown";
    public string OutdatedStatus { get; set; } = "Unknown";
    public string? LatestKnownVersion { get; set; }
}
