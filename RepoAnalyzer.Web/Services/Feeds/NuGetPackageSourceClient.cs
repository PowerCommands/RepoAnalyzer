using System.Text.Json;

namespace RepoAnalyzer.Web.Services.Feeds;

public sealed class NuGetPackageSourceClient
{
    private readonly IHttpClientFactory _httpClientFactory;

    public NuGetPackageSourceClient(IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
    }

    public async Task<List<string>> GetVersionsAsync(string packageId, CancellationToken ct = default)
    {
        var normalizedPackageId = NormalizePackageId(packageId);
        var client = _httpClientFactory.CreateClient(nameof(NuGetPackageSourceClient));
        using var response = await client.GetAsync($"https://api.nuget.org/v3-flatcontainer/{normalizedPackageId}/index.json", ct);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        if (!document.RootElement.TryGetProperty("versions", out var versionsElement) || versionsElement.ValueKind != JsonValueKind.Array)
        {
            return new List<string>();
        }

        return versionsElement
            .EnumerateArray()
            .Select(x => x.GetString())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x!)
            .ToList();
    }

    public async Task<byte[]> DownloadPackageAsync(string packageId, string version, CancellationToken ct = default)
    {
        var normalizedPackageId = NormalizePackageId(packageId);
        var normalizedVersion = version.Trim().ToLowerInvariant();
        var client = _httpClientFactory.CreateClient(nameof(NuGetPackageSourceClient));
        using var response = await client.GetAsync($"https://api.nuget.org/v3-flatcontainer/{normalizedPackageId}/{normalizedVersion}/{normalizedPackageId}.{normalizedVersion}.nupkg", ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsByteArrayAsync(ct);
    }

    public static string NormalizePackageId(string packageId) => packageId.Trim().ToLowerInvariant();
}
