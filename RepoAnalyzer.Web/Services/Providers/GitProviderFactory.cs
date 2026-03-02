using RepoAnalyzer.Web.Models.Enums;

namespace RepoAnalyzer.Web.Services.Providers;

public sealed class GitProviderFactory
{
    private readonly AzureDevOpsServerProvider _azure;
    private readonly GitHubProvider _gitHub;

    public GitProviderFactory(AzureDevOpsServerProvider azure, GitHubProvider gitHub)
    {
        _azure = azure;
        _gitHub = gitHub;
    }

    public IGitProvider Resolve(ConnectionType type) => type switch
    {
        ConnectionType.AzureDevOpsServer => _azure,
        ConnectionType.GitHub => _gitHub,
        _ => throw new NotSupportedException($"Unsupported provider type: {type}")
    };
}
