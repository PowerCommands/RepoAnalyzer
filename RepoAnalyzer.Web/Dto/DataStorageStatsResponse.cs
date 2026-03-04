namespace RepoAnalyzer.Web.Dto;

public sealed class DataStorageStatsResponse
{
    public int JsonFileCount { get; set; }
    public long TotalJsonBytes { get; set; }
    public int SbomFileCount { get; set; }
    public long TotalSbomBytes { get; set; }
    public long TotalStoredBytes { get; set; }
}
