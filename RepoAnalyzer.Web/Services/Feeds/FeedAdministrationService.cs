using RepoAnalyzer.Web.Dto;
using RepoAnalyzer.Web.Models;
using RepoAnalyzer.Web.Models.Enums;
using RepoAnalyzer.Web.Services.Analysis.Logging;

namespace RepoAnalyzer.Web.Services.Feeds;

public sealed class FeedAdministrationService : IFeedAdministrationService
{
    private readonly AppDataService _data;
    private readonly FeedImportServiceResolver _feedImportResolver;
    private readonly ILogger<FeedAdministrationService> _logger;
    private readonly IAnalysisLog _analysisLog;

    public FeedAdministrationService(
        AppDataService data,
        FeedImportServiceResolver feedImportResolver,
        ILogger<FeedAdministrationService> logger,
        IAnalysisLog analysisLog)
    {
        _data = data;
        _feedImportResolver = feedImportResolver;
        _logger = logger;
        _analysisLog = analysisLog;
    }

    public async Task<List<FeedPackageView>> GetPackagesAsync(FeedType feedType, CancellationToken ct = default)
    {
        var packages = await _data.GetFeedPackagesAsync(ct);
        var links = await _data.GetComponentFeedPackageLinksAsync(ct);

        return packages
            .Where(x => x.FeedType == feedType)
            .OrderByDescending(x => x.CreatedUtc)
            .ThenBy(x => x.PackageId, StringComparer.OrdinalIgnoreCase)
            .Select(x => FeedPackageMapper.ToView(x, links))
            .ToList();
    }

