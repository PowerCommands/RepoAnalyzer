using RepoAnalyzer.Web.Models.Enums;

namespace RepoAnalyzer.Web.Services.Feeds;

public sealed class FeedImportServiceResolver
{
    private readonly Dictionary<FeedType, IFeedImportService> _services;

    public FeedImportServiceResolver(IEnumerable<IFeedImportService> services)
    {
        _services = services.ToDictionary(x => x.FeedType);
    }

    public IFeedImportService GetRequired(FeedType feedType)
        => _services.TryGetValue(feedType, out var service)
            ? service
            : throw new InvalidOperationException($"Feed type '{feedType}' is not supported yet.");
}
