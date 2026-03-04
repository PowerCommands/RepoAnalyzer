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
    private readonly SafeCliRunner _safeCliRunner;
    private readonly SbomService _sbomService;
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
        SafeCliRunner safeCliRunner,
        SbomService sbomService,
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
        _safeCliRunner = safeCliRunner;
        _sbomService = sbomService;
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

    public async Task<SbomFileResponse> CreateSbomAsync(SbomCreateRequest request, CancellationToken ct = default)
        => await _sbomService.CreateAsync(request, ct);

    public async Task<List<SbomFileResponse>> GetSbomFilesAsync(string? connectionId, string? repositoryId, CancellationToken ct = default)
        => await _sbomService.ListAsync(connectionId, repositoryId, ct);

    public async Task DeleteSbomAsync(string id, CancellationToken ct = default)
    {
        var deleted = await _sbomService.DeleteAsync(id, ct);
        if (!deleted)
        {
            throw new InvalidOperationException("SBOM file was not found.");
        }
    }

    public string GetSbomDownloadUrl(string id) => $"/internal-api/tools/sbom/{Uri.EscapeDataString(id)}/download";

    public Task<DataStorageStatsResponse> GetDataStorageStatsAsync(CancellationToken ct = default)
    {
        var dataPath = _configuration["DataPath"] ?? "/app/data";
        Directory.CreateDirectory(dataPath);

        var jsonFiles = Directory.GetFiles(dataPath, "*.json", SearchOption.TopDirectoryOnly);
        var totalBytes = jsonFiles
            .Select(path => new FileInfo(path))
            .Sum(file => file.Exists ? file.Length : 0L);

        var sbomRoot = Path.Combine(dataPath, "sbom");
        Directory.CreateDirectory(sbomRoot);
        var sbomFiles = Directory.GetFiles(sbomRoot)
            .Where(x => !string.Equals(Path.GetFileName(x), "index.json", StringComparison.OrdinalIgnoreCase))
            .ToList();
        var sbomBytes = sbomFiles
            .Select(path => new FileInfo(path))
            .Sum(file => file.Exists ? file.Length : 0L);

        return Task.FromResult(new DataStorageStatsResponse
        {
            JsonFileCount = jsonFiles.Length,
            TotalJsonBytes = totalBytes,
            SbomFileCount = sbomFiles.Count,
            TotalSbomBytes = sbomBytes,
            TotalStoredBytes = totalBytes + sbomBytes
        });
    }

    public async Task<ToolchainVersionsResponse> GetToolchainVersionsAsync(CancellationToken ct = default)
    {
        var dotnetSdkVersion = await RunVersionCommandAsync("dotnet", ["--version"], TimeSpan.FromSeconds(10), ct);
        var nugetVersionRaw = await RunVersionCommandAsync("dotnet", ["nuget", "--version"], TimeSpan.FromSeconds(10), ct);
        var nodeVersion = await RunVersionCommandAsync("npm", ["-v"], TimeSpan.FromSeconds(10), ct);
        var mavenVersionRaw = await RunVersionCommandAsync("mvn", ["-v"], TimeSpan.FromSeconds(15), ct);
        var pythonVersion = await RunVersionCommandAsync("python3", ["--version"], TimeSpan.FromSeconds(10), ct);
        var pipVersion = await RunVersionCommandAsync("python3", ["-m", "pip", "--version"], TimeSpan.FromSeconds(10), ct);

        var hasDotNet = IsAvailable(dotnetSdkVersion);
        var hasNpm = IsAvailable(nodeVersion);
        var hasMaven = IsAvailable(mavenVersionRaw);
        var hasPython = IsAvailable(pythonVersion);

        return new ToolchainVersionsResponse
        {
            DotNetSdkVersion = dotnetSdkVersion,
            NuGetVersion = ExtractLastNonEmptyLine(nugetVersionRaw),
            NodeVersion = nodeVersion,
            MavenVersion = ExtractFirstNonEmptyLine(mavenVersionRaw),
            PythonVersion = pythonVersion,
            PipVersion = pipVersion,
            NuGetScannerTool = hasDotNet
                ? "dotnet list <project.csproj> package --vulnerable --include-transitive --format json + --outdated --include-transitive --format json"
                : "Unavailable (dotnet not found)",
            NpmScannerTool = hasNpm
                ? "npm audit --json --package-lock-only + npm outdated --json"
                : "Unavailable (npm not found)",
            MavenScannerTool = hasMaven
                ? "mvn versions:display-dependency-updates + mvn org.owasp:dependency-check-maven:check"
                : "Unavailable (mvn not found)",
            PythonScannerTool = hasPython
                ? "python3 -m pip list --outdated --format=json + python3 -m pip_audit -r requirements.txt --format json"
                : "Unavailable (python3 not found)"
        };
    }

    private async Task<string> RunVersionCommandAsync(
        string command,
        IEnumerable<string> args,
        TimeSpan timeout,
        CancellationToken ct)
    {
        try
        {
            var result = await _safeCliRunner.RunAsync(
                command,
                args,
                workingDirectory: AppContext.BaseDirectory,
                timeout: timeout,
                outputLimit: 8_192,
                ct);

            if (result.ExitCode != 0)
            {
                return "Unavailable";
            }

            var combined = string.IsNullOrWhiteSpace(result.StdOut)
                ? result.StdErr
                : result.StdOut;

            return string.IsNullOrWhiteSpace(combined)
                ? "Unavailable"
                : combined.Trim();
        }
        catch
        {
            return "Unavailable";
        }
    }

    private static string ExtractFirstNonEmptyLine(string text)
        => text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault(line => !string.IsNullOrWhiteSpace(line))
            ?? "Unavailable";

    private static string ExtractLastNonEmptyLine(string text)
        => text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault(line => !string.IsNullOrWhiteSpace(line))
            ?? "Unavailable";

    private static bool IsAvailable(string text)
        => !string.Equals(text, "Unavailable", StringComparison.OrdinalIgnoreCase);

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

    public sealed class ToolchainVersionsResponse
    {
        public string DotNetSdkVersion { get; set; } = "Unavailable";
        public string NuGetVersion { get; set; } = "Unavailable";
        public string NodeVersion { get; set; } = "Unavailable";
        public string MavenVersion { get; set; } = "Unavailable";
        public string PythonVersion { get; set; } = "Unavailable";
        public string PipVersion { get; set; } = "Unavailable";
        public string NuGetScannerTool { get; set; } = "Unavailable";
        public string NpmScannerTool { get; set; } = "Unavailable";
        public string MavenScannerTool { get; set; } = "Unavailable";
        public string PythonScannerTool { get; set; } = "Unavailable";
    }
}
