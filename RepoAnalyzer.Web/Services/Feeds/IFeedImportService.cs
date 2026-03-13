using RepoAnalyzer.Web.Dto;
using RepoAnalyzer.Web.Models.Enums;

namespace RepoAnalyzer.Web.Services.Feeds;

public interface IFeedImportService
{
    FeedType FeedType { get; }
    Task<FeedPackageView> ImportAsync(NuGetFeedImportRequest request, CancellationToken ct = default);
    Task<NuGetFeedVersionsResponse> GetAvailableVersionsAsync(string packageId, CancellationToken ct = default);
}
