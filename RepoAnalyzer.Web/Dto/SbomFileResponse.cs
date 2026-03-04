namespace RepoAnalyzer.Web.Dto;

public sealed class SbomFileResponse
{
    public string Id { get; set; } = string.Empty;
    public string ConnectionId { get; set; } = string.Empty;
    public string RepositoryId { get; set; } = string.Empty;
    public string RepositoryName { get; set; } = string.Empty;
    public string SpecVersion { get; set; } = string.Empty;
    public string OutputType { get; set; } = string.Empty;
    public string Format { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public int ComponentCount { get; set; }
}
