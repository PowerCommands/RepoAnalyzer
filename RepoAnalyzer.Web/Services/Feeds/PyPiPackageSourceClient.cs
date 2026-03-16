using System.Text.Json;
using System.Text.RegularExpressions;

namespace RepoAnalyzer.Web.Services.Feeds;

public sealed class PyPiPackageSourceClient
{
    private static readonly Regex NormalizePattern = new("[-_.]+", RegexOptions.Compiled);
    private readonly IHttpClientFactory _httpClientFactory;

    public PyPiPackageSourceClient(IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
    }

    public async Task<ProjectDocument> GetProjectDocumentAsync(string packageId, CancellationToken ct = default)
    {
        var normalizedPackageId = NormalizePackageId(packageId);
        if (string.IsNullOrWhiteSpace(normalizedPackageId))
        {
            throw new InvalidOperationException("Package ID is required.");
        }

        var client = _httpClientFactory.CreateClient(nameof(PyPiPackageSourceClient));
        var response = await client.GetAsync($"https://pypi.org/pypi/{Uri.EscapeDataString(packageId.Trim())}/json", ct);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        var root = document.RootElement;

        var latestVersion = root.TryGetProperty("info", out var infoElement) &&
                            infoElement.ValueKind == JsonValueKind.Object &&
                            infoElement.TryGetProperty("version", out var versionElement)
            ? versionElement.GetString()
            : null;

        var packageName = root.TryGetProperty("info", out infoElement) &&
                          infoElement.ValueKind == JsonValueKind.Object &&
                          infoElement.TryGetProperty("name", out var nameElement)
            ? nameElement.GetString() ?? packageId.Trim()
            : packageId.Trim();

        var versions = new Dictionary<string, List<ReleaseFile>>(StringComparer.OrdinalIgnoreCase);
        if (root.TryGetProperty("releases", out var releasesElement) && releasesElement.ValueKind == JsonValueKind.Object)
        {
            foreach (var release in releasesElement.EnumerateObject())
            {
                if (release.Value.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                var files = release.Value.EnumerateArray()
                    .Select(ToReleaseFile)
                    .Where(x => x is not null)
                    .Select(x => x!)
                    .ToList();

                versions[release.Name] = files;
            }
        }

        return new ProjectDocument
        {
            PackageId = packageName,
            NormalizedPackageId = normalizedPackageId,
            LatestVersion = latestVersion,
            Versions = versions
        };
    }

    public async Task<List<string>> GetVersionsAsync(string packageId, CancellationToken ct = default)
    {
        var document = await GetProjectDocumentAsync(packageId, ct);
        return document.Versions.Keys
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<SelectedRelease> GetReleaseAsync(string packageId, string version, CancellationToken ct = default)
    {
        var document = await GetProjectDocumentAsync(packageId, ct);
        if (!document.Versions.TryGetValue(version.Trim(), out var files) || files.Count == 0)
        {
            throw new InvalidOperationException($"Version '{version}' was not found for Python package '{packageId}'.");
        }

        var chosen = files
            .OrderByDescending(x => string.Equals(x.PackageType, "bdist_wheel", StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(x => string.Equals(x.PackageType, "sdist", StringComparison.OrdinalIgnoreCase))
            .ThenBy(x => x.FileName, StringComparer.OrdinalIgnoreCase)
            .First();

        return new SelectedRelease
        {
            PackageId = document.PackageId,
            NormalizedPackageId = document.NormalizedPackageId,
            Version = version.Trim(),
            File = chosen
        };
    }

    public async Task<byte[]> DownloadFileAsync(string url, CancellationToken ct = default)
    {
        var client = _httpClientFactory.CreateClient(nameof(PyPiPackageSourceClient));
        return await client.GetByteArrayAsync(url, ct);
    }

    public static string NormalizePackageId(string packageId)
        => NormalizePattern.Replace(packageId.Trim(), "-").ToLowerInvariant();

    private static ReleaseFile? ToReleaseFile(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var fileName = element.TryGetProperty("filename", out var fileNameElement) ? fileNameElement.GetString() : null;
        var url = element.TryGetProperty("url", out var urlElement) ? urlElement.GetString() : null;
        if (string.IsNullOrWhiteSpace(fileName) || string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        string? sha256 = null;
        if (element.TryGetProperty("digests", out var digestsElement) &&
            digestsElement.ValueKind == JsonValueKind.Object &&
            digestsElement.TryGetProperty("sha256", out var sha256Element))
        {
            sha256 = sha256Element.GetString();
        }

        return new ReleaseFile
        {
            FileName = fileName,
            Url = url,
            PackageType = element.TryGetProperty("packagetype", out var packageTypeElement) ? packageTypeElement.GetString() : null,
            PythonVersion = element.TryGetProperty("python_version", out var pythonVersionElement) ? pythonVersionElement.GetString() : null,
            RequiresPython = element.TryGetProperty("requires_python", out var requiresPythonElement) ? requiresPythonElement.GetString() : null,
            Sha256 = sha256,
            MetadataJson = element.GetRawText()
        };
    }

    public sealed class ProjectDocument
    {
        public string PackageId { get; set; } = string.Empty;
        public string NormalizedPackageId { get; set; } = string.Empty;
        public string? LatestVersion { get; set; }
        public Dictionary<string, List<ReleaseFile>> Versions { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    public sealed class SelectedRelease
    {
        public string PackageId { get; set; } = string.Empty;
        public string NormalizedPackageId { get; set; } = string.Empty;
        public string Version { get; set; } = string.Empty;
        public ReleaseFile File { get; set; } = new();
    }

    public sealed class ReleaseFile
    {
        public string FileName { get; set; } = string.Empty;
        public string Url { get; set; } = string.Empty;
        public string? PackageType { get; set; }
        public string? PythonVersion { get; set; }
        public string? RequiresPython { get; set; }
        public string? Sha256 { get; set; }
        public string MetadataJson { get; set; } = "{}";
    }
}
