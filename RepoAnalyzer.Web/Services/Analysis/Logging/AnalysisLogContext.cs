namespace RepoAnalyzer.Web.Services.Analysis.Logging;

public sealed class AnalysisLogContext
{
    public string AnalysisRunId { get; set; } = string.Empty;
    public string? ConnectionId { get; set; }
    public string? ProviderType { get; set; }
    public string? WorkspaceId { get; set; }
    public string? WorkspaceName { get; set; }
    public string? RepositoryId { get; set; }
    public string? RepositoryName { get; set; }
}
