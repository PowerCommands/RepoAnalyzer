using System.Text.Json;
using RepoAnalyzer.Web.Models;
using RepoAnalyzer.Web.Models.Enums;

namespace RepoAnalyzer.Web.Services.Analysis;

public sealed class PythonCliInspector
{
    private readonly SafeCliRunner _cliRunner;

    public PythonCliInspector(SafeCliRunner cliRunner)
    {
        _cliRunner = cliRunner;
    }

    public async Task<List<Finding>> AnalyzeOutdatedAsync(
        string repositoryId,
        string projectId,
        string analysisPath,
        string requirementsPath,
        string requirementsContent,
        CancellationToken ct = default)
    {
        var findings = new List<Finding>();

        var reqPath = Path.Combine(analysisPath, requirementsPath.Replace('/', Path.DirectorySeparatorChar));
        var dir = Path.GetDirectoryName(reqPath) ?? analysisPath;
        Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(reqPath, requirementsContent, ct);

        var names = ParseRequirementNames(requirementsContent);

        var result = await _cliRunner.RunAsync(
            "python3",
            new[] { "-m", "pip", "list", "--outdated", "--format=json" },
            dir,
            TimeSpan.FromSeconds(20),
            2_000_000,
            ct);

        if (result.ExitCode != 0 || string.IsNullOrWhiteSpace(result.StdOut))
        {
            return findings;
        }

        using var doc = JsonDocument.Parse(result.StdOut);
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
        {
            return findings;
        }

        foreach (var pkg in doc.RootElement.EnumerateArray())
        {
            var name = pkg.TryGetProperty("name", out var n) ? n.GetString() : null;
            if (string.IsNullOrWhiteSpace(name) || !names.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            findings.Add(new Finding
            {
                Type = FindingType.Outdated,
                Ecosystem = "python",
                PackageName = name,
                InstalledVersion = pkg.TryGetProperty("version", out var installed) ? installed.GetString() : null,
                FixedVersion = pkg.TryGetProperty("latest_version", out var latest) ? latest.GetString() : null,
                Severity = "Unknown",
                SourceTool = "python3 -m pip",
                ProjectId = projectId,
                RepositoryId = repositoryId
            });
        }

        return findings;
    }

    private static List<string> ParseRequirementNames(string content)
    {
        var result = new List<string>();

        foreach (var line in content.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = line.Trim();
            if (string.IsNullOrWhiteSpace(trimmed) || trimmed.StartsWith("#", StringComparison.Ordinal))
            {
                continue;
            }

            var separators = new[] { "==", ">=", "<=", "~=" };
            var name = separators.Select(sep => trimmed.Split(sep, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x))
                       ?? trimmed;

            result.Add(name.Trim());
        }

        return result;
    }
}
