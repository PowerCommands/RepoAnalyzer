using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Xml.Linq;
using RepoAnalyzer.Web.Dto;
using RepoAnalyzer.Web.Models;
using RepoAnalyzer.Web.Models.Enums;
using RepoAnalyzer.Web.Services.Analysis.Logging;

namespace RepoAnalyzer.Web.Services.Feeds;

public sealed class NuGetFeedImportService : IFeedImportService
{
    private readonly AppDataService _data;
    private readonly NuGetPackageSourceClient _nugetClient;
    private readonly FeedStoragePathService _pathService;
    private readonly ILogger<NuGetFeedImportService> _logger;
    private readonly IAnalysisLog _analysisLog;

    public NuGetFeedImportService(
        AppDataService data,
        NuGetPackageSourceClient nugetClient,
        FeedStoragePathService pathService,
        ILogger<NuGetFeedImportService> logger,
        IAnalysisLog analysisLog)
    {
        _data = data;
        _nugetClient = nugetClient;
        _pathService = pathService;
        _logger = logger;
        _analysisLog = analysisLog;
    }

    public FeedType FeedType => FeedType.NuGet;

    public async Task<NuGetFeedVersionsResponse> GetAvailableVersionsAsync(string packageId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(packageId))
        {
            throw new InvalidOperationException("Package ID is required.");
        }

        return new NuGetFeedVersionsResponse
        {
            PackageId = packageId.Trim(),
            Versions = await _nugetClient.GetVersionsAsync(packageId, ct)
        };
    }

    public async Task<FeedPackageView> ImportAsync(NuGetFeedImportRequest request, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.PackageId))
        {
            throw new InvalidOperationException("Package ID is required.");
        }

        if (string.IsNullOrWhiteSpace(request.Version))
        {
            throw new InvalidOperationException("Package version is required.");
        }

        var normalizedPackageId = NuGetPackageSourceClient.NormalizePackageId(request.PackageId);
        var version = request.Version.Trim();
        var packages = await _data.GetFeedPackagesAsync(ct);
        var links = await _data.GetComponentFeedPackageLinksAsync(ct);
        var logContext = CreateLogContext();

        var existing = packages.FirstOrDefault(x =>
            x.FeedType == FeedType.NuGet &&
            string.Equals(x.NormalizedPackageId, normalizedPackageId, StringComparison.Ordinal) &&
            string.Equals(x.Version, version, StringComparison.OrdinalIgnoreCase));

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
            _logger.LogInformation(
                "Component already exist in feed, download skipped. PackageId={PackageId}, Version={Version}, FeedType={FeedType}",
                existing.PackageId,
                existing.Version,
                existing.FeedType);
            await EnsureComponentLinkAsync(existing.Id, request.ComponentId, links, ct);
            var refreshedLinks = await _data.GetComponentFeedPackageLinksAsync(ct);
            return FeedPackageMapper.ToView(existing, refreshedLinks);
        }

        try
        {
            await _analysisLog.InfoAsync(
                "FeedImportStart",
                "Starting NuGet package download for feed import.",
                logContext,
                new Dictionary<string, object?>
                {
                    ["packageId"] = request.PackageId,
                    ["version"] = version,
                    ["feedType"] = FeedType.NuGet.ToString()
                },
                ct);
            _logger.LogInformation(
                "Starting NuGet package download for feed import. PackageId={PackageId}, Version={Version}",
                request.PackageId,
                version);

            var packageBytes = await _nugetClient.DownloadPackageAsync(request.PackageId, version, ct);
            var metadata = ReadMetadata(packageBytes);
            var sha256 = Convert.ToHexString(SHA256.HashData(packageBytes)).ToLowerInvariant();
            var filePath = _pathService.GetPackageFilePath(FeedType.NuGet, normalizedPackageId, metadata.Version);

            var fileDirectory = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrWhiteSpace(fileDirectory))
            {
                Directory.CreateDirectory(fileDirectory);
            }

            await File.WriteAllBytesAsync(filePath, packageBytes, ct);

            var package = new FeedPackage
            {
                FeedType = FeedType.NuGet,
                PackageId = metadata.Id,
                NormalizedPackageId = NuGetPackageSourceClient.NormalizePackageId(metadata.Id),
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
                "NuGet package downloaded and stored successfully.",
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
                "NuGet package downloaded and stored successfully. PackageId={PackageId}, Version={Version}, Bytes={Bytes}, FilePath={FilePath}, Sha256={Sha256}",
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
                "NuGet package download failed for feed import.",
                logContext,
                ex,
                new Dictionary<string, object?>
                {
                    ["packageId"] = request.PackageId,
                    ["version"] = version,
                    ["feedType"] = FeedType.NuGet.ToString()
                },
                ct);
            _logger.LogError(
                ex,
                "NuGet package download failed for feed import. PackageId={PackageId}, Version={Version}",
                request.PackageId,
                version);
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

    private static NuGetMetadata ReadMetadata(byte[] packageBytes)
    {
        using var packageStream = new MemoryStream(packageBytes, writable: false);
        using var archive = new ZipArchive(packageStream, ZipArchiveMode.Read, leaveOpen: false);
        var nuspecEntry = archive.Entries.FirstOrDefault(x => x.FullName.EndsWith(".nuspec", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("The NuGet package does not contain a .nuspec file.");

        using var entryStream = nuspecEntry.Open();
        var document = XDocument.Load(entryStream);
        var metadataElement = document.Root?.Elements().FirstOrDefault(x => x.Name.LocalName == "metadata")
            ?? throw new InvalidOperationException("The NuGet package .nuspec does not contain metadata.");

        var id = metadataElement.Elements().FirstOrDefault(x => x.Name.LocalName == "id")?.Value?.Trim();
        var version = metadataElement.Elements().FirstOrDefault(x => x.Name.LocalName == "version")?.Value?.Trim();
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(version))
        {
            throw new InvalidOperationException("The NuGet package .nuspec is missing id or version.");
        }

        var description = metadataElement.Elements().FirstOrDefault(x => x.Name.LocalName == "description")?.Value?.Trim();
        var authors = metadataElement.Elements().FirstOrDefault(x => x.Name.LocalName == "authors")?.Value?.Trim();
        var dependencies = metadataElement
            .Descendants()
            .Where(x => x.Name.LocalName == "dependency")
            .Select(x => new
            {
                Id = x.Attribute("id")?.Value,
                Version = x.Attribute("version")?.Value,
                TargetFramework = x.Parent?.Attribute("targetFramework")?.Value
            })
            .ToList();

        var metadataJson = JsonSerializer.Serialize(new
        {
            id,
            version,
            description,
            authors,
            dependencies
        }, new JsonSerializerOptions { WriteIndented = true });

        return new NuGetMetadata
        {
            Id = id,
            Version = version,
            Description = description,
            MetadataJson = metadataJson
        };
    }

    private sealed class NuGetMetadata
    {
        public string Id { get; set; } = string.Empty;
        public string Version { get; set; } = string.Empty;
        public string? Description { get; set; }
        public string MetadataJson { get; set; } = string.Empty;
    }
}
