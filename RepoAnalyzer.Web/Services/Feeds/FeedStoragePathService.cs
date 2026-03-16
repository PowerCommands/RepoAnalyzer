using RepoAnalyzer.Web.Models.Enums;

namespace RepoAnalyzer.Web.Services.Feeds;

public sealed class FeedStoragePathService
{
    private readonly string _dataPath;

    public FeedStoragePathService(IConfiguration configuration)
    {
        _dataPath = configuration["DataPath"] ?? "/app/data";
    }

    public string GetPackageFilePath(FeedType feedType, string normalizedPackageId, string version)
    {
        var root = GetFeedRoot(feedType);
        Directory.CreateDirectory(root);
        var packageFolder = Path.Combine(root, normalizedPackageId, version);
        Directory.CreateDirectory(packageFolder);
        return Path.Combine(packageFolder, GetPackageFileName(feedType, normalizedPackageId, version));
    }

    public string GetPackageFilePath(FeedType feedType, string normalizedPackageId, string version, string fileName)
    {
        var root = GetFeedRoot(feedType);
        Directory.CreateDirectory(root);
        var packageFolder = Path.Combine(root, normalizedPackageId, version);
        Directory.CreateDirectory(packageFolder);
        return Path.Combine(packageFolder, fileName);
    }

    public string GetTempRoot()
    {
        var root = Path.Combine(_dataPath, "tmp", "feeds");
        Directory.CreateDirectory(root);
        return root;
    }

    private string GetFeedRoot(FeedType feedType) => Path.Combine(_dataPath, "feeds", feedType.ToString().ToLowerInvariant());

    public static string GetPackageFileName(FeedType feedType, string normalizedPackageId, string version)
    {
        var leafName = normalizedPackageId.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? normalizedPackageId;
        return feedType switch
        {
            FeedType.NuGet => $"{leafName}.{version}.nupkg",
            FeedType.Npm => $"{leafName}-{version}.tgz",
            FeedType.Python => $"{leafName}-{version}.whl",
            FeedType.Maven => $"{leafName}-{version}.jar",
            _ => $"{leafName}-{version}.bin"
        };
    }
}
