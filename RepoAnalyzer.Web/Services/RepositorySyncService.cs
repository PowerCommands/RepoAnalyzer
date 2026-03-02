using RepoAnalyzer.Web.Models;
using RepoAnalyzer.Web.Models.Enums;
using RepoAnalyzer.Web.Services.Analysis.Logging;
using RepoAnalyzer.Web.Services.Providers;

namespace RepoAnalyzer.Web.Services;

public sealed class RepositorySyncService
{
    private readonly AppDataService _data;
    private readonly ConnectionService _connectionService;
    private readonly GitProviderFactory _providerFactory;
    private readonly IAnalysisLog _analysisLog;

    public RepositorySyncService(
        AppDataService data,
        ConnectionService connectionService,
        GitProviderFactory providerFactory,
        IAnalysisLog analysisLog)
    {
        _data = data;
        _connectionService = connectionService;
        _providerFactory = providerFactory;
        _analysisLog = analysisLog;
    }

    public Task SyncConnectionAsync(string connectionId, CancellationToken ct = default)
    {
        // Kept for backward compatibility; behavior is add-only.
        return FetchNewRepositoriesAsync(connectionId, workspaceNames: null, ct);
    }

    public async Task<(int AddedWorkspaces, int AddedRepositories)> FetchNewRepositoriesAsync(string connectionId, IReadOnlyCollection<string>? workspaceNames = null, CancellationToken ct = default)
    {
        var runId = $"fetch-{Guid.NewGuid():N}";
        var connection = await _connectionService.GetRawByIdAsync(connectionId, ct)
            ?? throw new InvalidOperationException("Connection not found.");
        var context = new AnalysisLogContext
        {
            AnalysisRunId = runId,
            ConnectionId = connection.Id,
            ProviderType = connection.Type == ConnectionType.AzureDevOpsServer ? "ADS" : "GitHub"
        };

        await _analysisLog.InfoAsync(
            "FetchRepositories",
            "Starting repository fetch.",
            context,
            new Dictionary<string, object?>
            {
                ["connectionId"] = connection.Id,
                ["connectionName"] = connection.Name,
                ["providerType"] = context.ProviderType,
                ["target"] = connection.BaseUrlOrOrg,
                ["hasToken"] = !string.IsNullOrWhiteSpace(connection.EncryptedToken)
            },
            ct);

        var provider = _providerFactory.Resolve(connection.Type);
        var workspaces = await _data.GetWorkspacesAsync(ct);
        var repositories = await _data.GetRepositoriesAsync(ct);

        var addedWorkspaces = 0;
        var addedRepos = 0;

        var providerWorkspaces = await provider.GetWorkspacesAsync(connection, ct);
        var requestedWorkspaceNames = (workspaceNames ?? Array.Empty<string>())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (connection.Type == ConnectionType.AzureDevOpsServer && requestedWorkspaceNames.Count > 0)
        {
            var requestedSet = requestedWorkspaceNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
            providerWorkspaces = providerWorkspaces
                .Where(x => requestedSet.Contains(x.Name))
                .ToList();
        }
        await _analysisLog.InfoAsync(
            "FetchRepositories",
            "Provider workspaces fetched.",
            context,
            new Dictionary<string, object?>
            {
                ["workspaceCount"] = providerWorkspaces.Count,
                ["workspaceNames"] = providerWorkspaces.Select(x => x.Name).ToList(),
                ["requestedWorkspaceNames"] = requestedWorkspaceNames
            },
            ct);
        var workspaceMap = new Dictionary<string, Workspace>(StringComparer.OrdinalIgnoreCase);

        if (connection.Type == ConnectionType.GitHub)
        {
            var wsName = providerWorkspaces.FirstOrDefault()?.Name ?? "(GitHub)";
            var existing = workspaces.FirstOrDefault(w => w.ConnectionId == connectionId && string.Equals(w.Name, wsName, StringComparison.OrdinalIgnoreCase));
            if (existing is null)
            {
                existing = new Workspace { ConnectionId = connectionId, Name = wsName };
                workspaces.Add(existing);
                addedWorkspaces++;
            }

            workspaceMap[wsName] = existing;
        }
        else
        {
            foreach (var providerWorkspace in providerWorkspaces)
            {
                var existing = workspaces.FirstOrDefault(w => w.ConnectionId == connectionId && string.Equals(w.Name, providerWorkspace.Name, StringComparison.OrdinalIgnoreCase));
                if (existing is null)
                {
                    existing = new Workspace { ConnectionId = connectionId, Name = providerWorkspace.Name };
                    workspaces.Add(existing);
                    addedWorkspaces++;
                }

                workspaceMap[providerWorkspace.Name] = existing;
            }
        }

        foreach (var providerWorkspace in providerWorkspaces)
        {
            if (!workspaceMap.TryGetValue(providerWorkspace.Name, out var localWorkspace))
            {
                continue;
            }

            var providerRepos = await provider.GetRepositoriesAsync(connection, localWorkspace, ct);
            await _analysisLog.InfoAsync(
                "FetchRepositories",
                "Provider repositories fetched for workspace.",
                context,
                new Dictionary<string, object?>
                {
                    ["workspaceId"] = localWorkspace.Id,
                    ["workspaceName"] = localWorkspace.Name,
                    ["repositoryCount"] = providerRepos.Count,
                    ["repositoryNames"] = providerRepos.Select(x => x.Name).Take(50).ToList(),
                    ["repositoryDiagnostics"] = providerRepos
                        .Take(25)
                        .Select(x => new Dictionary<string, object?>
                        {
                            ["name"] = x.Name,
                            ["url"] = x.Url,
                            ["looksLikeFallback"] = LooksLikeFallbackRepository(x)
                        })
                        .ToList()
                },
                ct);

            var fallbackLikeCount = providerRepos.Count(LooksLikeFallbackRepository);
            if (fallbackLikeCount > 0)
            {
                await _analysisLog.WarningAsync(
                    "FetchRepositories",
                    "One or more repositories look like provider fallback data.",
                    context,
                    new Dictionary<string, object?>
                    {
                        ["workspaceId"] = localWorkspace.Id,
                        ["workspaceName"] = localWorkspace.Name,
                        ["fallbackLikeCount"] = fallbackLikeCount,
                        ["fallbackLikeRepositories"] = providerRepos
                            .Where(LooksLikeFallbackRepository)
                            .Take(25)
                            .Select(x => new Dictionary<string, object?>
                            {
                                ["name"] = x.Name,
                                ["url"] = x.Url
                            })
                            .ToList()
                    },
                    ct);
            }

            foreach (var providerRepo in providerRepos)
            {
                var exists = repositories.Any(r =>
                    r.ConnectionId == connectionId &&
                    r.WorkspaceId == localWorkspace.Id &&
                    string.Equals(r.Name, providerRepo.Name, StringComparison.OrdinalIgnoreCase));

                if (exists)
                {
                    continue;
                }

                repositories.Add(new RepositoryEntity
                {
                    ConnectionId = connectionId,
                    WorkspaceId = localWorkspace.Id,
                    Name = providerRepo.Name,
                    Url = providerRepo.Url
                });
                addedRepos++;
            }
        }

        await _data.SaveWorkspacesAsync(workspaces, ct);
        await _data.SaveRepositoriesAsync(repositories, ct);

        await _analysisLog.InfoAsync(
            "FetchRepositories",
            "Repository fetch completed.",
            context,
            new Dictionary<string, object?>
            {
                ["addedWorkspaces"] = addedWorkspaces,
                ["addedRepositories"] = addedRepos,
                ["totalLocalRepositories"] = repositories.Count(x => x.ConnectionId == connectionId)
            },
            ct);

        return (addedWorkspaces, addedRepos);
    }

    public async Task<List<string>> GetProviderWorkspaceNamesPreviewAsync(string connectionId, CancellationToken ct = default)
    {
        var connection = await _connectionService.GetRawByIdAsync(connectionId, ct)
            ?? throw new InvalidOperationException("Connection not found.");

        var provider = _providerFactory.Resolve(connection.Type);
        var workspaces = await provider.GetWorkspacesAsync(connection, ct);
        return workspaces
            .Select(x => x.Name)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool LooksLikeFallbackRepository(RepositoryEntity repository)
    {
        if (string.Equals(repository.Name, "sample-repo", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return !string.IsNullOrWhiteSpace(repository.Url) &&
               repository.Url.Contains("example.local", StringComparison.OrdinalIgnoreCase);
    }
}
