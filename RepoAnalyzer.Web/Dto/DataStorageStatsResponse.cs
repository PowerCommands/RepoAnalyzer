namespace RepoAnalyzer.Web.Dto;

public sealed class DataStorageStatsResponse
{
    public int JsonFileCount { get; set; }
    public long TotalJsonBytes { get; set; }
}
