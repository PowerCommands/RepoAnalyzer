namespace RepoAnalyzer.Web.Dto;

public sealed class SbomCreateRequest
{
    public string ConnectionId { get; set; } = string.Empty;
    public string RepositoryId { get; set; } = string.Empty;
    public string SpecVersion { get; set; } = "1.7";
    public string OutputType { get; set; } = "Flat";
    public string Format { get; set; } = "JSON";
}
