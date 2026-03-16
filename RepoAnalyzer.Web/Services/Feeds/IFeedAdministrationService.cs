using RepoAnalyzer.Web.Dto;
using RepoAnalyzer.Web.Models.Enums;

namespace RepoAnalyzer.Web.Services.Feeds;

public interface IFeedAdministrationService
{
    Task<List<FeedPackageView>> GetPackagesAsync(FeedType feedType, CancellationToken ct = default);
    Task<FeedPackageView> UpdatePackageAsync(string id, string? targetVersion, bool keepOldVersion, CancellationToken ct = default);
    Task DeletePackageAsync(string id, CancellationToken ct = default);
    Task<(string FilePath, string DownloadFileName)?> GetPackageFileAsync(FeedType feedType, string packageId, string version, string? fileName = null, CancellationToken ct = default);
    Task<List<string>> GetHostedVersionsAsync(FeedType feedType, string packageId, CancellationToken ct = default);
}
