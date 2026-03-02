using System.Text.Json;
using RepoAnalyzer.Web.Models;
using RepoAnalyzer.Web.Models.Enums;

namespace RepoAnalyzer.Web.Services.Analysis;

public sealed class DotNetCliInspector
{
    private const int PreviewLimit = 2000;
    private static readonly SemaphoreSlim SelfTestLock = new(1, 1);
    private static DotNetSelfTestResult? _cachedSelfTest;

    private readonly SafeCliRunner _cliRunner;
    private readonly ILogger<DotNetCliInspector> _logger;

    public DotNetCliInspector(SafeCliRunner cliRunner, ILogger<DotNetCliInspector> logger)
    {
        _cliRunner = cliRunner;
        _logger = logger;
    }

    public async Task<DotNetScanResult> AnalyzeAsync(
        string repositoryId,
        string projectId,
        string analysisPath,
        string projectRelativePath,
        string projectContent,
        Dictionary<string, string> extraFiles,
        CancellationToken ct = default)
    {
        var result = new DotNetScanResult();

        var fullPath = Path.Combine(analysisPath, projectRelativePath.Replace('/', Path.DirectorySeparatorChar));
        var dir = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrWhiteSpace(dir))
        {
            Directory.CreateDirectory(dir);
        }

        await File.WriteAllTextAsync(fullPath, projectContent, ct);

        foreach (var extra in extraFiles)
        {
            var extraPath = Path.Combine(analysisPath, extra.Key.Replace('/', Path.DirectorySeparatorChar));
            var extraDir = Path.GetDirectoryName(extraPath);
            if (!string.IsNullOrWhiteSpace(extraDir))
            {
                Directory.CreateDirectory(extraDir);
            }

            await File.WriteAllTextAsync(extraPath, extra.Value, ct);
        }

        try
        {
            var packageRoot = Path.Combine(analysisPath, ".nuget", "packages");
            Directory.CreateDirectory(packageRoot);

            var restoreArgs = new[] { "restore", fullPath, "--packages", packageRoot, "--disable-parallel" };
            var restoreResult = await _cliRunner.RunAsync(
                "dotnet",
                restoreArgs,
                analysisPath,
                TimeSpan.FromSeconds(90),
                3_000_000,
                ct);
            result.RestoreCommand = $"dotnet {string.Join(' ', restoreArgs.Select(QuoteArg))}";
            result.RestoreExitCode = restoreResult.ExitCode;
            result.RestoreStdOutPreview = ToPreview(restoreResult.StdOut);
            result.RestoreStdErrPreview = ToPreview(restoreResult.StdErr);

            if (restoreResult.ExitCode != 0)
            {
                result.Error = "dotnet restore failed";
                return result;
            }

            var vulnerableArgs = new[] { "list", fullPath, "package", "--vulnerable", "--include-transitive", "--format", "json" };
            var vulnerableResult = await _cliRunner.RunAsync(
                "dotnet",
                vulnerableArgs,
                analysisPath,
                TimeSpan.FromSeconds(20),
                2_000_000,
                ct);
            result.VulnerableCommand = $"dotnet {string.Join(' ', vulnerableArgs.Select(QuoteArg))}";
            result.VulnerableExitCode = vulnerableResult.ExitCode;
            result.VulnerableStdOutPreview = ToPreview(vulnerableResult.StdOut);
            result.VulnerableStdErrPreview = ToPreview(vulnerableResult.StdErr);

            var outdatedArgs = new[] { "list", fullPath, "package", "--outdated", "--include-transitive", "--format", "json" };
            var outdatedResult = await _cliRunner.RunAsync(
                "dotnet",
                outdatedArgs,
                analysisPath,
                TimeSpan.FromSeconds(20),
                2_000_000,
                ct);
            result.OutdatedCommand = $"dotnet {string.Join(' ', outdatedArgs.Select(QuoteArg))}";
            result.OutdatedExitCode = outdatedResult.ExitCode;
            result.OutdatedStdOutPreview = ToPreview(outdatedResult.StdOut);
            result.OutdatedStdErrPreview = ToPreview(outdatedResult.StdErr);

            if (vulnerableResult.ExitCode == 0 && !string.IsNullOrWhiteSpace(vulnerableResult.StdOut))
            {
                var vulnerableJson = ExtractJsonPayload(vulnerableResult.StdOut);
                if (!string.IsNullOrWhiteSpace(vulnerableJson))
                {
                    result.Vulnerabilities.AddRange(ParseDotnetFindings(vulnerableJson, FindingType.Vulnerability, repositoryId, projectId));
                }
            }

            if (outdatedResult.ExitCode == 0 && !string.IsNullOrWhiteSpace(outdatedResult.StdOut))
            {
                var outdatedJson = ExtractJsonPayload(outdatedResult.StdOut);
                if (!string.IsNullOrWhiteSpace(outdatedJson))
                {
                    result.Outdated.AddRange(ParseDotnetFindings(outdatedJson, FindingType.Outdated, repositoryId, projectId));
                }
            }
        }
        catch (Exception ex)
        {
            result.Error = ex.Message;
            _logger.LogWarning(ex, "dotnet list package check failed for {ProjectPath}", projectRelativePath);
        }

