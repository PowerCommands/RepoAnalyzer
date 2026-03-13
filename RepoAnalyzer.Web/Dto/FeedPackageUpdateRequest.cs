namespace RepoAnalyzer.Web.Dto;

public sealed class FeedPackageUpdateRequest
{
    public string? TargetVersion { get; set; }
    public bool KeepOldVersion { get; set; }
}
