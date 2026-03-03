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

    public async Task<(List<Finding> Vulnerabilities, List<Finding> Outdated)> AnalyzeAsync(
        string repositoryId,
        string projectId,
        string analysisPath,
        string requirementsPath,
        string requirementsContent,
        CancellationToken ct = default)
    {
        var vulnerabilities = new List<Finding>();
        var outdated = new List<Finding>();

        var reqPath = Path.Combine(analysisPath, requirementsPath.Replace('/', Path.DirectorySeparatorChar));
        var dir = Path.GetDirectoryName(reqPath) ?? analysisPath;
        Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(reqPath, requirementsContent, ct);

        var names = ParseRequirementNames(requirementsContent);
        if (names.Count == 0)
        {
            return (vulnerabilities, outdated);
        }

        var script = """
                     import json
                     import os
                     import shutil
                     import subprocess
                     import sys
                     import tempfile
                     import venv

                     req_path = sys.argv[1]
                     work_dir = sys.argv[2]

                     payload = {
                         "install_exit_code": -1,
                         "install_stdout": "",
                         "install_stderr": "",
                         "outdated_exit_code": -1,
                         "outdated_stdout": "",
                         "outdated_stderr": "",
                         "audit_exit_code": -1,
                         "audit_stdout": "",
                         "audit_stderr": "",
                     }

                     run_dir = tempfile.mkdtemp(prefix="py-scan-", dir=work_dir)
                     venv_dir = os.path.join(run_dir, ".venv")
                     venv.EnvBuilder(with_pip=True, clear=True).create(venv_dir)
                     venv_python = os.path.join(venv_dir, "bin", "python")

                     try:
                         install = subprocess.run(
                             [venv_python, "-m", "pip", "install", "--disable-pip-version-check", "-r", req_path],
                             capture_output=True,
                             text=True,
                             timeout=300,
                         )
                         payload["install_exit_code"] = install.returncode
                         payload["install_stdout"] = install.stdout
                         payload["install_stderr"] = install.stderr

                         if install.returncode == 0:
                             outdated = subprocess.run(
                                 [venv_python, "-m", "pip", "list", "--outdated", "--format=json"],
                                 capture_output=True,
                                 text=True,
                                 timeout=180,
                             )
                             payload["outdated_exit_code"] = outdated.returncode
                             payload["outdated_stdout"] = outdated.stdout
                             payload["outdated_stderr"] = outdated.stderr

                             install_audit = subprocess.run(
                                 [venv_python, "-m", "pip", "install", "--disable-pip-version-check", "pip-audit"],
                                 capture_output=True,
                                 text=True,
                                 timeout=180,
                             )

                             if install_audit.returncode == 0:
                                 audit = subprocess.run(
                                     [venv_python, "-m", "pip_audit", "-r", req_path, "--format", "json", "--progress-spinner", "off"],
                                     capture_output=True,
                                     text=True,
                                     timeout=240,
                                 )
                                 payload["audit_exit_code"] = audit.returncode
                                 payload["audit_stdout"] = audit.stdout
                                 payload["audit_stderr"] = audit.stderr
                             else:
                                 payload["audit_exit_code"] = install_audit.returncode
                                 payload["audit_stdout"] = install_audit.stdout
                                 payload["audit_stderr"] = install_audit.stderr
                     finally:
                         shutil.rmtree(run_dir, ignore_errors=True)

                     print(json.dumps(payload))
                     """;

        var result = await _cliRunner.RunAsync(
            "python3",
            new[] { "-c", script, reqPath, dir },
            dir,
            TimeSpan.FromSeconds(420),
            2_000_000,
            ct);

        if (result.ExitCode != 0 || string.IsNullOrWhiteSpace(result.StdOut))
        {
            return (vulnerabilities, outdated);
        }

        using var doc = JsonDocument.Parse(result.StdOut);
        if (doc.RootElement.ValueKind != JsonValueKind.Object)
        {
            return (vulnerabilities, outdated);
        }

        if (doc.RootElement.TryGetProperty("outdated_exit_code", out var outdatedExitCode) &&
            outdatedExitCode.ValueKind == JsonValueKind.Number &&
            outdatedExitCode.GetInt32() == 0 &&
            doc.RootElement.TryGetProperty("outdated_stdout", out var outdatedStdoutEl))
        {
            var outdatedStdout = outdatedStdoutEl.GetString();
            if (!string.IsNullOrWhiteSpace(outdatedStdout))
            {
                outdated.AddRange(ParseOutdatedPackages(outdatedStdout, names, repositoryId, projectId));
            }
        }

        if (doc.RootElement.TryGetProperty("audit_exit_code", out var auditExitCode) &&
            auditExitCode.ValueKind == JsonValueKind.Number &&
            (auditExitCode.GetInt32() == 0 || auditExitCode.GetInt32() == 1) &&
            doc.RootElement.TryGetProperty("audit_stdout", out var auditStdoutEl))
        {
            var auditStdout = auditStdoutEl.GetString();
            if (!string.IsNullOrWhiteSpace(auditStdout))
            {
                vulnerabilities.AddRange(ParseVulnerabilities(auditStdout, repositoryId, projectId));
            }
        }

        return (vulnerabilities, outdated);
    }

    private static IEnumerable<Finding> ParseOutdatedPackages(
        string json,
        IReadOnlyCollection<string> rootRequirementNames,
        string repositoryId,
        string projectId)
    {
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        foreach (var pkg in doc.RootElement.EnumerateArray())
        {
            var name = pkg.TryGetProperty("name", out var nameEl) ? nameEl.GetString() : null;
            if (string.IsNullOrWhiteSpace(name) || !rootRequirementNames.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            yield return new Finding
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
            };
        }
    }

    private static IEnumerable<Finding> ParseVulnerabilities(string json, string repositoryId, string projectId)
    {
        using var doc = JsonDocument.Parse(json);

        if (doc.RootElement.ValueKind == JsonValueKind.Object &&
            doc.RootElement.TryGetProperty("dependencies", out var dependenciesElement) &&
            dependenciesElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var finding in ParseVulnerabilitiesFromDependenciesArray(dependenciesElement, repositoryId, projectId))
            {
                yield return finding;
            }

            yield break;
        }

        if (doc.RootElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var finding in ParseVulnerabilitiesFromDependenciesArray(doc.RootElement, repositoryId, projectId))
            {
                yield return finding;
            }
        }
    }

    private static IEnumerable<Finding> ParseVulnerabilitiesFromDependenciesArray(JsonElement dependencies, string repositoryId, string projectId)
    {
        foreach (var dependency in dependencies.EnumerateArray())
        {
            if (!dependency.TryGetProperty("vulns", out var vulnerabilities) || vulnerabilities.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            var packageName = dependency.TryGetProperty("name", out var nameEl) ? nameEl.GetString() ?? "unknown" : "unknown";
            var installedVersion = dependency.TryGetProperty("version", out var versionEl) ? versionEl.GetString() : null;

            foreach (var vulnerability in vulnerabilities.EnumerateArray())
            {
                string? fixedVersion = null;
                if (vulnerability.TryGetProperty("fix_versions", out var fixVersions) &&
                    fixVersions.ValueKind == JsonValueKind.Array &&
                    fixVersions.GetArrayLength() > 0)
                {
                    fixedVersion = fixVersions.EnumerateArray().FirstOrDefault().GetString();
                }

                var advisory = vulnerability.TryGetProperty("id", out var idEl)
                    ? idEl.GetString()
                    : vulnerability.TryGetProperty("aliases", out var aliasesEl) &&
                      aliasesEl.ValueKind == JsonValueKind.Array &&
                      aliasesEl.GetArrayLength() > 0
                        ? aliasesEl.EnumerateArray().FirstOrDefault().GetString()
                        : null;

                yield return new Finding
                {
                    Type = FindingType.Vulnerability,
                    Ecosystem = "python",
                    PackageName = packageName,
                    InstalledVersion = installedVersion,
                    FixedVersion = fixedVersion,
                    Severity = "Unknown",
                    Advisory = advisory,
                    SourceTool = "pip-audit",
                    ProjectId = projectId,
                    RepositoryId = repositoryId
                };
            }
        }
    }

    private static List<string> ParseRequirementNames(string content)
    {
        var result = new List<string>();

        foreach (var rawLine in content.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = rawLine.Trim();
            if (string.IsNullOrWhiteSpace(trimmed) || trimmed.StartsWith("#", StringComparison.Ordinal))
            {
                continue;
            }

            var line = trimmed.Split(';', 2, StringSplitOptions.TrimEntries)[0];
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var atIndex = line.IndexOf(" @ ", StringComparison.Ordinal);
            if (atIndex >= 0)
            {
                line = line[..atIndex];
            }

            var bracketIndex = line.IndexOf('[', StringComparison.Ordinal);
            if (bracketIndex >= 0)
            {
                line = line[..bracketIndex];
            }

            var separators = new[] { "==", ">=", "<=", "~=", "!=", ">", "<" };
            var name = separators
                .Select(sep => line.Split(sep, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault())
                .FirstOrDefault(x => !string.IsNullOrWhiteSpace(x))
                ?? line;

            result.Add(name.Trim());
        }

        return result;
    }
}
