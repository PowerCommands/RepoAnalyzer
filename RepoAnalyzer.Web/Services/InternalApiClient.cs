using RepoAnalyzer.Web.Dto;
using RepoAnalyzer.Web.Models;
using RepoAnalyzer.Web.Services.Analysis;
using RepoAnalyzer.Web.Services.Analysis.Logging;

namespace RepoAnalyzer.Web.Services;

public sealed class InternalApiClient
{
    private readonly ConnectionService _connectionService;
    private readonly ConnectionValidationService _connectionValidationService;
    private readonly RepositorySyncService _repositorySyncService;
    private readonly QueryService _queryService;
    private readonly RepositoryAnalyzerService _repositoryAnalyzerService;
    private readonly AnalyzeRunService _analyzeRunService;
    private readonly IAnalysisLog _analysisLog;
    private readonly BackupService _backupService;
    private readonly IConfiguration _configuration;

    public InternalApiClient(
        ConnectionService connectionService,
        ConnectionValidationService connectionValidationService,
        RepositorySyncService repositorySyncService,
        QueryService queryService,
        RepositoryAnalyzerService repositoryAnalyzerService,
        AnalyzeRunService analyzeRunService,
        IAnalysisLog analysisLog,
        BackupService backupService,
        IConfiguration configuration)
    {
        _connectionService = connectionService;
        _connectionValidationService = connectionValidationService;
        _repositorySyncService = repositorySyncService;
        _queryService = queryService;
        _repositoryAnalyzerService = repositoryAnalyzerService;
        _analyzeRunService = analyzeRunService;
        _analysisLog = analysisLog;
        _backupService = backupService;
        _configuration = configuration;
    }

    public async Task<List<ConnectionView>> GetConnectionsAsync(CancellationToken ct = default)
        => await _connectionService.GetAllAsync(ct);

    public async Task<ConnectionView?> SaveConnectionAsync(ConnectionUpsertRequest request, CancellationToken ct = default)
        => await _connectionService.UpsertAsync(request, ct);

    public async Task<ConnectionTestResult?> TestConnectionAsync(string id, CancellationToken ct = default)
        => await _connectionValidationService.TestConnectionAsync(id, ct);

    public async Task DeleteConnectionAsync(string id, CancellationToken ct = default)
        => await _connectionService.DeleteAsync(id, ct);

    public async Task SyncConnectionAsync(string id, CancellationToken ct = default)
        => await _repositorySyncService.SyncConnectionAsync(id, ct);

    public async Task<List<string>> GetProviderWorkspaceNamesPreviewAsync(string id, CancellationToken ct = default)
        => await _repositorySyncService.GetProviderWorkspaceNamesPreviewAsync(id, ct);

    public async Task<FetchRepositoriesResult?> FetchRepositoriesAsync(string id, IEnumerable<string>? workspaceNames = null, CancellationToken ct = default)
    {
        var requestedWorkspaceNames = workspaceNames?
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var result = await _repositorySyncService.FetchNewRepositoriesAsync(id, requestedWorkspaceNames, ct);

        return new FetchRepositoriesResult
        {
            AddedWorkspaces = result.AddedWorkspaces,
            AddedRepositories = result.AddedRepositories
        };
    }

    public async Task<List<Workspace>> GetWorkspacesAsync(CancellationToken ct = default)
        => await _queryService.GetWorkspacesAsync(ct);

    public async Task<List<RepositoryEntity>> GetRepositoriesAsync(CancellationToken ct = default)
        => await _queryService.GetRepositoriesAsync(ct);

    public async Task DeleteRepositoryAsync(string id, CancellationToken ct = default)
        => await _connectionService.DeleteRepositoryAsync(id, ct);

    public async Task<RepoAnalysisSnapshot?> AnalyzeAsync(string repositoryId, CancellationToken ct = default)
        => await _repositoryAnalyzerService.AnalyzeRepositoryAsync(repositoryId, analysisRunId: null, progress: null, ct);

    public Task<AnalyzeRun?> StartAnalysisAsync(string repositoryId, CancellationToken ct = default)
        => Task.FromResult<AnalyzeRun?>(_analyzeRunService.Start(repositoryId));

    public Task<AnalyzeRun?> GetAnalysisRunAsync(string runId, CancellationToken ct = default)
        => Task.FromResult(_analyzeRunService.Get(runId));

    public Task<List<AnalyzeRun>> GetRecentAnalysisRunsAsync(int take = 20, CancellationToken ct = default)
        => Task.FromResult(_analyzeRunService.GetRecent(take));

    public async Task<List<RepoAnalysisSnapshot>> GetSnapshotsAsync(CancellationToken ct = default)
        => await _queryService.GetSnapshotsAsync(ct);

    public async Task<List<Component>> GetComponentsAsync(string? nameFilter, string? repositoryId, int? take = null, CancellationToken ct = default)
        => await _queryService.GetLatestComponentsAsync(nameFilter, repositoryId, take, ct);

    public async Task DeleteAnalysisAsync(string repositoryId, CancellationToken ct = default)
        => await _repositoryAnalyzerService.ClearRepositoryAnalysisAsync(repositoryId, ct);

    public async Task<List<GlobalFinding>> GetFindingsAsync(string? ecosystem, string? severity, string? repositoryId, CancellationToken ct = default)
        => await _queryService.GetGlobalFindingsAsync(ecosystem, severity, repositoryId, ct);

    public async Task<AnalysisLogLatestResponse> GetLatestAnalysisLogsAsync(int lines = 2000, CancellationToken ct = default)
    {
        var requestedLines = Math.Clamp(lines, 1, 50000);
        var logLines = await _analysisLog.ReadLatestLinesAsync(requestedLines, ct);

        return new AnalysisLogLatestResponse
        {
            RequestedLines = requestedLines,
            ReturnedLines = logLines.Count,
            Lines = logLines
        };
    }

    public async Task ClearAnalysisLogsAsync(CancellationToken ct = default)
        => await _analysisLog.ClearAsync(ct);

    public string GetAnalysisLogDownloadUrl() => "/internal-api/tools/logs/download";

    public async Task<BackupDownloadResult> DownloadBackupAsync(CancellationToken ct = default)
    {
        var result = await _backupService.CreateBackupZipAsync(ct);
        using var backupStream = result.Stream;

        return new BackupDownloadResult
        {
            FileName = result.FileName,
            Bytes = result.Stream.ToArray()
        };
    }

    public async Task<BackupRestoreResult?> RestoreBackupAsync(string fileName, Stream stream, CancellationToken ct = default)
        => await _backupService.RestoreZipAsync(stream, ct);

    public Task<DataStorageStatsResponse> GetDataStorageStatsAsync(CancellationToken ct = default)
    {
        var dataPath = _configuration["DataPath"] ?? "/app/data";
        Directory.CreateDirectory(dataPath);

        var jsonFiles = Directory.GetFiles(dataPath, "*.json", SearchOption.TopDirectoryOnly);
        var totalBytes = jsonFiles
            .Select(path => new FileInfo(path))
            .Sum(file => file.Exists ? file.Length : 0L);

        return Task.FromResult(new DataStorageStatsResponse
        {
            JsonFileCount = jsonFiles.Length,
            TotalJsonBytes = totalBytes
        });
    }

    public sealed class FetchRepositoriesResult
    {
        public int AddedWorkspaces { get; set; }
        public int AddedRepositories { get; set; }
    }

    public sealed class BackupDownloadResult
    {
        public string FileName { get; set; } = string.Empty;
        public byte[] Bytes { get; set; } = Array.Empty<byte>();
    }
}
