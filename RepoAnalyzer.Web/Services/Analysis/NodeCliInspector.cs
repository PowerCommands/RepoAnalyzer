using System.Text.Json;
using RepoAnalyzer.Web.Models;
using RepoAnalyzer.Web.Models.Enums;

namespace RepoAnalyzer.Web.Services.Analysis;

public sealed class NodeCliInspector
{
    private readonly SafeCliRunner _cliRunner;

    public NodeCliInspector(SafeCliRunner cliRunner)
    {
        _cliRunner = cliRunner;
    }

    public async Task<(List<Finding> Vulnerabilities, List<Finding> Outdated)> AnalyzeAsync(
        string repositoryId,
        string projectId,
        string analysisPath,
        string packageJsonPath,
        string packageJsonContent,
        string? packageLockContent,
        CancellationToken ct = default)
    {
        var vulnerabilities = new List<Finding>();
        var outdated = new List<Finding>();

        var packageJsonFullPath = Path.Combine(analysisPath, packageJsonPath.Replace('/', Path.DirectorySeparatorChar));
        var projectDir = Path.GetDirectoryName(packageJsonFullPath) ?? analysisPath;
        Directory.CreateDirectory(projectDir);
        await File.WriteAllTextAsync(packageJsonFullPath, packageJsonContent, ct);

        if (!string.IsNullOrWhiteSpace(packageLockContent))
        {
            var lockPath = Path.Combine(projectDir, "package-lock.json");
            await File.WriteAllTextAsync(lockPath, packageLockContent, ct);
        }

        var hasLock = File.Exists(Path.Combine(projectDir, "package-lock.json"));
        if (hasLock)
        {
            var audit = await _cliRunner.RunAsync(
                "npm",
                new[] { "audit", "--json", "--package-lock-only" },
                projectDir,
                TimeSpan.FromSeconds(25),
                2_000_000,
                ct);

            if (!string.IsNullOrWhiteSpace(audit.StdOut))
            {
                vulnerabilities.AddRange(ParseAudit(audit.StdOut, repositoryId, projectId));
            }
        }

        var outdatedResult = await _cliRunner.RunAsync(
            "npm",
            new[] { "outdated", "--json" },
            projectDir,
            TimeSpan.FromSeconds(20),
            2_000_000,
            ct);

        if (!string.IsNullOrWhiteSpace(outdatedResult.StdOut) && outdatedResult.StdOut.TrimStart().StartsWith("{", StringComparison.Ordinal))
        {
            outdated.AddRange(ParseOutdated(outdatedResult.StdOut, repositoryId, projectId));
        }

        return (vulnerabilities, outdated);
    }

    private static IEnumerable<Finding> ParseAudit(string json, string repositoryId, string projectId)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("vulnerabilities", out var vulns) || vulns.ValueKind != JsonValueKind.Object)
        {
            yield break;
        }

        foreach (var vuln in vulns.EnumerateObject())
        {
            var root = vuln.Value;
            var severity = NormalizeSeverity(root.TryGetProperty("severity", out var sev) ? sev.GetString() : null);
            string? advisory = null;

            if (root.TryGetProperty("via", out var via) && via.ValueKind == JsonValueKind.Array)
            {
                var firstObject = via.EnumerateArray().FirstOrDefault(v => v.ValueKind == JsonValueKind.Object);
                if (firstObject.ValueKind == JsonValueKind.Object)
                {
                    advisory = firstObject.TryGetProperty("url", out var url) ? url.GetString() : null;
                }
            }

            yield return new Finding
            {
                Type = FindingType.Vulnerability,
                Ecosystem = "npm",
                PackageName = vuln.Name,
                InstalledVersion = root.TryGetProperty("range", out var range) ? range.GetString() : null,
                FixedVersion = root.TryGetProperty("fixAvailable", out var fix) && fix.ValueKind == JsonValueKind.Object && fix.TryGetProperty("version", out var version)
                    ? version.GetString()
                    : null,
                Severity = severity,
                Advisory = advisory,
                SourceTool = "npm",
                ProjectId = projectId,
                RepositoryId = repositoryId
            };
        }
    }

    private static IEnumerable<Finding> ParseOutdated(string json, string repositoryId, string projectId)
    {
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Object)
        {
            yield break;
        }

        foreach (var pkg in doc.RootElement.EnumerateObject())
        {
            var installed = pkg.Value.TryGetProperty("current", out var cur) ? cur.GetString() : null;
            var latest = pkg.Value.TryGetProperty("latest", out var lat) ? lat.GetString() : null;

            yield return new Finding
            {
                Type = FindingType.Outdated,
                Ecosystem = "npm",
                PackageName = pkg.Name,
                InstalledVersion = installed,
                FixedVersion = latest,
                Severity = "Unknown",
                Advisory = null,
                SourceTool = "npm",
                ProjectId = projectId,
                RepositoryId = repositoryId
            };
        }
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
}
