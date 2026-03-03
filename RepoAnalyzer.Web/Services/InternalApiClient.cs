using System.Net.Http.Json;
using System.Net.Http.Headers;
using RepoAnalyzer.Web.Dto;
using RepoAnalyzer.Web.Models;

namespace RepoAnalyzer.Web.Services;

public sealed class InternalApiClient
{
    private readonly IHttpClientFactory _factory;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly IConfiguration _configuration;

    public InternalApiClient(IHttpClientFactory factory, IHttpContextAccessor httpContextAccessor, IConfiguration configuration)
    {
        _factory = factory;
        _httpContextAccessor = httpContextAccessor;
        _configuration = configuration;
    }

    public async Task<List<ConnectionView>> GetConnectionsAsync(CancellationToken ct = default)
        => await GetAsync<List<ConnectionView>>("/internal-api/connections", ct) ?? new List<ConnectionView>();

    public async Task<ConnectionView?> SaveConnectionAsync(ConnectionUpsertRequest request, CancellationToken ct = default)
        => await PostAsync<ConnectionUpsertRequest, ConnectionView>("/internal-api/connections", request, ct);

    public async Task<ConnectionTestResult?> TestConnectionAsync(string id, CancellationToken ct = default)
        => await PostAsync<object, ConnectionTestResult>($"/internal-api/connections/{id}/test", new { }, ct);

    public async Task DeleteConnectionAsync(string id, CancellationToken ct = default)
        => await DeleteAsync($"/internal-api/connections/{id}", ct);

    public async Task SyncConnectionAsync(string id, CancellationToken ct = default)
        => await PostAsync<object, object>($"/internal-api/connections/{id}/sync", new { }, ct);

    public async Task<List<string>> GetProviderWorkspaceNamesPreviewAsync(string id, CancellationToken ct = default)
        => await GetAsync<List<string>>($"/internal-api/connections/{id}/workspaces/preview", ct) ?? new List<string>();

    public async Task<FetchRepositoriesResult?> FetchRepositoriesAsync(string id, IEnumerable<string>? workspaceNames = null, CancellationToken ct = default)
    {
        var request = new FetchRepositoriesRequest
        {
            WorkspaceNames = workspaceNames?.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
        };

        return await PostAsync<FetchRepositoriesRequest, FetchRepositoriesResult>($"/internal-api/connections/{id}/fetch-repositories", request, ct);
    }

    public async Task<List<Workspace>> GetWorkspacesAsync(CancellationToken ct = default)
        => await GetAsync<List<Workspace>>("/internal-api/workspaces", ct) ?? new List<Workspace>();

    public async Task<List<RepositoryEntity>> GetRepositoriesAsync(CancellationToken ct = default)
        => await GetAsync<List<RepositoryEntity>>("/internal-api/repositories", ct) ?? new List<RepositoryEntity>();

    public async Task DeleteRepositoryAsync(string id, CancellationToken ct = default)
        => await DeleteAsync($"/internal-api/repositories/{id}", ct);

    public async Task<RepoAnalysisSnapshot?> AnalyzeAsync(string repositoryId, CancellationToken ct = default)
        => await PostAsync<AnalyzeRepositoryRequest, RepoAnalysisSnapshot>("/internal-api/analyze", new AnalyzeRepositoryRequest { RepositoryId = repositoryId }, ct);

    public async Task<AnalyzeRun?> StartAnalysisAsync(string repositoryId, CancellationToken ct = default)
        => await PostAsync<AnalyzeStartRequest, AnalyzeRun>("/internal-api/analysis/start", new AnalyzeStartRequest { RepositoryId = repositoryId }, ct);

    public async Task<AnalyzeRun?> GetAnalysisRunAsync(string runId, CancellationToken ct = default)
        => await GetAsync<AnalyzeRun>($"/internal-api/analysis/runs/{runId}", ct);

    public async Task<List<AnalyzeRun>> GetRecentAnalysisRunsAsync(int take = 20, CancellationToken ct = default)
        => await GetAsync<List<AnalyzeRun>>($"/internal-api/analysis/runs/recent?take={take}", ct) ?? new List<AnalyzeRun>();

    public async Task<List<RepoAnalysisSnapshot>> GetSnapshotsAsync(CancellationToken ct = default)
        => await GetAsync<List<RepoAnalysisSnapshot>>("/internal-api/snapshots", ct) ?? new List<RepoAnalysisSnapshot>();

    public async Task<List<Component>> GetComponentsAsync(string? nameFilter, string? repositoryId, int? take = null, CancellationToken ct = default)
    {
        var queryParts = new List<string>
        {
            $"name={Uri.EscapeDataString(nameFilter ?? string.Empty)}",
            $"repositoryId={Uri.EscapeDataString(repositoryId ?? string.Empty)}"
        };

        if (take.HasValue)
        {
            queryParts.Add($"take={take.Value}");
        }

        var query = string.Join("&", queryParts);
        return await GetAsync<List<Component>>($"/internal-api/components?{query}", ct) ?? new List<Component>();
    }

