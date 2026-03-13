namespace RepoAnalyzer.Web.Dto;

public sealed class NuGetFeedImportRequest
{
    public string PackageId { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string? ComponentId { get; set; }
}
