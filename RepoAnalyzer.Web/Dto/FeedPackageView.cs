namespace RepoAnalyzer.Web.Dto;

public sealed class FeedPackageView
{
    public string Id { get; set; } = string.Empty;
    public string FeedType { get; set; } = string.Empty;
    public string PackageId { get; set; } = string.Empty;
    public string NormalizedPackageId { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string? MetadataJson { get; set; }
    public string FilePath { get; set; } = string.Empty;
    public string Sha256 { get; set; } = string.Empty;
    public DateTimeOffset CreatedUtc { get; set; }
    public DateTimeOffset? LastScanUtc { get; set; }
    public string VulnerabilityStatus { get; set; } = "Unknown";
    public string OutdatedStatus { get; set; } = "Unknown";
    public string? LatestKnownVersion { get; set; }
    public List<string> ComponentIds { get; set; } = new();
    public string DownloadUrl { get; set; } = string.Empty;
}
