using RepoAnalyzer.Web.Dto;
using RepoAnalyzer.Web.Models;
using RepoAnalyzer.Web.Models.Enums;
using RepoAnalyzer.Web.Services.Analysis;

namespace RepoAnalyzer.Web.Services.Feeds;

public sealed class FeedScannerService : IFeedScannerService
{
    private readonly AppDataService _data;
    private readonly DotNetCliInspector _dotNetCliInspector;
    private readonly FeedStoragePathService _pathService;

    public FeedScannerService(
        AppDataService data,
        DotNetCliInspector dotNetCliInspector,
        FeedStoragePathService pathService)
    {
        _data = data;
        _dotNetCliInspector = dotNetCliInspector;
        _pathService = pathService;
    }

    public async Task<FeedPackageView> ScanVulnerabilitiesAsync(string id, CancellationToken ct = default)
    {
        var (package, packages, links) = await LoadAsync(id, ct);
        if (package.FeedType != FeedType.NuGet)
        {
            throw new InvalidOperationException($"Feed type '{package.FeedType}' is not supported yet.");
        }

        var scanResult = await AnalyzeNuGetPackageAsync(package, ct);
        var vulnerability = scanResult.Vulnerabilities
            .Where(x => string.Equals(x.PackageName, package.PackageId, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(x => SeverityRank(x.Severity))
            .FirstOrDefault();

        package.VulnerabilityStatus = vulnerability is null
            ? "NoKnownVulnerabilities"
            : string.IsNullOrWhiteSpace(vulnerability.Severity)
                ? "Vulnerable"
                : $"Vulnerable ({vulnerability.Severity})";
        package.LastScanUtc = DateTimeOffset.UtcNow;

        await _data.SaveFeedPackagesAsync(packages, ct);
        return FeedPackageMapper.ToView(package, links);
    }

    public async Task<FeedPackageView> CheckOutdatedAsync(string id, CancellationToken ct = default)
    {
        var (package, packages, links) = await LoadAsync(id, ct);
        if (package.FeedType != FeedType.NuGet)
        {
            throw new InvalidOperationException($"Feed type '{package.FeedType}' is not supported yet.");
        }

        var scanResult = await AnalyzeNuGetPackageAsync(package, ct);
        var outdated = scanResult.Outdated
            .FirstOrDefault(x => string.Equals(x.PackageName, package.PackageId, StringComparison.OrdinalIgnoreCase));

        package.LatestKnownVersion = outdated?.FixedVersion ?? package.Version;
        package.OutdatedStatus = outdated is null ? "UpToDate" : "Outdated";
        package.LastScanUtc = DateTimeOffset.UtcNow;

        await _data.SaveFeedPackagesAsync(packages, ct);
        return FeedPackageMapper.ToView(package, links);
    }

    public async Task<List<FeedPackageView>> ScanAllAsync(FeedType feedType, CancellationToken ct = default)
    {
        var packages = await _data.GetFeedPackagesAsync(ct);
        var links = await _data.GetComponentFeedPackageLinksAsync(ct);
        var selectedPackages = packages
            .Where(x => x.FeedType == feedType)
            .OrderBy(x => x.PackageId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.Version, StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var package in selectedPackages)
        {
            if (package.FeedType != FeedType.NuGet)
            {
                continue;
            }

            var scanResult = await AnalyzeNuGetPackageAsync(package, ct);
            var vulnerability = scanResult.Vulnerabilities
                .Where(x => string.Equals(x.PackageName, package.PackageId, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(x => SeverityRank(x.Severity))
                .FirstOrDefault();
            var outdated = scanResult.Outdated
                .FirstOrDefault(x => string.Equals(x.PackageName, package.PackageId, StringComparison.OrdinalIgnoreCase));

            package.VulnerabilityStatus = vulnerability is null
                ? "NoKnownVulnerabilities"
                : string.IsNullOrWhiteSpace(vulnerability.Severity)
                    ? "Vulnerable"
                    : $"Vulnerable ({vulnerability.Severity})";
            package.LatestKnownVersion = outdated?.FixedVersion ?? package.Version;
            package.OutdatedStatus = outdated is null ? "UpToDate" : "Outdated";
            package.LastScanUtc = DateTimeOffset.UtcNow;
        }

        await _data.SaveFeedPackagesAsync(packages, ct);

        return selectedPackages
            .Select(x => FeedPackageMapper.ToView(x, links))
            .ToList();
    }

    private async Task<(FeedPackage Package, List<FeedPackage> Packages, List<ComponentFeedPackageLink> Links)> LoadAsync(string id, CancellationToken ct)
    {
        var packages = await _data.GetFeedPackagesAsync(ct);
        var links = await _data.GetComponentFeedPackageLinksAsync(ct);
        var package = packages.FirstOrDefault(x => string.Equals(x.Id, id, StringComparison.Ordinal))
            ?? throw new InvalidOperationException("Feed package was not found.");
        return (package, packages, links);
    }

    private async Task<DotNetScanResult> AnalyzeNuGetPackageAsync(FeedPackage package, CancellationToken ct)
    {
        var tempRoot = Path.Combine(_pathService.GetTempRoot(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);

        try
        {
            var projectContent =
                $$"""
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net8.0</TargetFramework>
                  </PropertyGroup>
                  <ItemGroup>
                    <PackageReference Include="{{package.PackageId}}" Version="{{package.Version}}" />
                  </ItemGroup>
                </Project>
                """;

            return await _dotNetCliInspector.AnalyzeAsync(
                repositoryId: $"feed-{package.FeedType.ToString().ToLowerInvariant()}",
                projectId: package.Id,
                analysisPath: tempRoot,
                projectRelativePath: "FeedPackageScan.csproj",
                projectContent: projectContent,
                extraFiles: new Dictionary<string, string>(),
                ct);
        }
        finally
        {
            try
            {
                if (Directory.Exists(tempRoot))
                {
                    Directory.Delete(tempRoot, recursive: true);
                }
            }
            catch
            {
                // Best effort cleanup.
            }
        }
    }

    private static int SeverityRank(string? severity) => severity?.ToUpperInvariant() switch
    {
        "CRITICAL" => 5,
        "HIGH" => 4,
        "MODERATE" => 3,
        "MEDIUM" => 3,
        "LOW" => 2,
        "UNKNOWN" => 1,
        _ => 0
    };
}