    public async Task DeleteAnalysisAsync(string repositoryId, CancellationToken ct = default)
        => await DeleteAsync($"/internal-api/analysis/{repositoryId}", ct);

    public async Task<List<GlobalFinding>> GetFindingsAsync(string? ecosystem, string? severity, string? repositoryId, CancellationToken ct = default)
    {
        var query = $"ecosystem={Uri.EscapeDataString(ecosystem ?? string.Empty)}&severity={Uri.EscapeDataString(severity ?? string.Empty)}&repositoryId={Uri.EscapeDataString(repositoryId ?? string.Empty)}";
        return await GetAsync<List<GlobalFinding>>($"/internal-api/findings?{query}", ct) ?? new List<GlobalFinding>();
    }

    public async Task<AnalysisLogLatestResponse> GetLatestAnalysisLogsAsync(int lines = 2000, CancellationToken ct = default)
        => await GetAsync<AnalysisLogLatestResponse>($"/internal-api/tools/logs/latest?lines={lines}", ct) ?? new AnalysisLogLatestResponse();

    public async Task ClearAnalysisLogsAsync(CancellationToken ct = default)
        => await PostAsync<object, object>("/internal-api/tools/logs/clear", new { }, ct);

    public string GetAnalysisLogDownloadUrl() => "/internal-api/tools/logs/download";

    public async Task<BackupDownloadResult> DownloadBackupAsync(CancellationToken ct = default)
    {
        var client = BuildClient();
        using var response = await client.GetAsync("/internal-api/tools/backup/download", ct);
        response.EnsureSuccessStatusCode();

        var bytes = await response.Content.ReadAsByteArrayAsync(ct);
        var fileName = GetFileName(response.Content.Headers.ContentDisposition) ?? $"repo-analyzer-backup-{DateTime.UtcNow:yyyyMMdd-HHmmss}.zip";

        return new BackupDownloadResult
        {
            FileName = fileName,
            Bytes = bytes
        };
    }

    public async Task<BackupRestoreResult?> RestoreBackupAsync(string fileName, Stream stream, CancellationToken ct = default)
    {
        var client = BuildClient();
        using var content = new MultipartFormDataContent();
        using var fileContent = new StreamContent(stream);
        fileContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/zip");
        content.Add(fileContent, "file", fileName);

        using var response = await client.PostAsync("/internal-api/tools/backup/restore", content, ct);
        response.EnsureSuccessStatusCode();

        return await response.Content.ReadFromJsonAsync<BackupRestoreResult>(cancellationToken: ct);
    }

    public async Task<DataStorageStatsResponse> GetDataStorageStatsAsync(CancellationToken ct = default)
        => await GetAsync<DataStorageStatsResponse>("/internal-api/tools/storage/stats", ct) ?? new DataStorageStatsResponse();

    private static string? GetFileName(ContentDispositionHeaderValue? header)
    {
        var value = header?.FileNameStar ?? header?.FileName;
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return value.Trim('"');
    }

    private async Task<T?> GetAsync<T>(string relativeUrl, CancellationToken ct)
    {
        var client = BuildClient();
        return await client.GetFromJsonAsync<T>(relativeUrl, ct);
    }

    private async Task<TOut?> PostAsync<TIn, TOut>(string relativeUrl, TIn payload, CancellationToken ct)
    {
        var client = BuildClient();
        using var response = await client.PostAsJsonAsync(relativeUrl, payload, ct);
        response.EnsureSuccessStatusCode();

        if (response.Content.Headers.ContentLength == 0)
        {
            return default;
        }

        return await response.Content.ReadFromJsonAsync<TOut>(cancellationToken: ct);
    }

    private async Task DeleteAsync(string relativeUrl, CancellationToken ct)
    {
        var client = BuildClient();
        using var response = await client.DeleteAsync(relativeUrl, ct);
        response.EnsureSuccessStatusCode();
    }

    private HttpClient BuildClient()
    {
        var request = _httpContextAccessor.HttpContext?.Request;

        string baseUri;
        if (request is not null)
        {
            baseUri = $"{request.Scheme}://{request.Host}";
        }
        else
        {
            var aspnetcoreUrls = Environment.GetEnvironmentVariable("ASPNETCORE_URLS")
                                ?? _configuration["ASPNETCORE_URLS"]
                                ?? "http://127.0.0.1:8080";

            baseUri = aspnetcoreUrls.Split(';', StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault(x => x.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
                ?? "http://127.0.0.1:8080";
        }

        var client = _factory.CreateClient(nameof(InternalApiClient));
        client.BaseAddress = new Uri(baseUri);
        return client;
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
