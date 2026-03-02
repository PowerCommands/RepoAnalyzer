using RepoAnalyzer.Web.Models;

namespace RepoAnalyzer.Web.Services.Providers;

public interface IGitProvider
{
    Task<(bool Success, string Message)> TestConnectionAsync(Connection connection, CancellationToken ct = default);
    Task<IReadOnlyList<Workspace>> GetWorkspacesAsync(Connection connection, CancellationToken ct = default);
    Task<IReadOnlyList<RepositoryEntity>> GetRepositoriesAsync(Connection connection, Workspace? workspace, CancellationToken ct = default);
    Task<IReadOnlyList<RepoFile>> GetRepositoryFilesAsync(Connection connection, RepositoryEntity repository, CancellationToken ct = default);
}
