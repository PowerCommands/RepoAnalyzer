namespace RepoAnalyzer.Web.Dto;

public sealed class FetchRepositoriesRequest
{
    public List<string>? WorkspaceNames { get; set; }
}
