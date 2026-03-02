namespace RepoAnalyzer.Web.Dto;

public sealed class AnalysisLogLatestResponse
{
    public string FileName { get; set; } = "analysis.log";
    public int RequestedLines { get; set; }
    public int ReturnedLines { get; set; }
    public List<string> Lines { get; set; } = new();
}
