namespace RepoAnalyzer.Web.Models;

public sealed class RepoFile
{
    public string Path { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
}
