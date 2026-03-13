using RepoAnalyzer.Web.Dto;
using RepoAnalyzer.Web.Models.Enums;
using RepoAnalyzer.Web.Services.Analysis;
using RepoAnalyzer.Web.Services.Analysis.Logging;
using RepoAnalyzer.Web.Services.Feeds;

namespace RepoAnalyzer.Web.Services;

public static class InternalApiEndpoints
{
    public static IEndpointRouteBuilder MapInternalApi(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/internal-api");
        var feedApi = app.MapGroup("/api/feeds");

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

        group.MapPost("/tools/sbom/create", async (SbomCreateRequest request, SbomService sbom, CancellationToken ct) =>
            Results.Ok(await sbom.CreateAsync(request, ct)));

        group.MapGet("/tools/sbom", async (string? connectionId, string? repositoryId, SbomService sbom, CancellationToken ct) =>
            Results.Ok(await sbom.ListAsync(connectionId, repositoryId, ct)));

        group.MapGet("/tools/sbom/{id}/download", async (string id, SbomService sbom, CancellationToken ct) =>
        {
            var result = await sbom.ReadFileAsync(id, ct);
            if (result is null)
            {
                return Results.NotFound();
            }

            var contentType = string.Equals(result.Value.Meta.Format, "XML", StringComparison.OrdinalIgnoreCase)
                ? "application/xml"
                : "application/json";

            return Results.File(result.Value.Bytes, contentType, result.Value.Meta.FileName);
        });

        group.MapDelete("/tools/sbom/{id}", async (string id, SbomService sbom, CancellationToken ct) =>
        {
            var deleted = await sbom.DeleteAsync(id, ct);
            return deleted ? Results.NoContent() : Results.NotFound();
        });

        group.MapGet("/tools/storage/stats", async (IConfiguration configuration, SbomService sbom, CancellationToken ct) =>
        {
            var dataPath = configuration["DataPath"] ?? "/app/data";
            Directory.CreateDirectory(dataPath);

            var jsonFiles = Directory.GetFiles(dataPath, "*.json", SearchOption.TopDirectoryOnly);
            var totalBytes = jsonFiles
                .Select(path => new FileInfo(path))
                .Sum(file => file.Exists ? file.Length : 0L);

            var sbomBytes = await sbom.GetTotalSbomBytesAsync(ct);
            var sbomCount = await sbom.GetSbomCountAsync(ct);
            var feedRoot = Path.Combine(dataPath, "feeds");
            Directory.CreateDirectory(feedRoot);
            var feedFiles = Directory.Exists(feedRoot)
                ? Directory.GetFiles(feedRoot, "*.nupkg", SearchOption.AllDirectories).ToList()
                : new List<string>();
            var feedBytes = feedFiles
                .Select(path => new FileInfo(path))
                .Sum(file => file.Exists ? file.Length : 0L);

            var response = new DataStorageStatsResponse
            {
                JsonFileCount = jsonFiles.Length,
                TotalJsonBytes = totalBytes,
                SbomFileCount = sbomCount,
                TotalSbomBytes = sbomBytes,
                FeedFileCount = feedFiles.Count,
                TotalFeedBytes = feedBytes,
                TotalStoredBytes = totalBytes + sbomBytes + feedBytes
            };

            return Results.Ok(response);
        });

        feedApi.MapGet("/nuget/versions/{packageId}", async (string packageId, IFeedImportService importService, CancellationToken ct) =>
            Results.Ok(await importService.GetAvailableVersionsAsync(packageId, ct)));

        feedApi.MapPost("/nuget/import", async (NuGetFeedImportRequest request, IFeedImportService importService, CancellationToken ct) =>
            Results.Ok(await importService.ImportAsync(request, ct)));

        feedApi.MapGet("/packages", async (string? feedType, IFeedAdministrationService adminService, CancellationToken ct) =>
        {
            if (!Enum.TryParse<FeedType>(feedType, ignoreCase: true, out var parsedFeedType))
            {
                return Results.BadRequest("A valid feedType is required.");
            }

            return Results.Ok(await adminService.GetPackagesAsync(parsedFeedType, ct));
        });

        feedApi.MapPost("/packages/{id}/scan-vulnerabilities", async (string id, IFeedScannerService scannerService, CancellationToken ct) =>
            Results.Ok(await scannerService.ScanVulnerabilitiesAsync(id, ct)));

        feedApi.MapPost("/packages/{id}/check-outdated", async (string id, IFeedScannerService scannerService, CancellationToken ct) =>
            Results.Ok(await scannerService.CheckOutdatedAsync(id, ct)));

        feedApi.MapPost("/packages/scan-components", async (string? feedType, IFeedScannerService scannerService, CancellationToken ct) =>
        {
            if (!Enum.TryParse<FeedType>(feedType, ignoreCase: true, out var parsedFeedType))
            {
                return Results.BadRequest("A valid feedType is required.");
            }

            return Results.Ok(await scannerService.ScanAllAsync(parsedFeedType, ct));
        });

        feedApi.MapPost("/packages/{id}/update", async (string id, FeedPackageUpdateRequest request, IFeedAdministrationService adminService, CancellationToken ct) =>
            Results.Ok(await adminService.UpdatePackageAsync(id, request.TargetVersion, request.KeepOldVersion, ct)));

        feedApi.MapDelete("/packages/{id}", async (string id, IFeedAdministrationService adminService, CancellationToken ct) =>
        {
            await adminService.DeletePackageAsync(id, ct);
            return Results.NoContent();
        });

        app.MapGet("/feeds/nuget/v3/index.json", (HttpRequest request) =>
        {
            var baseUrl = $"{request.Scheme}://{request.Host}";
            var serviceIndex = new
            {
                version = "3.0.0",
                resources = new object[]
                {
                    new
                    {
                        @id = $"{baseUrl}/feeds/nuget/v3/flatcontainer/",
                        @type = "PackageBaseAddress/3.0.0",
                        comment = "Repo Analyzer local NuGet feed"
                    }
                }
            };

            return Results.Ok(serviceIndex);
        });

        app.MapGet("/feeds/nuget/v3/flatcontainer/{packageId}/index.json", async (string packageId, IFeedAdministrationService adminService, CancellationToken ct) =>
        {
            var versions = await adminService.GetHostedNuGetVersionsAsync(packageId, ct);
            return Results.Ok(new
            {
                versions
            });
        });

        app.MapGet("/feeds/nuget/v3/flatcontainer/{packageId}/{version}/{fileName}", async (string packageId, string version, string fileName, IFeedAdministrationService adminService, CancellationToken ct) =>
        {
            var result = await adminService.GetNuGetPackageFileAsync(packageId, version, ct);
            if (result is null)
            {
                return Results.NotFound();
            }

            return Results.File(result.Value.FilePath, "application/octet-stream", result.Value.DownloadFileName);
        });

        app.MapGet("/feeds/nuget/{packageId}/{version}/{fileName}", async (string packageId, string version, string fileName, IFeedAdministrationService adminService, CancellationToken ct) =>
        {
            var result = await adminService.GetNuGetPackageFileAsync(packageId, version, ct);
            if (result is null)
            {
                return Results.NotFound();
            }

            return Results.File(result.Value.FilePath, "application/octet-stream", result.Value.DownloadFileName);
        });

        return app;
    }
}
