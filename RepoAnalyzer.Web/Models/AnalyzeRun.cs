namespace RepoAnalyzer.Web.Models;

public sealed class AnalyzeRun
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string RepositoryId { get; set; } = string.Empty;
    public string Status { get; set; } = "Queued";
    public DateTimeOffset StartedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public string? CurrentStep { get; set; }
    public string? CurrentMessage { get; set; }
    public int? ProgressPercent { get; set; }
    public string? Error { get; set; }
    public RepoAnalysisSnapshot? Snapshot { get; set; }
}