    public async Task<FeedPackageView> UpdatePackageAsync(string id, string? targetVersion, bool keepOldVersion, CancellationToken ct = default)
    {
        var packages = await _data.GetFeedPackagesAsync(ct);
        var links = await _data.GetComponentFeedPackageLinksAsync(ct);
        var package = packages.FirstOrDefault(x => string.Equals(x.Id, id, StringComparison.Ordinal))
            ?? throw new InvalidOperationException("Feed package was not found.");

        var chosenVersion = targetVersion;
        if (string.IsNullOrWhiteSpace(chosenVersion))
        {
            var importService = _feedImportResolver.GetRequired(package.FeedType);
            var versionResponse = await importService.GetAvailableVersionsAsync(package.PackageId, ct);
            chosenVersion = versionResponse.LatestVersion ?? versionResponse.Versions.LastOrDefault();
        }

        if (string.IsNullOrWhiteSpace(chosenVersion))
        {
            throw new InvalidOperationException("No target version could be determined.");
        }

        var logContext = CreateLogContext();
        await _analysisLog.InfoAsync(
            "FeedUpdateStart",
            "Starting feed package update.",
            logContext,
            new Dictionary<string, object?>
            {
                ["packageId"] = package.PackageId,
                ["currentVersion"] = package.Version,
                ["targetVersion"] = chosenVersion,
                ["keepOldVersion"] = keepOldVersion,
                ["feedType"] = package.FeedType.ToString()
            },
            ct);

        _logger.LogInformation(
            "Starting feed package update. PackageId={PackageId}, CurrentVersion={CurrentVersion}, TargetVersion={TargetVersion}, KeepOldVersion={KeepOldVersion}, FeedType={FeedType}",
            package.PackageId,
            package.Version,
            chosenVersion,
            keepOldVersion,
            package.FeedType);

        var linkedComponentIds = links
            .Where(x => string.Equals(x.FeedPackageId, package.Id, StringComparison.Ordinal))
            .Select(x => x.ComponentId)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var updated = await _feedImportResolver.GetRequired(package.FeedType).ImportAsync(new FeedPackageImportRequest
        {
            FeedType = package.FeedType,
            PackageId = package.PackageId,
            Version = chosenVersion,
            ComponentId = linkedComponentIds.FirstOrDefault()
        }, ct);

        if (linkedComponentIds.Count > 1)
        {
            var refreshedLinks = await _data.GetComponentFeedPackageLinksAsync(ct);
            var changed = false;
            foreach (var componentId in linkedComponentIds.Skip(1))
            {
                var exists = refreshedLinks.Any(x =>
                    string.Equals(x.ComponentId, componentId, StringComparison.Ordinal) &&
                    string.Equals(x.FeedPackageId, updated.Id, StringComparison.Ordinal));
                if (exists)
                {
                    continue;
                }

                refreshedLinks.Add(new ComponentFeedPackageLink
                {
                    ComponentId = componentId,
                    FeedPackageId = updated.Id
                });
                changed = true;
            }

            if (changed)
            {
                await _data.SaveComponentFeedPackageLinksAsync(refreshedLinks, ct);
                return (await GetPackagesAsync(package.FeedType, ct)).First(x => x.Id == updated.Id);
            }
        }

        if (!keepOldVersion)
        {
            var refreshedPackages = await _data.GetFeedPackagesAsync(ct);
            var refreshedLinks = await _data.GetComponentFeedPackageLinksAsync(ct);
            var oldPackage = refreshedPackages.FirstOrDefault(x => string.Equals(x.Id, package.Id, StringComparison.Ordinal));
            if (oldPackage is not null)
            {
                refreshedPackages.Remove(oldPackage);
                refreshedLinks.RemoveAll(x => string.Equals(x.FeedPackageId, oldPackage.Id, StringComparison.Ordinal));
                await _data.SaveFeedPackagesAsync(refreshedPackages, ct);
                await _data.SaveComponentFeedPackageLinksAsync(refreshedLinks, ct);

                try
                {
                    if (File.Exists(oldPackage.FilePath))
                    {
                        File.Delete(oldPackage.FilePath);
                    }
                }
                catch
                {
                    // Best effort cleanup of old package file.
                }
            }
        }

        await _analysisLog.InfoAsync(
            "FeedUpdateCompleted",
            "Feed package update completed.",
            logContext,
            new Dictionary<string, object?>
            {
                ["packageId"] = package.PackageId,
                ["oldVersion"] = package.Version,
                ["newVersion"] = updated.Version,
                ["keepOldVersion"] = keepOldVersion,
                ["feedType"] = package.FeedType.ToString()
            },
            ct);
        _logger.LogInformation(
            "Feed package update completed. PackageId={PackageId}, OldVersion={OldVersion}, NewVersion={NewVersion}, KeepOldVersion={KeepOldVersion}, FeedType={FeedType}",
            package.PackageId,
            package.Version,
            updated.Version,
            keepOldVersion,
            package.FeedType);

        return updated;
    }

