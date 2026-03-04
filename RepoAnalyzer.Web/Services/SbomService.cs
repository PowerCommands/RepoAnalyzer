using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml.Linq;
using RepoAnalyzer.Web.Dto;
using RepoAnalyzer.Web.Models;
using RepoAnalyzer.Web.Services.Analysis.Logging;

namespace RepoAnalyzer.Web.Services;

public sealed class SbomService
{
    private const string IndexFileName = "index.json";
    private static readonly SemaphoreSlim IndexLock = new(1, 1);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true
    };

    private readonly AppDataService _data;
    private readonly IAnalysisLog _log;
    private readonly string _sbomRootPath;
    private readonly string _indexPath;

    public SbomService(AppDataService data, IAnalysisLog log, IConfiguration configuration)
    {
        _data = data;
        _log = log;
        var dataPath = configuration["DataPath"] ?? "/app/data";
        _sbomRootPath = Path.Combine(dataPath, "sbom");
        _indexPath = Path.Combine(_sbomRootPath, IndexFileName);
        Directory.CreateDirectory(_sbomRootPath);
    }

    public async Task<SbomFileResponse> CreateAsync(SbomCreateRequest request, CancellationToken ct = default)
    {
        var specVersion = NormalizeSpecVersion(request.SpecVersion);
        var outputType = NormalizeOutputType(request.OutputType);
        var format = NormalizeFormat(request.Format);

        var connections = await _data.GetConnectionsAsync(ct);
        var repositories = await _data.GetRepositoriesAsync(ct);
        var snapshots = await _data.GetSnapshotsAsync(ct);

        var connection = connections.FirstOrDefault(x => x.Id == request.ConnectionId)
            ?? throw new InvalidOperationException("Connection was not found.");
        var repository = repositories.FirstOrDefault(x => x.Id == request.RepositoryId)
            ?? throw new InvalidOperationException("Repository was not found.");

        if (!string.Equals(repository.ConnectionId, request.ConnectionId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The selected repository does not belong to the selected connection.");
        }

        var snapshot = snapshots
            .Where(x => x.RepositoryId == repository.Id)
            .OrderByDescending(x => x.AnalyzedAtUtc)
            .FirstOrDefault()
            ?? throw new InvalidOperationException("No analysis snapshot exists for the selected repository.");

        var logContext = new AnalysisLogContext
        {
            AnalysisRunId = $"sbom-{Guid.NewGuid():N}",
            ConnectionId = connection.Id,
            ProviderType = connection.Type.ToString(),
            RepositoryId = repository.Id,
            RepositoryName = repository.Name
        };

        try
        {
            await _log.InfoAsync(
                "SBOM",
                "Starting SBOM generation.",
                logContext,
                new Dictionary<string, object?>
                {
                    ["connectionId"] = connection.Id,
                    ["repositoryId"] = repository.Id,
                    ["specVersion"] = specVersion,
                    ["outputType"] = outputType,
                    ["format"] = format
                },
                ct);

            var mapped = BuildMappedComponents(snapshot.Components);
            var rootRef = $"repo:{repository.Id}";
            var rootVersion = snapshot.AnalyzedAtUtc.ToString("yyyyMMddHHmmss");
            var timestamp = DateTimeOffset.UtcNow;

            if (string.Equals(outputType, "DependencyGraph", StringComparison.Ordinal))
            {
                await _log.InfoAsync(
                    "SBOM",
                    "Dependency relationships are not directly available from manifests. Root component was linked to all exported components.",
                    logContext,
                    new Dictionary<string, object?>
                    {
                        ["rootDependsOnCount"] = mapped.Count
                    },
                    ct);
            }

            var payload = string.Equals(format, "XML", StringComparison.Ordinal)
                ? BuildXmlPayload(specVersion, outputType, repository.Name, rootRef, rootVersion, timestamp, mapped)
                : BuildJsonPayload(specVersion, outputType, repository.Name, rootRef, rootVersion, timestamp, mapped);

            var extension = string.Equals(format, "XML", StringComparison.Ordinal) ? "xml" : "json";
            var safeRepoName = SanitizeFileToken(repository.Name);
            var fileTimestamp = timestamp.ToString("yyyyMMdd-HHmmssfff");
            var typeToken = string.Equals(outputType, "DependencyGraph", StringComparison.Ordinal) ? "dependencygraph" : "flat";
            var formatToken = extension;
            var fileName = $"{safeRepoName}__cyclonedx-{specVersion}__{typeToken}__{formatToken}__{fileTimestamp}.{extension}";
            var absolutePath = Path.Combine(_sbomRootPath, fileName);
            var relativePath = fileName;

            await File.WriteAllTextAsync(absolutePath, payload, ct);
            var size = new FileInfo(absolutePath).Length;

            var entry = new SbomIndexEntry
            {
                Id = Guid.NewGuid().ToString("N"),
                ConnectionId = connection.Id,
                RepositoryId = repository.Id,
                RepositoryName = repository.Name,
                SpecVersion = specVersion,
                OutputType = outputType,
                Format = format,
                FileName = fileName,
                RelativePath = relativePath,
                SizeBytes = size,
                CreatedAtUtc = timestamp,
                ComponentCount = mapped.Count
            };

            await SaveIndexEntryAsync(entry, ct);

            await _log.InfoAsync(
                "SBOM",
                "SBOM file written.",
                logContext,
                new Dictionary<string, object?>
                {
                    ["componentCount"] = mapped.Count,
                    ["purlMissingCount"] = mapped.Count(x => string.IsNullOrWhiteSpace(x.Purl)),
                    ["filePath"] = absolutePath,
                    ["fileSizeBytes"] = size
                },
                ct);

            return ToResponse(entry);
        }
        catch (Exception ex)
        {
            await _log.ErrorAsync(
                "SBOM",
                "SBOM generation failed.",
                logContext,
                ex,
                new Dictionary<string, object?>
                {
                    ["repositoryId"] = repository.Id,
                    ["specVersion"] = specVersion,
                    ["outputType"] = outputType,
                    ["format"] = format
                },
                ct);
            throw;
        }
    }

    public async Task<List<SbomFileResponse>> ListAsync(string? connectionId, string? repositoryId, CancellationToken ct = default)
    {
        var entries = await ReadIndexAsync(ct);

        if (!string.IsNullOrWhiteSpace(connectionId))
        {
            entries = entries.Where(x => string.Equals(x.ConnectionId, connectionId, StringComparison.Ordinal)).ToList();
        }

        if (!string.IsNullOrWhiteSpace(repositoryId))
        {
            entries = entries.Where(x => string.Equals(x.RepositoryId, repositoryId, StringComparison.Ordinal)).ToList();
        }

        return entries
            .OrderByDescending(x => x.CreatedAtUtc)
            .Select(ToResponse)
            .ToList();
    }

    public async Task<(SbomFileResponse Meta, byte[] Bytes)?> ReadFileAsync(string id, CancellationToken ct = default)
    {
        var safeId = ValidateId(id);
        var entries = await ReadIndexAsync(ct);
        var entry = entries.FirstOrDefault(x => string.Equals(x.Id, safeId, StringComparison.Ordinal));
        if (entry is null)
        {
            return null;
        }

        var fullPath = GetFullPath(entry.RelativePath);
        if (!File.Exists(fullPath))
        {
            return null;
        }

        var bytes = await File.ReadAllBytesAsync(fullPath, ct);
        return (ToResponse(entry), bytes);
    }

    public async Task<bool> DeleteAsync(string id, CancellationToken ct = default)
    {
        var safeId = ValidateId(id);
        await IndexLock.WaitAsync(ct);
        try
        {
            var entries = await ReadIndexUnsafeAsync(ct);
            var entry = entries.FirstOrDefault(x => string.Equals(x.Id, safeId, StringComparison.Ordinal));
            if (entry is null)
            {
                return false;
            }

            var fullPath = GetFullPath(entry.RelativePath);
            if (File.Exists(fullPath))
            {
                File.Delete(fullPath);
            }

            entries.RemoveAll(x => string.Equals(x.Id, safeId, StringComparison.Ordinal));
            await WriteIndexUnsafeAsync(entries, ct);
            return true;
        }
        finally
        {
            IndexLock.Release();
        }
    }

    public async Task<long> GetTotalSbomBytesAsync(CancellationToken ct = default)
    {
        var entries = await ReadIndexAsync(ct);
        return entries.Sum(x => x.SizeBytes);
    }

    public async Task<int> GetSbomCountAsync(CancellationToken ct = default)
    {
        var entries = await ReadIndexAsync(ct);
        return entries.Count;
    }

    private static string NormalizeSpecVersion(string? value)
        => string.Equals(value, "1.6", StringComparison.Ordinal) ? "1.6" : "1.7";

    private static string NormalizeOutputType(string? value)
        => string.Equals(value, "DependencyGraph", StringComparison.Ordinal) ? "DependencyGraph" : "Flat";

    private static string NormalizeFormat(string? value)
        => string.Equals(value, "XML", StringComparison.OrdinalIgnoreCase) ? "XML" : "JSON";

    private static string ValidateId(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            throw new InvalidOperationException("SBOM id is required.");
        }

        foreach (var ch in id)
        {
            if (!(char.IsLetterOrDigit(ch) || ch == '-'))
            {
                throw new InvalidOperationException("Invalid SBOM id.");
            }
        }

        return id;
    }

    private static string SanitizeFileToken(string value)
    {
        var chars = value
            .Trim()
            .Select(ch => char.IsLetterOrDigit(ch) ? ch : '_')
            .ToArray();
        var token = new string(chars).Trim('_');
        return string.IsNullOrWhiteSpace(token) ? "repository" : token;
    }

    private static string BuildJsonPayload(
        string specVersion,
        string outputType,
        string repositoryName,
        string rootRef,
        string rootVersion,
        DateTimeOffset generatedAtUtc,
        List<MappedComponent> components)
    {
        var componentRows = components.Select(c => new Dictionary<string, object?>
        {
            ["type"] = "library",
            ["bom-ref"] = c.BomRef,
            ["name"] = c.Name,
            ["version"] = c.Version,
            ["purl"] = c.Purl
        }).ToList();

        var bom = new Dictionary<string, object?>
        {
            ["bomFormat"] = "CycloneDX",
            ["specVersion"] = specVersion,
            ["serialNumber"] = $"urn:uuid:{Guid.NewGuid()}",
            ["version"] = 1,
            ["metadata"] = new Dictionary<string, object?>
            {
                ["timestamp"] = generatedAtUtc.UtcDateTime.ToString("O"),
                ["component"] = new Dictionary<string, object?>
                {
                    ["type"] = "application",
                    ["bom-ref"] = rootRef,
                    ["name"] = repositoryName,
                    ["version"] = rootVersion
                }
            },
            ["components"] = componentRows
        };

        if (string.Equals(outputType, "DependencyGraph", StringComparison.Ordinal))
        {
            var dependencies = new List<Dictionary<string, object?>>
            {
                new()
                {
                    ["ref"] = rootRef,
                    ["dependsOn"] = components.Select(x => x.BomRef).ToList()
                }
            };

            bom["dependencies"] = dependencies;
        }

        return JsonSerializer.Serialize(bom, JsonOptions);
    }

    private static string BuildXmlPayload(
        string specVersion,
        string outputType,
        string repositoryName,
        string rootRef,
        string rootVersion,
        DateTimeOffset generatedAtUtc,
        List<MappedComponent> components)
    {
        var ns = XNamespace.Get($"http://cyclonedx.org/schema/bom/{specVersion}");
        var bom = new XElement(ns + "bom",
            new XAttribute("serialNumber", $"urn:uuid:{Guid.NewGuid()}"),
            new XAttribute("version", "1"),
            new XElement(ns + "metadata",
                new XElement(ns + "timestamp", generatedAtUtc.UtcDateTime.ToString("O")),
                new XElement(ns + "component",
                    new XAttribute("type", "application"),
                    new XAttribute("bom-ref", rootRef),
                    new XElement(ns + "name", repositoryName),
                    new XElement(ns + "version", rootVersion))));

        var componentsElement = new XElement(ns + "components");
        foreach (var component in components)
        {
            var componentElement = new XElement(ns + "component",
                new XAttribute("type", "library"),
                new XAttribute("bom-ref", component.BomRef),
                new XElement(ns + "name", component.Name),
                new XElement(ns + "version", component.Version));
            if (!string.IsNullOrWhiteSpace(component.Purl))
            {
                componentElement.Add(new XElement(ns + "purl", component.Purl));
            }
            componentsElement.Add(componentElement);
        }

        bom.Add(componentsElement);

        if (string.Equals(outputType, "DependencyGraph", StringComparison.Ordinal))
        {
            var depsElement = new XElement(ns + "dependencies",
                new XElement(ns + "dependency",
                    new XAttribute("ref", rootRef),
                    components.Select(x => new XElement(ns + "dependency", new XAttribute("ref", x.BomRef)))));
            bom.Add(depsElement);
        }

        var doc = new XDocument(new XDeclaration("1.0", "utf-8", null), bom);
        return doc.ToString();
    }

    private static List<MappedComponent> BuildMappedComponents(List<Component> components)
    {
        var unique = components
            .Where(x => !string.IsNullOrWhiteSpace(x.Name))
            .GroupBy(x => $"{x.Ecosystem}|{x.Name}|{x.Version}", StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderByDescending(x => x.CapturedAtUtc).First())
            .OrderBy(x => x.Ecosystem, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.Version, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var mapped = new List<MappedComponent>(unique.Count);
        foreach (var component in unique)
        {
            var bomRef = $"pkg-{component.Id}";
            mapped.Add(new MappedComponent
            {
                BomRef = bomRef,
                Name = component.Name,
                Version = string.IsNullOrWhiteSpace(component.Version) ? "unknown" : component.Version,
                Purl = BuildPurl(component)
            });
        }

        return mapped;
    }

    private static string? BuildPurl(Component component)
    {
        var ecosystem = (component.Ecosystem ?? string.Empty).Trim().ToLowerInvariant();
        var name = (component.Name ?? string.Empty).Trim();
        var version = (component.Version ?? string.Empty).Trim();

        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(version))
        {
            return null;
        }

        if (ecosystem.Contains("nuget", StringComparison.Ordinal))
        {
            return $"pkg:nuget/{Uri.EscapeDataString(name)}@{Uri.EscapeDataString(version)}";
        }

        if (ecosystem.Contains("npm", StringComparison.Ordinal))
        {
            return $"pkg:npm/{Uri.EscapeDataString(name)}@{Uri.EscapeDataString(version)}";
        }

        if (ecosystem.Contains("maven", StringComparison.Ordinal))
        {
            var parts = name.Split(':', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2)
            {
                return $"pkg:maven/{Uri.EscapeDataString(parts[0])}/{Uri.EscapeDataString(parts[1])}@{Uri.EscapeDataString(version)}";
            }

            return $"pkg:maven/{Uri.EscapeDataString(name)}@{Uri.EscapeDataString(version)}";
        }

        if (ecosystem.Contains("pypi", StringComparison.Ordinal) || ecosystem.Contains("pip", StringComparison.Ordinal))
        {
            return $"pkg:pypi/{Uri.EscapeDataString(name)}@{Uri.EscapeDataString(version)}";
        }

        return null;
    }

    private async Task SaveIndexEntryAsync(SbomIndexEntry entry, CancellationToken ct)
    {
        await IndexLock.WaitAsync(ct);
        try
        {
            var entries = await ReadIndexUnsafeAsync(ct);
            entries.Add(entry);
            await WriteIndexUnsafeAsync(entries, ct);
        }
        finally
        {
            IndexLock.Release();
        }
    }

    private async Task<List<SbomIndexEntry>> ReadIndexAsync(CancellationToken ct)
    {
        await IndexLock.WaitAsync(ct);
        try
        {
            return await ReadIndexUnsafeAsync(ct);
        }
        finally
        {
            IndexLock.Release();
        }
    }

    private async Task<List<SbomIndexEntry>> ReadIndexUnsafeAsync(CancellationToken ct)
    {
        Directory.CreateDirectory(_sbomRootPath);
        if (!File.Exists(_indexPath))
        {
            return new List<SbomIndexEntry>();
        }

        await using var stream = new FileStream(_indexPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var entries = await JsonSerializer.DeserializeAsync<List<SbomIndexEntry>>(stream, JsonOptions, ct) ?? new List<SbomIndexEntry>();

        var cleaned = entries
            .Where(x => !string.IsNullOrWhiteSpace(x.Id) && !string.IsNullOrWhiteSpace(x.RelativePath))
            .Where(x => File.Exists(GetFullPath(x.RelativePath)))
            .OrderBy(x => x.CreatedAtUtc)
            .ToList();

        if (cleaned.Count != entries.Count)
        {
            await WriteIndexUnsafeAsync(cleaned, ct);
        }

        return cleaned;
    }

    private async Task WriteIndexUnsafeAsync(List<SbomIndexEntry> entries, CancellationToken ct)
    {
        Directory.CreateDirectory(_sbomRootPath);
        var tempPath = _indexPath + ".tmp";

        await using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            await JsonSerializer.SerializeAsync(stream, entries, JsonOptions, ct);
        }

        File.Move(tempPath, _indexPath, overwrite: true);
        if (File.Exists(tempPath))
        {
            File.Delete(tempPath);
        }
    }

    private string GetFullPath(string relativePath)
    {
        var candidate = Path.GetFullPath(Path.Combine(_sbomRootPath, relativePath));
        var root = Path.GetFullPath(_sbomRootPath);
        if (!candidate.StartsWith(root, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Invalid SBOM file path.");
        }

        return candidate;
    }

    private static SbomFileResponse ToResponse(SbomIndexEntry entry) => new()
    {
        Id = entry.Id,
        ConnectionId = entry.ConnectionId,
        RepositoryId = entry.RepositoryId,
        RepositoryName = entry.RepositoryName,
        SpecVersion = entry.SpecVersion,
        OutputType = entry.OutputType,
        Format = entry.Format,
        FileName = entry.FileName,
        SizeBytes = entry.SizeBytes,
        CreatedAtUtc = entry.CreatedAtUtc,
        ComponentCount = entry.ComponentCount
    };

    private sealed class SbomIndexEntry
    {
        public string Id { get; set; } = string.Empty;
        public string ConnectionId { get; set; } = string.Empty;
        public string RepositoryId { get; set; } = string.Empty;
        public string RepositoryName { get; set; } = string.Empty;
        public string SpecVersion { get; set; } = string.Empty;
        public string OutputType { get; set; } = string.Empty;
        public string Format { get; set; } = string.Empty;
        public string FileName { get; set; } = string.Empty;
        public string RelativePath { get; set; } = string.Empty;
        public long SizeBytes { get; set; }
        public DateTimeOffset CreatedAtUtc { get; set; }
        public int ComponentCount { get; set; }
    }

    private sealed class MappedComponent
    {
        public string BomRef { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Version { get; set; } = string.Empty;
        public string? Purl { get; set; }
    }
}
