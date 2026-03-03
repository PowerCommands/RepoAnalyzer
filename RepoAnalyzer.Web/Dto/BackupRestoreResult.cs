namespace RepoAnalyzer.Web.Dto;

public sealed class BackupRestoreResult
{
    public int RestoredFileCount { get; set; }
    public List<string> RestoredFiles { get; set; } = new();
}