    public async Task DeletePackageAsync(string id, CancellationToken ct = default)
    {
        var packages = await _data.GetFeedPackagesAsync(ct);
        var links = await _data.GetComponentFeedPackageLinksAsync(ct);
        var package = packages.FirstOrDefault(x => string.Equals(x.Id, id, StringComparison.Ordinal))
            ?? throw new InvalidOperationException("Feed package was not found.");
        var logContext = CreateLogContext();

        await _analysisLog.InfoAsync(
            "FeedDeleteStart",
            "Starting feed package delete.",
            logContext,
            new Dictionary<string, object?>
            {
                ["packageId"] = package.PackageId,
                ["version"] = package.Version,
                ["feedType"] = package.FeedType.ToString(),
                ["filePath"] = package.FilePath
            },
            ct);
        _logger.LogInformation(
            "Starting feed package delete. PackageId={PackageId}, Version={Version}, FeedType={FeedType}, FilePath={FilePath}",
            package.PackageId,
            package.Version,
            package.FeedType,
            package.FilePath);

        packages.Remove(package);
        links.RemoveAll(x => string.Equals(x.FeedPackageId, package.Id, StringComparison.Ordinal));

        await _data.SaveFeedPackagesAsync(packages, ct);
        await _data.SaveComponentFeedPackageLinksAsync(links, ct);

        try
        {
            if (File.Exists(package.FilePath))
            {
                File.Delete(package.FilePath);
            }

            var versionDirectory = Path.GetDirectoryName(package.FilePath);
            if (!string.IsNullOrWhiteSpace(versionDirectory) && Directory.Exists(versionDirectory) && !Directory.EnumerateFileSystemEntries(versionDirectory).Any())
            {
                Directory.Delete(versionDirectory);
            }

            var packageDirectory = versionDirectory is null ? null : Directory.GetParent(versionDirectory)?.FullName;
            if (!string.IsNullOrWhiteSpace(packageDirectory) && Directory.Exists(packageDirectory) && !Directory.EnumerateFileSystemEntries(packageDirectory).Any())
            {
                Directory.Delete(packageDirectory);
            }
        }
        catch
        {
            // Best effort cleanup.
        }

        await _analysisLog.InfoAsync(
            "FeedDeleteCompleted",
            "Feed package delete completed.",
            logContext,
            new Dictionary<string, object?>
            {
                ["packageId"] = package.PackageId,
                ["version"] = package.Version,
                ["feedType"] = package.FeedType.ToString()
            },
            ct);
        _logger.LogInformation(
            "Feed package delete completed. PackageId={PackageId}, Version={Version}, FeedType={FeedType}",
            package.PackageId,
            package.Version,
            package.FeedType);
    }

    public async Task<(string FilePath, string DownloadFileName)?> GetPackageFileAsync(FeedType feedType, string packageId, string version, string? fileName = null, CancellationToken ct = default)
    {
        var normalizedPackageId = feedType switch
        {
            FeedType.NuGet => NuGetPackageSourceClient.NormalizePackageId(packageId),
            FeedType.Npm => NpmPackageSourceClient.NormalizePackageId(packageId),
            FeedType.Python => PyPiPackageSourceClient.NormalizePackageId(packageId),
            FeedType.Maven => MavenPackageSourceClient.NormalizePackageId(packageId),
            _ => packageId.Trim()
        };
        var packages = await _data.GetFeedPackagesAsync(ct);
        var match = packages.FirstOrDefault(x =>
            x.FeedType == feedType &&
            string.Equals(x.NormalizedPackageId, normalizedPackageId, StringComparison.Ordinal) &&
            string.Equals(x.Version, version, StringComparison.OrdinalIgnoreCase));

        if (match is null || !File.Exists(match.FilePath))
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(fileName))
        {
            var versionDirectory = Path.GetDirectoryName(match.FilePath);
            if (!string.IsNullOrWhiteSpace(versionDirectory))
            {
                var requestedPath = Path.Combine(versionDirectory, Path.GetFileName(fileName));
                if (File.Exists(requestedPath))
                {
                    return (requestedPath, Path.GetFileName(requestedPath));
                }
            }
        }

        return (match.FilePath, Path.GetFileName(match.FilePath));
    }

    public async Task<List<string>> GetHostedVersionsAsync(FeedType feedType, string packageId, CancellationToken ct = default)
    {
        var normalizedPackageId = feedType switch
        {
            FeedType.NuGet => NuGetPackageSourceClient.NormalizePackageId(packageId),
            FeedType.Npm => NpmPackageSourceClient.NormalizePackageId(packageId),
            FeedType.Python => PyPiPackageSourceClient.NormalizePackageId(packageId),
            FeedType.Maven => MavenPackageSourceClient.NormalizePackageId(packageId),
            _ => packageId.Trim()
        };
        var packages = await _data.GetFeedPackagesAsync(ct);

        return packages
            .Where(x => x.FeedType == feedType && string.Equals(x.NormalizedPackageId, normalizedPackageId, StringComparison.Ordinal))
            .Select(x => x.Version)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static AnalysisLogContext CreateLogContext()
        => new()
        {
            AnalysisRunId = $"feeds-{Guid.NewGuid():N}",
            ProviderType = "Feeds"
        };
}
