using System.Text.Json;

namespace RepoAnalyzer.Web.Services.Feeds;

public sealed class NpmPackageSourceClient
{
    private readonly IHttpClientFactory _httpClientFactory;

    public NpmPackageSourceClient(IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
    }

    public async Task<PackageDocument> GetPackageDocumentAsync(string packageId, CancellationToken ct = default)
    {
        var normalizedPackageId = NormalizePackageId(packageId);
        if (string.IsNullOrWhiteSpace(normalizedPackageId))
        {
            throw new InvalidOperationException("Package ID is required.");
        }

        var client = _httpClientFactory.CreateClient(nameof(NpmPackageSourceClient));
        var response = await client.GetAsync($"https://registry.npmjs.org/{Uri.EscapeDataString(normalizedPackageId)}", ct);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

        if (!document.RootElement.TryGetProperty("versions", out var versionsElement) || versionsElement.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException("The npm registry response did not contain any versions.");
        }

        var latestVersion = document.RootElement.TryGetProperty("dist-tags", out var distTagsElement) &&
                            distTagsElement.ValueKind == JsonValueKind.Object &&
                            distTagsElement.TryGetProperty("latest", out var latestElement)
            ? latestElement.GetString()
            : null;

        var versions = new Dictionary<string, PackageVersionDocument>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in versionsElement.EnumerateObject())
        {
            var versionObject = property.Value;
            var version = versionObject.TryGetProperty("version", out var versionElement)
                ? versionElement.GetString()
                : property.Name;

            if (string.IsNullOrWhiteSpace(version))
            {
                continue;
            }

            string? tarballUrl = null;
            string? shasum = null;
            string? integrity = null;
            if (versionObject.TryGetProperty("dist", out var distElement) && distElement.ValueKind == JsonValueKind.Object)
            {
                tarballUrl = distElement.TryGetProperty("tarball", out var tarballElement) ? tarballElement.GetString() : null;
                shasum = distElement.TryGetProperty("shasum", out var shasumElement) ? shasumElement.GetString() : null;
                integrity = distElement.TryGetProperty("integrity", out var integrityElement) ? integrityElement.GetString() : null;
            }

            versions[version] = new PackageVersionDocument
            {
                Name = versionObject.TryGetProperty("name", out var nameElement) ? nameElement.GetString() ?? normalizedPackageId : normalizedPackageId,
                Version = version,
                Description = versionObject.TryGetProperty("description", out var descriptionElement) ? descriptionElement.GetString() : null,
                TarballUrl = tarballUrl,
                Shasum = shasum,
                Integrity = integrity,
                MetadataJson = versionObject.GetRawText()
            };
        }

        return new PackageDocument
        {
            PackageId = normalizedPackageId,
            LatestVersion = latestVersion,
            Versions = versions
        };
    }

    public async Task<List<string>> GetVersionsAsync(string packageId, CancellationToken ct = default)
    {
        var document = await GetPackageDocumentAsync(packageId, ct);
        return document.Versions.Keys
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<byte[]> DownloadTarballAsync(string tarballUrl, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(tarballUrl))
        {
            throw new InvalidOperationException("The npm package version did not contain a tarball URL.");
        }

        var client = _httpClientFactory.CreateClient(nameof(NpmPackageSourceClient));
        return await client.GetByteArrayAsync(tarballUrl, ct);
    }

    public static string NormalizePackageId(string packageId)
        => packageId.Trim().ToLowerInvariant();

    public sealed class PackageDocument
    {
        public string PackageId { get; set; } = string.Empty;
        public string? LatestVersion { get; set; }
        public Dictionary<string, PackageVersionDocument> Versions { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    public sealed class PackageVersionDocument
    {
        public string Name { get; set; } = string.Empty;
        public string Version { get; set; } = string.Empty;
        public string? Description { get; set; }
        public string? TarballUrl { get; set; }
        public string? Shasum { get; set; }
        public string? Integrity { get; set; }
        public string MetadataJson { get; set; } = "{}";
    }
}
