namespace RepoAnalyzer.Web.Dto;

public sealed class NuGetFeedVersionsResponse
{
    public string PackageId { get; set; } = string.Empty;
    public List<string> Versions { get; set; } = new();
}
