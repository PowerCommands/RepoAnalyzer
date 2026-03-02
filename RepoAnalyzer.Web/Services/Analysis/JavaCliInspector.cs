using System.Text.Json;
using System.Text.RegularExpressions;
using RepoAnalyzer.Web.Models;
using RepoAnalyzer.Web.Models.Enums;

namespace RepoAnalyzer.Web.Services.Analysis;

public sealed class JavaCliInspector
{
    private const int PreviewLimit = 2000;
    private static readonly Regex OutdatedLineRegex = new(
        @"(?<ga>[A-Za-z0-9_.\-]+:[A-Za-z0-9_.\-]+)\s+.*?(?<current>[A-Za-z0-9_.+\-]+)\s*->\s*(?<latest>[A-Za-z0-9_.+\-]+)",
        RegexOptions.Compiled);

    private readonly SafeCliRunner _cliRunner;
    private readonly ILogger<JavaCliInspector> _logger;

    public JavaCliInspector(SafeCliRunner cliRunner, ILogger<JavaCliInspector> logger)
    {
        _cliRunner = cliRunner;
        _logger = logger;
    }

    public async Task<JavaScanResult> AnalyzeAsync(
        string repositoryId,
        string projectId,
        string analysisPath,
        string pomRelativePath,
        string pomContent,
        CancellationToken ct = default)
    {
        var result = new JavaScanResult();

        var pomFullPath = Path.Combine(analysisPath, pomRelativePath.Replace('/', Path.DirectorySeparatorChar));
        var projectDir = Path.GetDirectoryName(pomFullPath) ?? analysisPath;
        Directory.CreateDirectory(projectDir);
        await File.WriteAllTextAsync(pomFullPath, pomContent, ct);

        var updatesFile = Path.Combine(projectDir, ".maven-dependency-updates.txt");
        var vulnOutDir = Path.Combine(projectDir, ".dependency-check");
        Directory.CreateDirectory(vulnOutDir);
        var vulnJsonPath = Path.Combine(vulnOutDir, "dependency-check-report.json");

        try
        {
            var outdatedArgs = new[]
            {
                "-B",
                "-ntp",
                "-f",
                pomFullPath,
                "versions:display-dependency-updates",
                "-DprocessDependencyManagement=true",
                "-DgenerateBackupPoms=false",
                $"-DoutputFile={updatesFile}"
            };

            var outdatedRun = await _cliRunner.RunAsync(
                "mvn",
                outdatedArgs,
                analysisPath,
                TimeSpan.FromSeconds(90),
                2_000_000,
                ct);

            result.OutdatedCommand = $"mvn {string.Join(' ', outdatedArgs.Select(QuoteArg))}";
            result.OutdatedExitCode = outdatedRun.ExitCode;
            result.OutdatedStdOutPreview = ToPreview(outdatedRun.StdOut);
            result.OutdatedStdErrPreview = ToPreview(outdatedRun.StdErr);

            if (File.Exists(updatesFile))
            {
                var updatesText = await File.ReadAllTextAsync(updatesFile, ct);
                result.Outdated.AddRange(ParseOutdated(updatesText, repositoryId, projectId));
            }
            else
            {
                result.Outdated.AddRange(ParseOutdated(outdatedRun.StdOut, repositoryId, projectId));
            }

            var vulnerableArgs = new[]
            {
                "-B",
                "-ntp",
                "-f",
                pomFullPath,
                "org.owasp:dependency-check-maven:check",
                "-Dformat=JSON",
                "-DfailOnError=false",
                "-DskipProvidedScope=true",
                "-DskipTestScope=true",
                $"-DoutputDirectory={vulnOutDir}"
            };

            var vulnerableRun = await _cliRunner.RunAsync(
                "mvn",
                vulnerableArgs,
                analysisPath,
                TimeSpan.FromSeconds(240),
                2_500_000,
                ct);

            result.VulnerableCommand = $"mvn {string.Join(' ', vulnerableArgs.Select(QuoteArg))}";
            result.VulnerableExitCode = vulnerableRun.ExitCode;
            result.VulnerableStdOutPreview = ToPreview(vulnerableRun.StdOut);
            result.VulnerableStdErrPreview = ToPreview(vulnerableRun.StdErr);

            if (File.Exists(vulnJsonPath))
            {
                var vulnJson = await File.ReadAllTextAsync(vulnJsonPath, ct);
                result.Vulnerabilities.AddRange(ParseDependencyCheckReport(vulnJson, repositoryId, projectId));
            }
        }
        catch (Exception ex)
        {
            result.Error = ex.Message;
            _logger.LogWarning(ex, "Maven vulnerability check failed for {PomPath}", pomRelativePath);
        }

        return result;
    }

