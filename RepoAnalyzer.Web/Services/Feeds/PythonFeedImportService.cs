using System.Security.Cryptography;
using System.Text.Json;
using RepoAnalyzer.Web.Dto;
using RepoAnalyzer.Web.Models;
using RepoAnalyzer.Web.Models.Enums;
using RepoAnalyzer.Web.Services.Analysis.Logging;

namespace RepoAnalyzer.Web.Services.Feeds;

public sealed class PythonFeedImportService : IFeedImportService
{
    private readonly AppDataService _data;
    private readonly PyPiPackageSourceClient _pyPiClient;
    private readonly FeedStoragePathService _pathService;
    private readonly ILogger<PythonFeedImportService> _logger;
    private readonly IAnalysisLog _analysisLog;

    public PythonFeedImportService(
        AppDataService data,
        PyPiPackageSourceClient pyPiClient,
        FeedStoragePathService pathService,
        ILogger<PythonFeedImportService> logger,
        IAnalysisLog analysisLog)
    {
        _data = data;
        _pyPiClient = pyPiClient;
        _pathService = pathService;
        _logger = logger;
        _analysisLog = analysisLog;
    }

    public FeedType FeedType => FeedType.Python;

    public async Task<FeedPackageVersionsResponse> GetAvailableVersionsAsync(string packageId, CancellationToken ct = default)
    {
        var document = await _pyPiClient.GetProjectDocumentAsync(packageId, ct);
        return new FeedPackageVersionsResponse
        {
            FeedType = FeedType.Python,
            PackageId = document.PackageId,
            LatestVersion = document.LatestVersion,
            Versions = document.Versions.Keys
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .ToList()
        };
    }