        return result;
    }

    public async Task<DotNetSelfTestResult> EnsureSelfTestAsync(string tempRootPath, CancellationToken ct = default)
    {
        if (_cachedSelfTest is not null)
        {
            return _cachedSelfTest;
        }

        await SelfTestLock.WaitAsync(ct);
        try
        {
            if (_cachedSelfTest is not null)
            {
                return _cachedSelfTest;
            }

            var result = new DotNetSelfTestResult();
            var root = Path.Combine(tempRootPath, "selftest", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);

            try
            {
                var projectPath = Path.Combine(root, "SelfTest.csproj");
                var content = """
                              <Project Sdk="Microsoft.NET.Sdk">
                                <PropertyGroup>
                                  <TargetFramework>net8.0</TargetFramework>
                                </PropertyGroup>
                                <ItemGroup>
                                  <PackageReference Include="Newtonsoft.Json" Version="12.0.1" />
                                </ItemGroup>
                              </Project>
                              """;
                await File.WriteAllTextAsync(projectPath, content, ct);

                var packageRoot = Path.Combine(root, ".nuget", "packages");
                Directory.CreateDirectory(packageRoot);
                var restoreArgs = new[] { "restore", projectPath, "--packages", packageRoot, "--disable-parallel" };
                var restoreResult = await _cliRunner.RunAsync("dotnet", restoreArgs, root, TimeSpan.FromSeconds(90), 3_000_000, ct);
                result.RestoreCommand = $"dotnet {string.Join(' ', restoreArgs.Select(QuoteArg))}";
                result.RestoreExitCode = restoreResult.ExitCode;
                result.RestoreStdOutPreview = ToPreview(restoreResult.StdOut);
                result.RestoreStdErrPreview = ToPreview(restoreResult.StdErr);

                if (restoreResult.ExitCode != 0)
                {
                    result.Success = false;
                    result.Error = "dotnet restore failed during self-test";
                    _cachedSelfTest = result;
                    return result;
                }

                var vulnerableArgs = new[] { "list", projectPath, "package", "--vulnerable", "--include-transitive", "--format", "json" };
                var vulnerableResult = await _cliRunner.RunAsync("dotnet", vulnerableArgs, root, TimeSpan.FromSeconds(20), 2_000_000, ct);
                result.VulnerableCommand = $"dotnet {string.Join(' ', vulnerableArgs.Select(QuoteArg))}";
                result.VulnerableExitCode = vulnerableResult.ExitCode;
                result.VulnerableStdOutPreview = ToPreview(vulnerableResult.StdOut);
                result.VulnerableStdErrPreview = ToPreview(vulnerableResult.StdErr);

                var outdatedArgs = new[] { "list", projectPath, "package", "--outdated", "--include-transitive", "--format", "json" };
                var outdatedResult = await _cliRunner.RunAsync("dotnet", outdatedArgs, root, TimeSpan.FromSeconds(20), 2_000_000, ct);
                result.OutdatedCommand = $"dotnet {string.Join(' ', outdatedArgs.Select(QuoteArg))}";
                result.OutdatedExitCode = outdatedResult.ExitCode;
                result.OutdatedStdOutPreview = ToPreview(outdatedResult.StdOut);
                result.OutdatedStdErrPreview = ToPreview(outdatedResult.StdErr);

                if (vulnerableResult.ExitCode == 0 && !string.IsNullOrWhiteSpace(vulnerableResult.StdOut))
                {
                    var vulnerableJson = ExtractJsonPayload(vulnerableResult.StdOut);
                    if (!string.IsNullOrWhiteSpace(vulnerableJson))
                    {
                        result.VulnerabilityCount = ParseDotnetFindings(vulnerableJson, FindingType.Vulnerability, "self-test", "self-test").Count;
                    }
                }

                if (outdatedResult.ExitCode == 0 && !string.IsNullOrWhiteSpace(outdatedResult.StdOut))
                {
                    var outdatedJson = ExtractJsonPayload(outdatedResult.StdOut);
                    if (!string.IsNullOrWhiteSpace(outdatedJson))
                    {
                        result.OutdatedCount = ParseDotnetFindings(outdatedJson, FindingType.Outdated, "self-test", "self-test").Count;
                    }
                }

                result.Success = vulnerableResult.ExitCode == 0 && outdatedResult.ExitCode == 0;
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.Error = ex.Message;
            }
            finally
            {
                try
                {
                    if (Directory.Exists(root))
                    {
                        Directory.Delete(root, recursive: true);
                    }
                }
                catch
                {
                    // Best effort cleanup.
                }
            }

            _cachedSelfTest = result;
            return result;
        }
        finally
        {
            SelfTestLock.Release();
        }
    }

    private static List<Finding> ParseDotnetFindings(string json, FindingType type, string repositoryId, string projectId)
    {
        var findings = new List<Finding>();
        using var doc = JsonDocument.Parse(json);

        if (!doc.RootElement.TryGetProperty("projects", out var projectsElement))
        {
            return findings;
        }

        foreach (var project in projectsElement.EnumerateArray())
        {
            if (!project.TryGetProperty("frameworks", out var frameworks))
            {
                continue;
            }

            foreach (var framework in frameworks.EnumerateArray())
            {
                var packages = new List<JsonElement>();
                if (framework.TryGetProperty("topLevelPackages", out var topLevel))
                {
                    packages.AddRange(topLevel.EnumerateArray());
                }

                if (framework.TryGetProperty("transitivePackages", out var transitive))
                {
                    packages.AddRange(transitive.EnumerateArray());
                }

                foreach (var pkg in packages)
                {
                    var packageName = pkg.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? "unknown" : "unknown";
                    var resolved = pkg.TryGetProperty("resolvedVersion", out var resEl) ? resEl.GetString() : null;
                    var latest = pkg.TryGetProperty("latestVersion", out var latestEl) ? latestEl.GetString() : null;

                    if (type == FindingType.Vulnerability)
                    {
                        if (!pkg.TryGetProperty("vulnerabilities", out var vulnerabilities))
                        {
                            continue;
                        }

                        foreach (var vuln in vulnerabilities.EnumerateArray())
                        {
                            findings.Add(new Finding
                            {
                                Type = FindingType.Vulnerability,
                                Ecosystem = "NuGet",
                                PackageName = packageName,
                                InstalledVersion = resolved,
                                FixedVersion = latest,
                                Severity = NormalizeSeverity(vuln.TryGetProperty("severity", out var sev) ? sev.GetString() : null),
                                Advisory = vuln.TryGetProperty("advisoryurl", out var adv) ? adv.GetString() : null,
                                SourceTool = "dotnet",
                                ProjectId = projectId,
                                RepositoryId = repositoryId
                            });
                        }
                    }
                    else
                    {
                        if (string.IsNullOrWhiteSpace(latest) || string.Equals(latest, resolved, StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        findings.Add(new Finding
                        {
                            Type = FindingType.Outdated,
                            Ecosystem = "NuGet",
                            PackageName = packageName,
                            InstalledVersion = resolved,
                            FixedVersion = latest,
                            Severity = "Unknown",
                            SourceTool = "dotnet",
                            ProjectId = projectId,
                            RepositoryId = repositoryId
                        });
                    }
                }
            }
        }

        return findings;
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

    private static string? ExtractJsonPayload(string output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return null;
        }

        var index = output.IndexOf('{');
        if (index < 0 || index >= output.Length)
        {
            return null;
        }

        return output[index..].Trim();
    }
}

public sealed class DotNetScanResult
{
    public List<Finding> Vulnerabilities { get; } = new();
    public List<Finding> Outdated { get; } = new();
    public string RestoreCommand { get; set; } = string.Empty;
    public int RestoreExitCode { get; set; }
    public string RestoreStdOutPreview { get; set; } = string.Empty;
    public string RestoreStdErrPreview { get; set; } = string.Empty;
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

public sealed class DotNetSelfTestResult
{
    public bool Success { get; set; }
    public string RestoreCommand { get; set; } = string.Empty;
    public int RestoreExitCode { get; set; }
    public string RestoreStdOutPreview { get; set; } = string.Empty;
    public string RestoreStdErrPreview { get; set; } = string.Empty;
    public string VulnerableCommand { get; set; } = string.Empty;
    public int VulnerableExitCode { get; set; }
    public string VulnerableStdOutPreview { get; set; } = string.Empty;
    public string VulnerableStdErrPreview { get; set; } = string.Empty;
    public string OutdatedCommand { get; set; } = string.Empty;
    public int OutdatedExitCode { get; set; }
    public string OutdatedStdOutPreview { get; set; } = string.Empty;
    public string OutdatedStdErrPreview { get; set; } = string.Empty;
    public int VulnerabilityCount { get; set; }
    public int OutdatedCount { get; set; }
    public string? Error { get; set; }
}