    private static IEnumerable<Finding> ParseOutdated(string text, string repositoryId, string projectId)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            yield break;
        }

        foreach (Match match in OutdatedLineRegex.Matches(text))
        {
            var package = match.Groups["ga"].Value.Trim();
            var current = match.Groups["current"].Value.Trim();
            var latest = match.Groups["latest"].Value.Trim();
            if (string.IsNullOrWhiteSpace(package) ||
                string.IsNullOrWhiteSpace(current) ||
                string.IsNullOrWhiteSpace(latest) ||
                string.Equals(current, latest, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            yield return new Finding
            {
                Type = FindingType.Outdated,
                Ecosystem = "Maven",
                PackageName = package,
                InstalledVersion = current,
                FixedVersion = latest,
                Severity = "Unknown",
                SourceTool = "maven-versions-plugin",
                ProjectId = projectId,
                RepositoryId = repositoryId
            };
        }
    }

    private static IEnumerable<Finding> ParseDependencyCheckReport(string json, string repositoryId, string projectId)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("dependencies", out var dependencies) || dependencies.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        foreach (var dependency in dependencies.EnumerateArray())
        {
            if (!dependency.TryGetProperty("vulnerabilities", out var vulnerabilities) || vulnerabilities.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            var packageName = ExtractPackageName(dependency);
            var installedVersion = ExtractInstalledVersion(dependency);

            foreach (var vulnerability in vulnerabilities.EnumerateArray())
            {
                var severity = NormalizeSeverity(
                    vulnerability.TryGetProperty("severity", out var sevEl) ? sevEl.GetString() :
                    vulnerability.TryGetProperty("cvssv3", out var cvss3) && cvss3.TryGetProperty("baseSeverity", out var baseSev3) ? baseSev3.GetString() :
                    vulnerability.TryGetProperty("cvssv2", out var cvss2) && cvss2.TryGetProperty("severity", out var baseSev2) ? baseSev2.GetString() :
                    null);

                var advisory = vulnerability.TryGetProperty("url", out var urlEl)
                    ? urlEl.GetString()
                    : vulnerability.TryGetProperty("source", out var sourceEl) ? sourceEl.GetString() : null;

                yield return new Finding
                {
                    Type = FindingType.Vulnerability,
                    Ecosystem = "Maven",
                    PackageName = packageName,
                    InstalledVersion = installedVersion,
                    FixedVersion = null,
                    Severity = severity,
                    Advisory = advisory,
                    SourceTool = "owasp-dependency-check",
                    ProjectId = projectId,
                    RepositoryId = repositoryId
                };
            }
        }
    }

    private static string ExtractPackageName(JsonElement dependency)
    {
        if (dependency.TryGetProperty("packages", out var packagesEl) && packagesEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var package in packagesEl.EnumerateArray())
            {
                if (!package.TryGetProperty("id", out var idEl))
                {
                    continue;
                }

                var id = idEl.GetString();
                if (string.IsNullOrWhiteSpace(id))
                {
                    continue;
                }

                // Example purl: pkg:maven/group/artifact@1.2.3
                if (id.StartsWith("pkg:maven/", StringComparison.OrdinalIgnoreCase))
                {
                    var purl = id["pkg:maven/".Length..];
                    var atIndex = purl.IndexOf('@');
                    var withoutVersion = atIndex >= 0 ? purl[..atIndex] : purl;
                    return withoutVersion.Replace('/', ':');
                }

                return id;
            }
        }

        if (dependency.TryGetProperty("fileName", out var fileName))
        {
            return fileName.GetString() ?? "unknown";
        }

        return "unknown";
    }

    private static string? ExtractInstalledVersion(JsonElement dependency)
    {
        if (dependency.TryGetProperty("packages", out var packagesEl) && packagesEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var package in packagesEl.EnumerateArray())
            {
                if (!package.TryGetProperty("id", out var idEl))
                {
                    continue;
                }

                var id = idEl.GetString();
                if (string.IsNullOrWhiteSpace(id))
                {
                    continue;
                }

                var atIndex = id.LastIndexOf('@');
                if (atIndex >= 0 && atIndex < id.Length - 1)
                {
                    return id[(atIndex + 1)..];
                }
            }
        }

        return null;
    }

    private static string NormalizeSeverity(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return "Unknown";
        }

        var value = raw.Trim().ToLowerInvariant();
        return value switch
        {
            "critical" => "Critical",
            "high" => "High",
            "moderate" => "Medium",
            "medium" => "Medium",
            "low" => "Low",
            _ => "Unknown"
        };
    }

    private static string ToPreview(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var normalized = value.Replace("\r", string.Empty, StringComparison.Ordinal);
        if (normalized.Length <= PreviewLimit)
        {
            return normalized;
        }

        return $"{normalized[..PreviewLimit]}... [TRUNCATED]";
    }

    private static string QuoteArg(string arg)
        => arg.Contains(' ', StringComparison.Ordinal) ? $"\"{arg}\"" : arg;
}

public sealed class JavaScanResult
{
    public List<Finding> Vulnerabilities { get; } = new();
    public List<Finding> Outdated { get; } = new();
    public string VulnerableCommand { get; set; } = string.Empty;
    public int VulnerableExitCode { get; set; }
    public string VulnerableStdOutPreview { get; set; } = string.Empty;
    public string VulnerableStdErrPreview { get; set; } = string.Empty;
    public string OutdatedCommand { get; set; } = string.Empty;
    public int OutdatedExitCode { get; set; }
    public string OutdatedStdOutPreview { get; set; } = string.Empty;
    public string OutdatedStdErrPreview { get; set; } = string.Empty;
    public string? Error { get; set; }
}
