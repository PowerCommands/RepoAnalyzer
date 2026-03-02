using RepoAnalyzer.Web.Models;
using RepoAnalyzer.Web.Services.Storage;

namespace RepoAnalyzer.Web.Services;

public sealed class AppDataService
{
    private const string ConnectionsFile = "connections.json";
    private const string WorkspacesFile = "workspaces.json";
    private const string RepositoriesFile = "repositories.json";
    private const string SnapshotsFile = "snapshots.json";
    private const string GlobalFindingsFile = "globalFindings.json";

    private readonly JsonFileStore _store;

    public AppDataService(JsonFileStore store)
    {
        _store = store;
    }

    public Task<List<Connection>> GetConnectionsAsync(CancellationToken ct = default) => _store.ReadListAsync<Connection>(ConnectionsFile, ct);
    public Task SaveConnectionsAsync(List<Connection> data, CancellationToken ct = default) => _store.WriteListAsync(ConnectionsFile, data, ct);

    public Task<List<Workspace>> GetWorkspacesAsync(CancellationToken ct = default) => _store.ReadListAsync<Workspace>(WorkspacesFile, ct);
    public Task SaveWorkspacesAsync(List<Workspace> data, CancellationToken ct = default) => _store.WriteListAsync(WorkspacesFile, data, ct);

    public Task<List<RepositoryEntity>> GetRepositoriesAsync(CancellationToken ct = default) => _store.ReadListAsync<RepositoryEntity>(RepositoriesFile, ct);
    public Task SaveRepositoriesAsync(List<RepositoryEntity> data, CancellationToken ct = default) => _store.WriteListAsync(RepositoriesFile, data, ct);

    public Task<List<RepoAnalysisSnapshot>> GetSnapshotsAsync(CancellationToken ct = default) => _store.ReadListAsync<RepoAnalysisSnapshot>(SnapshotsFile, ct);
    public Task SaveSnapshotsAsync(List<RepoAnalysisSnapshot> data, CancellationToken ct = default) => _store.WriteListAsync(SnapshotsFile, data, ct);

    public Task<List<GlobalFinding>> GetGlobalFindingsAsync(CancellationToken ct = default) => _store.ReadListAsync<GlobalFinding>(GlobalFindingsFile, ct);
    public Task SaveGlobalFindingsAsync(List<GlobalFinding> data, CancellationToken ct = default) => _store.WriteListAsync(GlobalFindingsFile, data, ct);
}
