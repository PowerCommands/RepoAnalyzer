using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using RepoAnalyzer.Web.Models;

namespace RepoAnalyzer.Web.Services.Providers;

public sealed class AzureDevOpsServerProvider : IGitProvider
{
    private readonly HttpClient _httpClient;
    private readonly ConnectionService _connectionService;
    private readonly ILogger<AzureDevOpsServerProvider> _logger;

    public AzureDevOpsServerProvider(IHttpClientFactory httpClientFactory, ConnectionService connectionService, ILogger<AzureDevOpsServerProvider> logger)
    {
        _httpClient = httpClientFactory.CreateClient(nameof(AzureDevOpsServerProvider));
        _connectionService = connectionService;
        _logger = logger;
    }

    public async Task<IReadOnlyList<Workspace>> GetWorkspacesAsync(Connection connection, CancellationToken ct = default)
    {
        var token = _connectionService.GetRawToken(connection);

        if (string.IsNullOrWhiteSpace(token))
        {
            return BuildStubWorkspaces(connection);
        }

        var request = new HttpRequestMessage(HttpMethod.Get, $"{connection.BaseUrlOrOrg.TrimEnd('/')}/_apis/projects?api-version=7.0");
        request.Headers.Authorization = BuildBasicAuth(token);

        try
        {
            using var response = await _httpClient.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                return BuildStubWorkspaces(connection);
            }

            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

            var workspaces = doc.RootElement.GetProperty("value")
                .EnumerateArray()
                .Select(x => new Workspace
                {
                    ConnectionId = connection.Id,
                    Name = x.GetProperty("name").GetString() ?? "Workspace"
                })
                .ToList();

            return workspaces.Count > 0 ? workspaces : BuildStubWorkspaces(connection);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Azure DevOps workspace fetch failed for connection {ConnectionId}", connection.Id);
            return BuildStubWorkspaces(connection);
        }
    }

    public async Task<(bool Success, string Message)> TestConnectionAsync(Connection connection, CancellationToken ct = default)
    {
        var token = _connectionService.GetRawToken(connection);
        if (string.IsNullOrWhiteSpace(token))
        {
            return (false, "Token is missing.");
        }

        var request = new HttpRequestMessage(HttpMethod.Get, $"{connection.BaseUrlOrOrg.TrimEnd('/')}/_apis/projects?$top=1&api-version=7.0");
        request.Headers.Authorization = BuildBasicAuth(token);

        try
        {
            using var response = await _httpClient.SendAsync(request, ct);
            if (response.IsSuccessStatusCode)
            {
                return (true, "Connection successful.");
            }

            return (false, $"Connection failed: HTTP {(int)response.StatusCode}.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Azure DevOps test connection failed for connection {ConnectionId}", connection.Id);
            return (false, "Connection failed: request error.");
        }
    }

    public async Task<IReadOnlyList<RepositoryEntity>> GetRepositoriesAsync(Connection connection, Workspace? workspace, CancellationToken ct = default)
    {
        if (workspace is null)
        {
            return new List<RepositoryEntity>();
        }

        var token = _connectionService.GetRawToken(connection);
        if (string.IsNullOrWhiteSpace(token))
        {
            return BuildStubRepositories(connection, workspace);
        }

        var request = new HttpRequestMessage(HttpMethod.Get,
            $"{connection.BaseUrlOrOrg.TrimEnd('/')}/{Uri.EscapeDataString(workspace.Name)}/_apis/git/repositories?api-version=7.0");
        request.Headers.Authorization = BuildBasicAuth(token);

        try
        {
            using var response = await _httpClient.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                return BuildStubRepositories(connection, workspace);
            }

            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

            var repos = doc.RootElement.GetProperty("value")
                .EnumerateArray()
                .Select(x => new RepositoryEntity
                {
                    ConnectionId = connection.Id,
                    WorkspaceId = workspace.Id,
                    Name = x.GetProperty("name").GetString() ?? "repo",
                    Url = x.GetProperty("webUrl").GetString() ?? string.Empty
                })
                .ToList();

            return repos.Count > 0 ? repos : BuildStubRepositories(connection, workspace);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Azure DevOps repository fetch failed for workspace {Workspace}", workspace.Name);
            return BuildStubRepositories(connection, workspace);
        }
    }

    public Task<IReadOnlyList<RepoFile>> GetRepositoryFilesAsync(Connection connection, RepositoryEntity repository, CancellationToken ct = default)
    {
        // TODO: Pull manifest files through Azure DevOps Git item APIs. For MVP we return a safe sample manifest set.
        IReadOnlyList<RepoFile> files =
        [
            new RepoFile
            {
                Path = "src/AdsSample/AdsSample.csproj",
                Content = "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net6.0</TargetFramework></PropertyGroup><ItemGroup><PackageReference Include=\"Serilog\" Version=\"2.10.0\" /></ItemGroup></Project>"
            }
        ];

        return Task.FromResult(files);
    }

    private static List<Workspace> BuildStubWorkspaces(Connection connection)
    {
        return
        [
            new Workspace { ConnectionId = connection.Id, Name = "Default Workspace" }
        ];
    }

    private static List<RepositoryEntity> BuildStubRepositories(Connection connection, Workspace workspace)
    {
        return
        [
            new RepositoryEntity
            {
                ConnectionId = connection.Id,
                WorkspaceId = workspace.Id,
                Name = "sample-repo",
                Url = "https://example.local/ads/sample-repo"
            }
        ];
    }

    private static AuthenticationHeaderValue BuildBasicAuth(string token)
    {
        var bytes = Encoding.ASCII.GetBytes($":{token}");
        return new AuthenticationHeaderValue("Basic", Convert.ToBase64String(bytes));
    }
}
