using System.Security.Cryptography;
using System.Text.Json;
using RepoAnalyzer.Web.Dto;
using RepoAnalyzer.Web.Models;
using RepoAnalyzer.Web.Models.Enums;
using RepoAnalyzer.Web.Services.Analysis.Logging;

namespace RepoAnalyzer.Web.Services.Feeds;

public sealed class MavenFeedImportService : IFeedImportService
{
    private readonly AppDataService _data;
    private readonly MavenPackageSourceClient _mavenClient;
    private readonly FeedStoragePathService _pathService;
    private readonly ILogger<MavenFeedImportService> _logger;
    private readonly IAnalysisLog _analysisLog;

    public MavenFeedImportService(
        AppDataService data,
        MavenPackageSourceClient mavenClient,
        FeedStoragePathService pathService,
        ILogger<MavenFeedImportService> logger,
        IAnalysisLog analysisLog)
    {
        _data = data;
        _mavenClient = mavenClient;
        _pathService = pathService;
        _logger = logger;
        _analysisLog = analysisLog;
    }

    public FeedType FeedType => FeedType.Maven;

    public async Task<FeedPackageVersionsResponse> GetAvailableVersionsAsync(string packageId, CancellationToken ct = default)
    {
        var document = await _mavenClient.GetVersionsAsync(packageId, ct);
        return new FeedPackageVersionsResponse
        {
            FeedType = FeedType.Maven,
            PackageId = document.PackageId,
            LatestVersion = document.LatestVersion,
            Versions = document.Versions
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

        var normalizedPackageId = MavenPackageSourceClient.NormalizePackageId(request.PackageId);
        var requestedVersion = request.Version.Trim();
        var packages = await _data.GetFeedPackagesAsync(ct);
        var links = await _data.GetComponentFeedPackageLinksAsync(ct);
        var logContext = CreateLogContext();

        var existing = packages.FirstOrDefault(x =>
            x.FeedType == FeedType.Maven &&
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
                "Starting Maven package download for feed import.",
                logContext,
                new Dictionary<string, object?>
                {
                    ["packageId"] = request.PackageId,
                    ["version"] = requestedVersion,
                    ["feedType"] = FeedType.Maven.ToString()
                },
                ct);

            var release = await _mavenClient.GetReleaseAsync(request.PackageId, requestedVersion, ct);
            var artifactBytes = await _mavenClient.DownloadFileAsync(release.ArtifactUrl, ct);
            var artifactFilePath = _pathService.GetPackageFilePath(FeedType.Maven, release.NormalizedPackageId, release.Version, release.ArtifactFileName);
            var pomFilePath = _pathService.GetPackageFilePath(FeedType.Maven, release.NormalizedPackageId, release.Version, release.PomFileName);

            await File.WriteAllBytesAsync(artifactFilePath, artifactBytes, ct);
            await File.WriteAllTextAsync(pomFilePath, release.PomContent, ct);

            var package = new FeedPackage
            {
                FeedType = FeedType.Maven,
                PackageId = release.PackageId,
                NormalizedPackageId = release.NormalizedPackageId,
                Version = release.Version,
                Description = release.Description,
                MetadataJson = JsonSerializer.Serialize(new
                {
                    groupId = release.GroupId,
                    artifactId = release.ArtifactId,
                    version = release.Version,
                    packaging = release.Packaging,
                    artifactFileName = release.ArtifactFileName,
                    pomFileName = release.PomFileName
                }, new JsonSerializerOptions { WriteIndented = true }),
                FilePath = artifactFilePath,
                Sha256 = Convert.ToHexString(SHA256.HashData(artifactBytes)).ToLowerInvariant(),
                CreatedUtc = DateTimeOffset.UtcNow
            };

            packages.Add(package);
            await _data.SaveFeedPackagesAsync(packages, ct);

            await _analysisLog.InfoAsync(
                "FeedImportCompleted",
                "Maven package downloaded and stored successfully.",
                logContext,
                new Dictionary<string, object?>
                {
                    ["packageId"] = package.PackageId,
                    ["version"] = package.Version,
                    ["feedType"] = package.FeedType.ToString(),
                    ["bytes"] = artifactBytes.Length,
                    ["filePath"] = package.FilePath,
                    ["sha256"] = package.Sha256
                },
                ct);
            _logger.LogInformation(
                "Maven package downloaded and stored successfully. PackageId={PackageId}, Version={Version}, Bytes={Bytes}, FilePath={FilePath}, Sha256={Sha256}",
                package.PackageId,
                package.Version,
                artifactBytes.Length,
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
                "Maven package download failed for feed import.",
                logContext,
                ex,
                new Dictionary<string, object?>
                {
                    ["packageId"] = request.PackageId,
                    ["version"] = requestedVersion,
                    ["feedType"] = FeedType.Maven.ToString()
                },
                ct);
            _logger.LogError(
                ex,
                "Maven package download failed for feed import. PackageId={PackageId}, Version={Version}",
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

    private static AnalysisLogContext CreateLogContext()
        => new()
        {
            AnalysisRunId = $"feeds-{Guid.NewGuid():N}",
            ProviderType = "Feeds"
        };
}
