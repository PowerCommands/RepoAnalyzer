using System.Xml.Linq;

namespace RepoAnalyzer.Web.Services.Feeds;

public sealed class MavenPackageSourceClient
{
    private readonly IHttpClientFactory _httpClientFactory;

    public MavenPackageSourceClient(IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
    }

    public async Task<MavenVersionsDocument> GetVersionsAsync(string packageId, CancellationToken ct = default)
    {
        var coordinate = ParsePackageId(packageId);
        var client = _httpClientFactory.CreateClient(nameof(MavenPackageSourceClient));
        var metadataUrl = $"{GetBasePackageUrl(coordinate)}/maven-metadata.xml";
        using var stream = await client.GetStreamAsync(metadataUrl, ct);
        var document = XDocument.Load(stream);

        var versioning = document.Root?.Elements().FirstOrDefault(x => x.Name.LocalName == "versioning");
        var latest = versioning?.Elements().FirstOrDefault(x => x.Name.LocalName is "latest" or "release")?.Value?.Trim();
        var versions = versioning?
            .Elements()
            .FirstOrDefault(x => x.Name.LocalName == "versions")?
            .Elements()
            .Where(x => x.Name.LocalName == "version")
            .Select(x => x.Value.Trim())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToList()
            ?? new List<string>();

        return new MavenVersionsDocument
        {
            PackageId = $"{coordinate.GroupId}:{coordinate.ArtifactId}",
            NormalizedPackageId = NormalizePackageId(packageId),
            LatestVersion = latest,
            Versions = versions
        };
    }

    public async Task<MavenReleaseDocument> GetReleaseAsync(string packageId, string version, CancellationToken ct = default)
    {
        var coordinate = ParsePackageId(packageId);
        var normalizedVersion = version.Trim();
        var client = _httpClientFactory.CreateClient(nameof(MavenPackageSourceClient));

        var pomFileName = $"{coordinate.ArtifactId}-{normalizedVersion}.pom";
        var pomUrl = $"{GetBasePackageUrl(coordinate)}/{Uri.EscapeDataString(normalizedVersion)}/{pomFileName}";
        var pomContent = await client.GetStringAsync(pomUrl, ct);
        var pomDocument = XDocument.Parse(pomContent);

        var packaging = pomDocument.Root?.Elements().FirstOrDefault(x => x.Name.LocalName == "packaging")?.Value?.Trim();
        if (string.IsNullOrWhiteSpace(packaging))
        {
            packaging = "jar";
        }

        var description = pomDocument.Root?.Elements().FirstOrDefault(x => x.Name.LocalName == "description")?.Value?.Trim();
        var artifactFileName = $"{coordinate.ArtifactId}-{normalizedVersion}.{packaging}";
        var artifactUrl = $"{GetBasePackageUrl(coordinate)}/{Uri.EscapeDataString(normalizedVersion)}/{artifactFileName}";

        return new MavenReleaseDocument
        {
            PackageId = $"{coordinate.GroupId}:{coordinate.ArtifactId}",
            NormalizedPackageId = NormalizePackageId(packageId),
            GroupId = coordinate.GroupId,
            ArtifactId = coordinate.ArtifactId,
            Version = normalizedVersion,
            Packaging = packaging,
            Description = description,
            PomFileName = pomFileName,
            PomUrl = pomUrl,
            PomContent = pomContent,
            ArtifactFileName = artifactFileName,
            ArtifactUrl = artifactUrl
        };
    }

    public async Task<byte[]> DownloadFileAsync(string url, CancellationToken ct = default)
    {
        var client = _httpClientFactory.CreateClient(nameof(MavenPackageSourceClient));
        return await client.GetByteArrayAsync(url, ct);
    }

    public static string NormalizePackageId(string packageId)
    {
        var parsed = ParsePackageId(packageId);
        return $"{parsed.GroupId.ToLowerInvariant()}:{parsed.ArtifactId.ToLowerInvariant()}";
    }

    public static MavenCoordinate ParsePackageId(string packageId)
    {
        var value = packageId.Trim();
        var parts = value.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != 2)
        {
            throw new InvalidOperationException("Maven package ID must use 'groupId:artifactId'.");
        }

        return new MavenCoordinate
        {
            GroupId = parts[0],
            ArtifactId = parts[1]
        };
    }

    public static string GetGroupPath(string groupId) => groupId.Replace('.', '/');

    private static string GetBasePackageUrl(MavenCoordinate coordinate)
        => $"https://repo1.maven.org/maven2/{GetGroupPath(coordinate.GroupId)}/{coordinate.ArtifactId}";

    public sealed class MavenCoordinate
    {
        public string GroupId { get; set; } = string.Empty;
        public string ArtifactId { get; set; } = string.Empty;
    }

    public sealed class MavenVersionsDocument
    {
        public string PackageId { get; set; } = string.Empty;
        public string NormalizedPackageId { get; set; } = string.Empty;
        public string? LatestVersion { get; set; }
        public List<string> Versions { get; set; } = new();
    }

    public sealed class MavenReleaseDocument
    {
        public string PackageId { get; set; } = string.Empty;
        public string NormalizedPackageId { get; set; } = string.Empty;
        public string GroupId { get; set; } = string.Empty;
        public string ArtifactId { get; set; } = string.Empty;
        public string Version { get; set; } = string.Empty;
        public string Packaging { get; set; } = "jar";
        public string? Description { get; set; }
        public string PomFileName { get; set; } = string.Empty;
        public string PomUrl { get; set; } = string.Empty;
        public string PomContent { get; set; } = string.Empty;
        public string ArtifactFileName { get; set; } = string.Empty;
        public string ArtifactUrl { get; set; } = string.Empty;
    }
}
