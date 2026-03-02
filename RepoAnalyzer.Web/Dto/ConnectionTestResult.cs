namespace RepoAnalyzer.Web.Dto;

public sealed class ConnectionTestResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
}
