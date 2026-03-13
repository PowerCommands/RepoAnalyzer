using RepoAnalyzer.Web.Dto;
using RepoAnalyzer.Web.Models;

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
            DownloadUrl = $"/feeds/nuget/v3/flatcontainer/{Uri.EscapeDataString(package.NormalizedPackageId)}/{Uri.EscapeDataString(package.Version)}/{Uri.EscapeDataString(package.NormalizedPackageId)}.{Uri.EscapeDataString(package.Version)}.nupkg"
        };
    }
}
