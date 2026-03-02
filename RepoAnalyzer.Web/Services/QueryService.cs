using RepoAnalyzer.Web.Models;

namespace RepoAnalyzer.Web.Services;

public sealed class QueryService
{
    private readonly AppDataService _data;

    public QueryService(AppDataService data)
    {
        _data = data;
    }

    public async Task<List<RepositoryEntity>> GetRepositoriesAsync(CancellationToken ct = default)
    {
        var repos = await _data.GetRepositoriesAsync(ct);
        return repos.OrderBy(r => r.Name).ToList();
    }

    public async Task<List<Workspace>> GetWorkspacesAsync(CancellationToken ct = default)
    {
        var workspaces = await _data.GetWorkspacesAsync(ct);
        return workspaces.OrderBy(w => w.Name).ToList();
    }

    public async Task<List<RepoAnalysisSnapshot>> GetSnapshotsAsync(CancellationToken ct = default)
    {
        var snapshots = await _data.GetSnapshotsAsync(ct);
        return snapshots.OrderByDescending(s => s.AnalyzedAtUtc).ToList();
    }

    public async Task<RepoAnalysisSnapshot?> GetLatestSnapshotForRepoAsync(string repositoryId, CancellationToken ct = default)
    {
        var snapshots = await _data.GetSnapshotsAsync(ct);
        return snapshots.FirstOrDefault(x => x.RepositoryId == repositoryId);
    }

    public async Task<List<Component>> GetLatestComponentsAsync(string? nameFilter, string? repositoryId, int? take = null, CancellationToken ct = default)
    {
        var snapshots = await GetSnapshotsAsync(ct);
        var components = snapshots.SelectMany(s => s.Components)
            .OrderByDescending(c => c.CapturedAtUtc)
            .ThenBy(c => c.Name)
            .ToList();

        if (!string.IsNullOrWhiteSpace(repositoryId))
        {
            components = components.Where(c => c.RepositoryId == repositoryId).ToList();
        }

        if (!string.IsNullOrWhiteSpace(nameFilter))
        {
            components = components
                .Where(c => c.Name.Contains(nameFilter, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        if (take.HasValue && take.Value > 0)
        {
            return components.Take(take.Value).ToList();
        }

        return components;
    }

    public async Task<List<GlobalFinding>> GetGlobalFindingsAsync(string? ecosystem, string? severity, string? repositoryId, CancellationToken ct = default)
    {
        var findings = await _data.GetGlobalFindingsAsync(ct);

        if (!string.IsNullOrWhiteSpace(ecosystem))
        {
            findings = findings.Where(x => string.Equals(x.Ecosystem, ecosystem, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        if (!string.IsNullOrWhiteSpace(severity))
        {
            findings = findings.Where(x => string.Equals(x.Severity, severity, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        if (!string.IsNullOrWhiteSpace(repositoryId))
        {
            findings = findings.Where(x => x.AffectedLocations.Any(a => a.RepositoryId == repositoryId)).ToList();
        }

        return findings.OrderByDescending(x => x.AnalyzedAtUtc).ThenBy(x => x.PackageName).ToList();
    }
}
