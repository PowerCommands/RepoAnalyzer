using RepoAnalyzer.Web.Dto;
using RepoAnalyzer.Web.Services.Analysis;
using RepoAnalyzer.Web.Services.Analysis.Logging;

namespace RepoAnalyzer.Web.Services;

public static class InternalApiEndpoints
{
    public static IEndpointRouteBuilder MapInternalApi(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/internal-api");

        group.MapGet("/connections", async (ConnectionService service, CancellationToken ct) =>
            Results.Ok(await service.GetAllAsync(ct)));

        group.MapPost("/connections", async (ConnectionUpsertRequest request, ConnectionService service, CancellationToken ct) =>
            Results.Ok(await service.UpsertAsync(request, ct)));

        group.MapDelete("/connections/{id}", async (string id, ConnectionService service, CancellationToken ct) =>
        {
            await service.DeleteAsync(id, ct);
            return Results.NoContent();
        });

        group.MapPost("/connections/{id}/sync", async (string id, RepositorySyncService sync, CancellationToken ct) =>
        {
            await sync.SyncConnectionAsync(id, ct);
            return Results.Ok();
        });

        group.MapGet("/connections/{id}/workspaces/preview", async (string id, RepositorySyncService sync, CancellationToken ct) =>
            Results.Ok(await sync.GetProviderWorkspaceNamesPreviewAsync(id, ct)));

        group.MapPost("/connections/{id}/fetch-repositories", async (string id, FetchRepositoriesRequest? request, RepositorySyncService sync, CancellationToken ct) =>
        {
            var result = await sync.FetchNewRepositoriesAsync(id, request?.WorkspaceNames, ct);
            return Results.Ok(new
            {
                AddedWorkspaces = result.AddedWorkspaces,
                AddedRepositories = result.AddedRepositories
            });
        });

        group.MapPost("/connections/{id}/test", async (string id, ConnectionValidationService service, CancellationToken ct) =>
            Results.Ok(await service.TestConnectionAsync(id, ct)));

        group.MapGet("/workspaces", async (QueryService service, CancellationToken ct) => Results.Ok(await service.GetWorkspacesAsync(ct)));
        group.MapGet("/repositories", async (QueryService service, CancellationToken ct) => Results.Ok(await service.GetRepositoriesAsync(ct)));
        group.MapDelete("/repositories/{id}", async (string id, ConnectionService service, CancellationToken ct) =>
        {
            await service.DeleteRepositoryAsync(id, ct);
            return Results.NoContent();
        });
        group.MapGet("/snapshots", async (QueryService service, CancellationToken ct) => Results.Ok(await service.GetSnapshotsAsync(ct)));

        group.MapPost("/analyze", async (AnalyzeRepositoryRequest request, RepositoryAnalyzerService service, CancellationToken ct) =>
            Results.Ok(await service.AnalyzeRepositoryAsync(request.RepositoryId, analysisRunId: null, progress: null, ct)));

        group.MapPost("/analysis/start", (AnalyzeStartRequest request, AnalyzeRunService runs) =>
            Results.Ok(runs.Start(request.RepositoryId)));

        group.MapGet("/analysis/runs/recent", (int? take, AnalyzeRunService runs) =>
            Results.Ok(runs.GetRecent(take.GetValueOrDefault(20))));

        group.MapGet("/analysis/runs/{runId}", (string runId, AnalyzeRunService runs) =>
        {
            var run = runs.Get(runId);
            return run is null ? Results.NotFound() : Results.Ok(run);
        });

        group.MapDelete("/analysis/{repositoryId}", async (string repositoryId, RepositoryAnalyzerService analyzer, CancellationToken ct) =>
        {
            await analyzer.ClearRepositoryAnalysisAsync(repositoryId, ct);
            return Results.NoContent();
        });

        group.MapGet("/components", async (string? name, string? repositoryId, int? take, QueryService service, CancellationToken ct) =>
            Results.Ok(await service.GetLatestComponentsAsync(name, repositoryId, take, ct)));

        group.MapGet("/findings", async (string? ecosystem, string? severity, string? repositoryId, QueryService service, CancellationToken ct) =>
            Results.Ok(await service.GetGlobalFindingsAsync(ecosystem, severity, repositoryId, ct)));

        group.MapGet("/tools/logs/latest", async (int? lines, IAnalysisLog logs, CancellationToken ct) =>
        {
            var lineCount = lines.GetValueOrDefault(2000);
            var result = new AnalysisLogLatestResponse
            {
                RequestedLines = lineCount,
                Lines = await logs.ReadLatestLinesAsync(lineCount, ct)
            };
            result.ReturnedLines = result.Lines.Count;
            return Results.Ok(result);
        });

        group.MapGet("/tools/logs/download", (IAnalysisLog logs) =>
        {
            var path = logs.GetCurrentLogFilePath();
            if (!File.Exists(path))
            {
                File.WriteAllText(path, string.Empty);
            }

            return Results.File(path, "text/plain", "analysis.log");
        });

        group.MapPost("/tools/logs/clear", async (IAnalysisLog logs, CancellationToken ct) =>
        {
            await logs.ClearAsync(ct);
            return Results.Ok();
        });

        group.MapGet("/tools/backup/download", async (BackupService backup, CancellationToken ct) =>
        {
            var result = await backup.CreateBackupZipAsync(ct);
            return Results.File(result.Stream, "application/zip", result.FileName);
        });

        group.MapPost("/tools/backup/restore", async (HttpRequest request, BackupService backup, CancellationToken ct) =>
        {
            if (!request.HasFormContentType)
            {
                return Results.BadRequest("Expected multipart form data.");
            }

            var form = await request.ReadFormAsync(ct);
            var file = form.Files["file"] ?? form.Files.FirstOrDefault();
            if (file is null || file.Length == 0)
            {
                return Results.BadRequest("Backup file is required.");
            }

            await using var stream = file.OpenReadStream();
            var restored = await backup.RestoreZipAsync(stream, ct);
            return Results.Ok(restored);
        });

        group.MapGet("/tools/storage/stats", (IConfiguration configuration) =>
        {
            var dataPath = configuration["DataPath"] ?? "/app/data";
            Directory.CreateDirectory(dataPath);

            var jsonFiles = Directory.GetFiles(dataPath, "*.json", SearchOption.TopDirectoryOnly);
            var totalBytes = jsonFiles
                .Select(path => new FileInfo(path))
                .Sum(file => file.Exists ? file.Length : 0L);

            var response = new DataStorageStatsResponse
            {
                JsonFileCount = jsonFiles.Length,
                TotalJsonBytes = totalBytes
            };

            return Results.Ok(response);
        });

        return app;
    }
}
