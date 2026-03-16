using System.Text.Json;
using RepoAnalyzer.Web.Dto;
using RepoAnalyzer.Web.Models;
using RepoAnalyzer.Web.Models.Enums;
using RepoAnalyzer.Web.Services.Analysis;

namespace RepoAnalyzer.Web.Services.Feeds;

public sealed class FeedScannerService : IFeedScannerService
{
    private readonly AppDataService _data;
    private readonly DotNetCliInspector _dotNetCliInspector;
    private readonly NodeCliInspector _nodeCliInspector;
    private readonly PythonCliInspector _pythonCliInspector;
    private readonly JavaCliInspector _javaCliInspector;
    private readonly SafeCliRunner _safeCliRunner;
    private readonly FeedStoragePathService _pathService;

    public FeedScannerService(
        AppDataService data,
        DotNetCliInspector dotNetCliInspector,
        NodeCliInspector nodeCliInspector,
        PythonCliInspector pythonCliInspector,
        JavaCliInspector javaCliInspector,
        SafeCliRunner safeCliRunner,
        FeedStoragePathService pathService)
    {
        _data = data;
        _dotNetCliInspector = dotNetCliInspector;
        _nodeCliInspector = nodeCliInspector;
        _pythonCliInspector = pythonCliInspector;
        _javaCliInspector = javaCliInspector;
        _safeCliRunner = safeCliRunner;
        _pathService = pathService;
    }

    public async Task<FeedPackageView> ScanVulnerabilitiesAsync(string id, CancellationToken ct = default)
    {
        var (package, packages, links) = await LoadAsync(id, ct);
        var scanResult = await AnalyzePackageAsync(package, ct);
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
        var scanResult = await AnalyzePackageAsync(package, ct);
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
            var scanResult = await AnalyzePackageAsync(package, ct);
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

    private async Task<(List<Finding> Vulnerabilities, List<Finding> Outdated)> AnalyzePackageAsync(FeedPackage package, CancellationToken ct)
    {
        return package.FeedType switch
        {
            FeedType.NuGet => ToFindingTuple(await AnalyzeNuGetPackageAsync(package, ct)),
            FeedType.Npm => await AnalyzeNpmPackageAsync(package, ct),
            FeedType.Python => await AnalyzePythonPackageAsync(package, ct),
            FeedType.Maven => ToFindingTuple(await AnalyzeMavenPackageAsync(package, ct)),
            _ => throw new InvalidOperationException($"Feed type '{package.FeedType}' is not supported yet.")
        };
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

    private async Task<(List<Finding> Vulnerabilities, List<Finding> Outdated)> AnalyzeNpmPackageAsync(FeedPackage package, CancellationToken ct)
    {
        var tempRoot = Path.Combine(_pathService.GetTempRoot(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);

        try
        {
            var packageJsonObject = new Dictionary<string, object?>
            {
                ["name"] = "repo-analyzer-feed-scan",
                ["version"] = "1.0.0",
                ["private"] = true,
                ["dependencies"] = new Dictionary<string, string>
                {
                    [package.PackageId] = package.Version
                }
            };

            var packageJsonContent = JsonSerializer.Serialize(packageJsonObject, new JsonSerializerOptions
            {
                WriteIndented = true
            });

            var packageJsonPath = Path.Combine(tempRoot, "package.json");
            await File.WriteAllTextAsync(packageJsonPath, packageJsonContent, ct);

            var lockResult = await _safeCliRunner.RunAsync(
                "npm",
                ["install", "--package-lock-only", "--ignore-scripts"],
                tempRoot,
                TimeSpan.FromSeconds(60),
                2_000_000,
                ct);

            string? packageLockContent = null;
            var packageLockPath = Path.Combine(tempRoot, "package-lock.json");
            if (lockResult.ExitCode == 0 && File.Exists(packageLockPath))
            {
                packageLockContent = await File.ReadAllTextAsync(packageLockPath, ct);
            }

            return await _nodeCliInspector.AnalyzeAsync(
                repositoryId: $"feed-{package.FeedType.ToString().ToLowerInvariant()}",
                projectId: package.Id,
                analysisPath: tempRoot,
                packageJsonPath: "package.json",
                packageJsonContent: packageJsonContent,
                packageLockContent: packageLockContent,
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

    private async Task<(List<Finding> Vulnerabilities, List<Finding> Outdated)> AnalyzePythonPackageAsync(FeedPackage package, CancellationToken ct)
    {
        var tempRoot = Path.Combine(_pathService.GetTempRoot(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);

        try
        {
            var requirementsContent = $"{package.PackageId}=={package.Version}{Environment.NewLine}";
            return await _pythonCliInspector.AnalyzeAsync(
                repositoryId: $"feed-{package.FeedType.ToString().ToLowerInvariant()}",
                projectId: package.Id,
                analysisPath: tempRoot,
                requirementsPath: "requirements.txt",
                requirementsContent: requirementsContent,
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

    private async Task<JavaScanResult> AnalyzeMavenPackageAsync(FeedPackage package, CancellationToken ct)
    {
        var tempRoot = Path.Combine(_pathService.GetTempRoot(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);

        try
        {
            var coordinate = MavenPackageSourceClient.ParsePackageId(package.PackageId);
            var pomContent =
                $$"""
                <project xmlns="http://maven.apache.org/POM/4.0.0"
                         xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance"
                         xsi:schemaLocation="http://maven.apache.org/POM/4.0.0 https://maven.apache.org/xsd/maven-4.0.0.xsd">
                  <modelVersion>4.0.0</modelVersion>
                  <groupId>repoanalyzer.feed</groupId>
                  <artifactId>feed-package-scan</artifactId>
                  <version>1.0.0</version>
                  <dependencies>
                    <dependency>
                      <groupId>{{coordinate.GroupId}}</groupId>
                      <artifactId>{{coordinate.ArtifactId}}</artifactId>
                      <version>{{package.Version}}</version>
                    </dependency>
                  </dependencies>
                </project>
                """;

            return await _javaCliInspector.AnalyzeAsync(
                repositoryId: $"feed-{package.FeedType.ToString().ToLowerInvariant()}",
                projectId: package.Id,
                analysisPath: tempRoot,
                pomRelativePath: "pom.xml",
                pomContent: pomContent,
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

    private static (List<Finding> Vulnerabilities, List<Finding> Outdated) ToFindingTuple(DotNetScanResult result)
        => (result.Vulnerabilities, result.Outdated);

    private static (List<Finding> Vulnerabilities, List<Finding> Outdated) ToFindingTuple(JavaScanResult result)
        => (result.Vulnerabilities, result.Outdated);
}
