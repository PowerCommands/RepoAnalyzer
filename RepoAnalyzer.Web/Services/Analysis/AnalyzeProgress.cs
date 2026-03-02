namespace RepoAnalyzer.Web.Services.Analysis;

public sealed class AnalyzeProgress
{
    public string Step { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public int? Percent { get; set; }
}
