using RepoAnalyzer.Web.Dto;
using RepoAnalyzer.Web.Models;

namespace RepoAnalyzer.Web.Services;

public sealed class ConnectionService
{
    private readonly AppDataService _data;
    private readonly TokenProtector _tokenProtector;

    public ConnectionService(AppDataService data, TokenProtector tokenProtector)
    {
        _data = data;
        _tokenProtector = tokenProtector;
    }

    public async Task<List<ConnectionView>> GetAllAsync(CancellationToken ct = default)
    {
        var list = await _data.GetConnectionsAsync(ct);
        return list.Select(ToView).OrderBy(x => x.Name).ToList();
    }

    public async Task<Connection?> GetRawByIdAsync(string id, CancellationToken ct = default)
    {
        var list = await _data.GetConnectionsAsync(ct);
        return list.FirstOrDefault(x => x.Id == id);
    }

    public async Task<ConnectionView> UpsertAsync(ConnectionUpsertRequest request, CancellationToken ct = default)
    {
        var list = await _data.GetConnectionsAsync(ct);
        Connection? entity = null;

        if (!string.IsNullOrWhiteSpace(request.Id))
        {
            entity = list.FirstOrDefault(x => x.Id == request.Id);
        }

        if (entity is null)
        {
            entity = new Connection();
            list.Add(entity);
        }

        entity.Name = request.Name.Trim();
        entity.Type = request.Type;
        entity.BaseUrlOrOrg = request.BaseUrlOrOrg.Trim();
        entity.TokenExpiresAt = request.TokenExpiresAt;

        if (!string.IsNullOrWhiteSpace(request.RawToken))
        {
            entity.EncryptedToken = _tokenProtector.Protect(request.RawToken.Trim());
        }

        await _data.SaveConnectionsAsync(list, ct);
        return ToView(entity);
    }

    public async Task DeleteAsync(string id, CancellationToken ct = default)
    {
        var connections = await _data.GetConnectionsAsync(ct);
        var workspaces = await _data.GetWorkspacesAsync(ct);
        var repositories = await _data.GetRepositoriesAsync(ct);
        var snapshots = await _data.GetSnapshotsAsync(ct);
        var globalFindings = await _data.GetGlobalFindingsAsync(ct);

        connections.RemoveAll(x => x.Id == id);

        var repoIds = repositories.Where(r => r.ConnectionId == id).Select(r => r.Id).ToHashSet(StringComparer.Ordinal);

        workspaces.RemoveAll(x => x.ConnectionId == id);
        repositories.RemoveAll(x => x.ConnectionId == id);
        snapshots.RemoveAll(x => repoIds.Contains(x.RepositoryId));
        foreach (var global in globalFindings)
        {
            global.AffectedLocations.RemoveAll(x => repoIds.Contains(x.RepositoryId));
        }
        globalFindings.RemoveAll(x => x.AffectedLocations.Count == 0);

        await _data.SaveConnectionsAsync(connections, ct);
        await _data.SaveWorkspacesAsync(workspaces, ct);
        await _data.SaveRepositoriesAsync(repositories, ct);
        await _data.SaveSnapshotsAsync(snapshots, ct);
        await _data.SaveGlobalFindingsAsync(globalFindings, ct);
    }

    public async Task DeleteRepositoryAsync(string repositoryId, CancellationToken ct = default)
    {
        var workspaces = await _data.GetWorkspacesAsync(ct);
        var repositories = await _data.GetRepositoriesAsync(ct);
        var snapshots = await _data.GetSnapshotsAsync(ct);
        var globalFindings = await _data.GetGlobalFindingsAsync(ct);

        var repo = repositories.FirstOrDefault(x => x.Id == repositoryId);
        if (repo is null)
        {
            return;
        }

        repositories.RemoveAll(x => x.Id == repositoryId);
        snapshots.RemoveAll(x => x.RepositoryId == repositoryId);

        foreach (var global in globalFindings)
        {
            global.AffectedLocations.RemoveAll(x => x.RepositoryId == repositoryId);
        }
        globalFindings.RemoveAll(x => x.AffectedLocations.Count == 0);

        if (!string.IsNullOrWhiteSpace(repo.WorkspaceId))
        {
            var hasReposInWorkspace = repositories.Any(x => x.ConnectionId == repo.ConnectionId && x.WorkspaceId == repo.WorkspaceId);
            if (!hasReposInWorkspace)
            {
                workspaces.RemoveAll(x => x.ConnectionId == repo.ConnectionId && x.Id == repo.WorkspaceId);
            }
        }

        await _data.SaveWorkspacesAsync(workspaces, ct);
        await _data.SaveRepositoriesAsync(repositories, ct);
        await _data.SaveSnapshotsAsync(snapshots, ct);
        await _data.SaveGlobalFindingsAsync(globalFindings, ct);
    }

    public string GetRawToken(Connection connection) => _tokenProtector.Unprotect(connection.EncryptedToken);

    private static ConnectionView ToView(Connection c) => new()
    {
        Id = c.Id,
        Name = c.Name,
        Type = c.Type,
        BaseUrlOrOrg = c.BaseUrlOrOrg,
        TokenExpiresAt = c.TokenExpiresAt,
        HasToken = !string.IsNullOrWhiteSpace(c.EncryptedToken)
    };
}
