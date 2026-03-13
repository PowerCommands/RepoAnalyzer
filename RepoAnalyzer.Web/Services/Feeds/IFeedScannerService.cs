using RepoAnalyzer.Web.Models.Enums;
using RepoAnalyzer.Web.Dto;

namespace RepoAnalyzer.Web.Services.Feeds;

public interface IFeedScannerService
{
    Task<FeedPackageView> ScanVulnerabilitiesAsync(string id, CancellationToken ct = default);
    Task<FeedPackageView> CheckOutdatedAsync(string id, CancellationToken ct = default);
    Task<List<FeedPackageView>> ScanAllAsync(FeedType feedType, CancellationToken ct = default);
}
