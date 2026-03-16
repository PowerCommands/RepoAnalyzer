using RepoAnalyzer.Web.Dto;
using RepoAnalyzer.Web.Models;
using RepoAnalyzer.Web.Models.Enums;

namespace RepoAnalyzer.Web.Services.Feeds;

internal static class FeedPackageMapper
{
    public static FeedPackageView ToView(FeedPackage package, IEnumerable<ComponentFeedPackageLink> links)
    {
        return new FeedPackageView
        {
            Id = package.Id,
            FeedType = package.FeedType.ToString(),
            PackageId = package.PackageId,
            NormalizedPackageId = package.NormalizedPackageId,
            Version = package.Version,
            Description = package.Description,
            MetadataJson = package.MetadataJson,
            FilePath = package.FilePath,
            Sha256 = package.Sha256,
            CreatedUtc = package.CreatedUtc,
            LastScanUtc = package.LastScanUtc,
            VulnerabilityStatus = package.VulnerabilityStatus,
            OutdatedStatus = package.OutdatedStatus,
            LatestKnownVersion = package.LatestKnownVersion,
            ComponentIds = links
                .Where(x => string.Equals(x.FeedPackageId, package.Id, StringComparison.Ordinal))
                .Select(x => x.ComponentId)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(x => x, StringComparer.Ordinal)
                .ToList(),
            DownloadUrl = BuildDownloadUrl(package)
        };
    }

    private static string BuildDownloadUrl(FeedPackage package)
        => package.FeedType switch
        {
            FeedType.NuGet => $"/feeds/nuget/v3/flatcontainer/{Uri.EscapeDataString(package.NormalizedPackageId)}/{Uri.EscapeDataString(package.Version)}/{Uri.EscapeDataString(FeedStoragePathService.GetPackageFileName(package.FeedType, package.NormalizedPackageId, package.Version))}",
            FeedType.Npm => $"/feeds/npm/-/tarball?packageId={Uri.EscapeDataString(package.PackageId)}&version={Uri.EscapeDataString(package.Version)}",
            FeedType.Python => $"/feeds/pypi/packages/{Uri.EscapeDataString(package.NormalizedPackageId)}/{Uri.EscapeDataString(package.Version)}/{Uri.EscapeDataString(Path.GetFileName(package.FilePath))}",
            FeedType.Maven => BuildMavenDownloadUrl(package),
            _ => string.Empty
        };

    private static string BuildMavenDownloadUrl(FeedPackage package)
    {
        var coordinate = MavenPackageSourceClient.ParsePackageId(package.PackageId);
        var groupPath = MavenPackageSourceClient.GetGroupPath(coordinate.GroupId);
        return $"/feeds/maven/{groupPath}/{Uri.EscapeDataString(coordinate.ArtifactId)}/{Uri.EscapeDataString(package.Version)}/{Uri.EscapeDataString(Path.GetFileName(package.FilePath))}";
    }
}
