using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using RepoAnalyzer.Web.Dto;
using RepoAnalyzer.Web.Models;
using RepoAnalyzer.Web.Models.Enums;
using RepoAnalyzer.Web.Services.Analysis.Logging;

namespace RepoAnalyzer.Web.Services.Feeds;

public sealed class NpmFeedImportService : IFeedImportService
{
    private readonly AppDataService _data;
    private readonly NpmPackageSourceClient _npmClient;
    private readonly FeedStoragePathService _pathService;
    private readonly ILogger<NpmFeedImportService> _logger;
    private readonly IAnalysisLog _analysisLog;

    public NpmFeedImportService(
        AppDataService data,
        NpmPackageSourceClient npmClient,
        FeedStoragePathService pathService,
        ILogger<NpmFeedImportService> logger,
        IAnalysisLog analysisLog)
    {
        _data = data;
        _npmClient = npmClient;
        _pathService = pathService;
        _logger = logger;
        _analysisLog = analysisLog;
    }

    public FeedType FeedType => FeedType.Npm;

    public async Task<FeedPackageVersionsResponse> GetAvailableVersionsAsync(string packageId, CancellationToken ct = default)
    {
        var document = await _npmClient.GetPackageDocumentAsync(packageId, ct);
        return new FeedPackageVersionsResponse
        {
            FeedType = FeedType.Npm,
            PackageId = document.PackageId,
            LatestVersion = document.LatestVersion,
            Versions = document.Versions.Keys.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList()
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

        var normalizedPackageId = NpmPackageSourceClient.NormalizePackageId(request.PackageId);
        var requestedVersion = request.Version.Trim();
        var packages = await _data.GetFeedPackagesAsync(ct);
        var links = await _data.GetComponentFeedPackageLinksAsync(ct);
        var logContext = CreateLogContext();

        var existing = packages.FirstOrDefault(x =>
            x.FeedType == FeedType.Npm &&
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
                "Starting npm package download for feed import.",
                logContext,
                new Dictionary<string, object?>
                {
                    ["packageId"] = request.PackageId,
                    ["version"] = requestedVersion,
                    ["feedType"] = FeedType.Npm.ToString()
                },
                ct);

            var document = await _npmClient.GetPackageDocumentAsync(request.PackageId, ct);
            if (!document.Versions.TryGetValue(requestedVersion, out var versionDocument))
            {
                throw new InvalidOperationException($"Version '{requestedVersion}' was not found for npm package '{request.PackageId}'.");
            }

            var packageBytes = await _npmClient.DownloadTarballAsync(versionDocument.TarballUrl ?? string.Empty, ct);
            var metadata = ReadMetadata(packageBytes, versionDocument);
            var sha256 = Convert.ToHexString(SHA256.HashData(packageBytes)).ToLowerInvariant();
            var filePath = _pathService.GetPackageFilePath(FeedType.Npm, metadata.NormalizedPackageId, metadata.Version);

            var fileDirectory = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrWhiteSpace(fileDirectory))
            {
                Directory.CreateDirectory(fileDirectory);
            }

            await File.WriteAllBytesAsync(filePath, packageBytes, ct);

            var package = new FeedPackage
            {
                FeedType = FeedType.Npm,
                PackageId = metadata.PackageId,
                NormalizedPackageId = metadata.NormalizedPackageId,
                Version = metadata.Version,
                Description = metadata.Description,
                MetadataJson = metadata.MetadataJson,
                FilePath = filePath,
                Sha256 = sha256,
                CreatedUtc = DateTimeOffset.UtcNow
            };

            packages.Add(package);
            await _data.SaveFeedPackagesAsync(packages, ct);

            await _analysisLog.InfoAsync(
                "FeedImportCompleted",
                "npm package downloaded and stored successfully.",
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
                "npm package downloaded and stored successfully. PackageId={PackageId}, Version={Version}, Bytes={Bytes}, FilePath={FilePath}, Sha256={Sha256}",
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
                "npm package download failed for feed import.",
                logContext,
                ex,
                new Dictionary<string, object?>
                {
                    ["packageId"] = request.PackageId,
                    ["version"] = requestedVersion,
                    ["feedType"] = FeedType.Npm.ToString()
                },
                ct);
            _logger.LogError(
                ex,
                "npm package download failed for feed import. PackageId={PackageId}, Version={Version}",
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

    private static NpmMetadata ReadMetadata(byte[] packageBytes, NpmPackageSourceClient.PackageVersionDocument versionDocument)
    {
        using var packageStream = new MemoryStream(packageBytes, writable: false);
        using var gzipStream = new GZipStream(packageStream, CompressionMode.Decompress, leaveOpen: false);
        using var tarReader = new TarReader(gzipStream, leaveOpen: false);

        while (tarReader.GetNextEntry() is { } entry)
        {
            if (entry.EntryType is not TarEntryType.RegularFile and not TarEntryType.V7RegularFile)
            {
                continue;
            }

            if (!string.Equals(entry.Name, "package/package.json", StringComparison.Ordinal))
            {
                continue;
            }

            using var contentStream = entry.DataStream ?? throw new InvalidOperationException("The npm tarball package.json file was empty.");
            using var document = JsonDocument.Parse(contentStream);
            var root = document.RootElement;
            var packageId = root.TryGetProperty("name", out var nameElement)
                ? nameElement.GetString()
                : versionDocument.Name;
            var version = root.TryGetProperty("version", out var versionElement)
                ? versionElement.GetString()
                : versionDocument.Version;

            if (string.IsNullOrWhiteSpace(packageId) || string.IsNullOrWhiteSpace(version))
            {
                throw new InvalidOperationException("The npm package.json file is missing name or version.");
            }

            var metadata = JsonSerializer.Deserialize<Dictionary<string, object?>>(versionDocument.MetadataJson)
                ?? new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

            metadata["dist"] = new Dictionary<string, object?>
            {
                ["shasum"] = versionDocument.Shasum,
                ["integrity"] = versionDocument.Integrity
            };

            return new NpmMetadata
            {
                PackageId = packageId,
                NormalizedPackageId = NpmPackageSourceClient.NormalizePackageId(packageId),
                Version = version,
                Description = root.TryGetProperty("description", out var descriptionElement) ? descriptionElement.GetString() : versionDocument.Description,
                MetadataJson = JsonSerializer.Serialize(metadata, new JsonSerializerOptions { WriteIndented = true })
            };
        }

        throw new InvalidOperationException("The npm tarball did not contain package/package.json.");
    }

    private sealed class NpmMetadata
    {
        public string PackageId { get; set; } = string.Empty;
        public string NormalizedPackageId { get; set; } = string.Empty;
        public string Version { get; set; } = string.Empty;
        public string? Description { get; set; }
        public string MetadataJson { get; set; } = "{}";
    }
}