    public async Task<FeedPackageView> ImportAsync(FeedPackageImportRequest request, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.PackageId))
        {
            throw new InvalidOperationException("Package ID is required.");
        }

        if (string.IsNullOrWhiteSpace(request.Version))
        {
            throw new InvalidOperationException("Package version is required.");
        }

        var normalizedPackageId = PyPiPackageSourceClient.NormalizePackageId(request.PackageId);
        var requestedVersion = request.Version.Trim();
        var packages = await _data.GetFeedPackagesAsync(ct);
        var links = await _data.GetComponentFeedPackageLinksAsync(ct);
        var logContext = CreateLogContext();

        var existing = packages.FirstOrDefault(x =>
            x.FeedType == FeedType.Python &&
            string.Equals(x.NormalizedPackageId, normalizedPackageId, StringComparison.Ordinal) &&
            string.Equals(x.Version, requestedVersion, StringComparison.OrdinalIgnoreCase));

        if (existing is not null)
        {
            await _analysisLog.InfoAsync(
                "FeedImportSkipExisting",
                "Component already exist in feed, download skipped.",
                logContext,
                new Dictionary<string, object?>
                {
                    ["packageId"] = existing.PackageId,
                    ["version"] = existing.Version,
                    ["feedType"] = existing.FeedType.ToString()
                },
                ct);
            await EnsureComponentLinkAsync(existing.Id, request.ComponentId, links, ct);
            var refreshedLinks = await _data.GetComponentFeedPackageLinksAsync(ct);
            return FeedPackageMapper.ToView(existing, refreshedLinks);
        }

        try
        {
            await _analysisLog.InfoAsync(
                "FeedImportStart",
                "Starting Python package download for feed import.",
                logContext,
                new Dictionary<string, object?>
                {
                    ["packageId"] = request.PackageId,
                    ["version"] = requestedVersion,
                    ["feedType"] = FeedType.Python.ToString()
                },
                ct);

            var release = await _pyPiClient.GetReleaseAsync(request.PackageId, requestedVersion, ct);
            var packageBytes = await _pyPiClient.DownloadFileAsync(release.File.Url, ct);
            var sha256 = Convert.ToHexString(SHA256.HashData(packageBytes)).ToLowerInvariant();
            var metadataJson = BuildMetadataJson(release);
            var filePath = _pathService.GetPackageFilePath(FeedType.Python, release.NormalizedPackageId, release.Version, release.File.FileName);

            var fileDirectory = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrWhiteSpace(fileDirectory))
            {
                Directory.CreateDirectory(fileDirectory);
            }

            await File.WriteAllBytesAsync(filePath, packageBytes, ct);

            var package = new FeedPackage
            {
                FeedType = FeedType.Python,
                PackageId = release.PackageId,
                NormalizedPackageId = release.NormalizedPackageId,
                Version = release.Version,
                Description = $"{release.File.PackageType ?? "distribution"}{(string.IsNullOrWhiteSpace(release.File.RequiresPython) ? string.Empty : $" | Requires-Python: {release.File.RequiresPython}")}",
                MetadataJson = metadataJson,
                FilePath = filePath,
                Sha256 = sha256,
                CreatedUtc = DateTimeOffset.UtcNow
            };

            packages.Add(package);
            await _data.SaveFeedPackagesAsync(packages, ct);

            await _analysisLog.InfoAsync(
                "FeedImportCompleted",
                "Python package downloaded and stored successfully.",
                logContext,
                new Dictionary<string, object?>
                {
                    ["packageId"] = package.PackageId,
                    ["version"] = package.Version,
                    ["feedType"] = package.FeedType.ToString(),
                    ["bytes"] = packageBytes.Length,
                    ["filePath"] = package.FilePath,
                    ["sha256"] = package.Sha256
                },
                ct);
            _logger.LogInformation(
                "Python package downloaded and stored successfully. PackageId={PackageId}, Version={Version}, Bytes={Bytes}, FilePath={FilePath}, Sha256={Sha256}",
                package.PackageId,
                package.Version,
                packageBytes.Length,
                package.FilePath,
                package.Sha256);

            links = await _data.GetComponentFeedPackageLinksAsync(ct);
            await EnsureComponentLinkAsync(package.Id, request.ComponentId, links, ct);
            var storedLinks = await _data.GetComponentFeedPackageLinksAsync(ct);
            return FeedPackageMapper.ToView(package, storedLinks);
        }
        catch (Exception ex)
        {
            await _analysisLog.ErrorAsync(
                "FeedImportFailed",
                "Python package download failed for feed import.",
                logContext,
                ex,
                new Dictionary<string, object?>
                {
                    ["packageId"] = request.PackageId,
                    ["version"] = requestedVersion,
                    ["feedType"] = FeedType.Python.ToString()
                },
                ct);
            _logger.LogError(
                ex,
                "Python package download failed for feed import. PackageId={PackageId}, Version={Version}",
                request.PackageId,
                requestedVersion);
            throw;
        }
    }

    private async Task EnsureComponentLinkAsync(string feedPackageId, string? componentId, List<ComponentFeedPackageLink> links, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(componentId))
        {
            return;
        }

        var normalizedComponentId = componentId.Trim();
        var exists = links.Any(x =>
            string.Equals(x.ComponentId, normalizedComponentId, StringComparison.Ordinal) &&
            string.Equals(x.FeedPackageId, feedPackageId, StringComparison.Ordinal));
        if (exists)
        {
            return;
        }

        links.Add(new ComponentFeedPackageLink
        {
            ComponentId = normalizedComponentId,
            FeedPackageId = feedPackageId
        });

        await _data.SaveComponentFeedPackageLinksAsync(links, ct);
    }

    private static string BuildMetadataJson(PyPiPackageSourceClient.SelectedRelease release)
    {
        Dictionary<string, object?> metadata;
        try
        {
            metadata = JsonSerializer.Deserialize<Dictionary<string, object?>>(release.File.MetadataJson)
                ?? new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            metadata = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        }

        metadata["name"] = release.PackageId;
        metadata["version"] = release.Version;
        metadata["filename"] = release.File.FileName;
        metadata["packagetype"] = release.File.PackageType;
        metadata["python_version"] = release.File.PythonVersion;
        metadata["requires_python"] = release.File.RequiresPython;
        metadata["sha256"] = release.File.Sha256;

        return JsonSerializer.Serialize(metadata, new JsonSerializerOptions { WriteIndented = true });
    }

    private static AnalysisLogContext CreateLogContext()
        => new()
        {
            AnalysisRunId = $"feeds-{Guid.NewGuid():N}",
            ProviderType = "Feeds"
        };
}
