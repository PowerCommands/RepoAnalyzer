using System.Text.Json;
using System.Xml.Linq;
using RepoAnalyzer.Web.Models;
using RepoAnalyzer.Web.Models.Enums;
using RepoAnalyzer.Web.Services.Analysis.Logging;
using RepoAnalyzer.Web.Services.Providers;

namespace RepoAnalyzer.Web.Services.Analysis;

public sealed class RepositoryAnalyzerService
{
    private readonly AppDataService _data;
    private readonly ConnectionService _connectionService;
    private readonly GitProviderFactory _providerFactory;
    private readonly DotNetCliInspector _dotnetCliInspector;
    private readonly NodeCliInspector _nodeCliInspector;
    private readonly PythonCliInspector _pythonCliInspector;
    private readonly JavaCliInspector _javaCliInspector;
    private readonly IAnalysisLog _analysisLog;
    private readonly string _dataPath;

    public RepositoryAnalyzerService(
        AppDataService data,
        ConnectionService connectionService,
        GitProviderFactory providerFactory,
        DotNetCliInspector dotnetCliInspector,
        NodeCliInspector nodeCliInspector,
        PythonCliInspector pythonCliInspector,
        JavaCliInspector javaCliInspector,
        IAnalysisLog analysisLog,
        IConfiguration configuration)
    {
        _data = data;
        _connectionService = connectionService;
        _providerFactory = providerFactory;
        _dotnetCliInspector = dotnetCliInspector;
        _nodeCliInspector = nodeCliInspector;
        _pythonCliInspector = pythonCliInspector;
        _javaCliInspector = javaCliInspector;
        _analysisLog = analysisLog;
        _dataPath = configuration["DataPath"] ?? "/app/data";
    }

    public async Task<RepoAnalysisSnapshot> AnalyzeRepositoryAsync(
        string repositoryId,
        string? analysisRunId = null,
        IProgress<AnalyzeProgress>? progress = null,
        CancellationToken ct = default)
    {
        var runId = string.IsNullOrWhiteSpace(analysisRunId) ? Guid.NewGuid().ToString("N") : analysisRunId;
        var repositories = await _data.GetRepositoriesAsync(ct);
        var workspaces = await _data.GetWorkspacesAsync(ct);
        var repository = repositories.FirstOrDefault(x => x.Id == repositoryId)
            ?? throw new InvalidOperationException("Repository not found.");
        var workspace = !string.IsNullOrWhiteSpace(repository.WorkspaceId)
            ? workspaces.FirstOrDefault(x => x.Id == repository.WorkspaceId)
            : null;

        var connection = await _connectionService.GetRawByIdAsync(repository.ConnectionId, ct)
            ?? throw new InvalidOperationException("Connection not found.");

        var context = new AnalysisLogContext
        {
            AnalysisRunId = runId,
            ConnectionId = connection.Id,
            ProviderType = MapProviderType(connection.Type),
            WorkspaceId = workspace?.Id,
            WorkspaceName = workspace?.Name,
            RepositoryId = repository.Id,
            RepositoryName = repository.Name
        };

        var currentStep = "StartAnalysis";
        var analysisStarted = DateTimeOffset.UtcNow;
        var totalTimer = System.Diagnostics.Stopwatch.StartNew();
        var analysisRoot = Path.Combine(_dataPath, "tmp", runId);

        await _analysisLog.InfoAsync(
            currentStep,
            "Analysis started.",
            context,
            new Dictionary<string, object?>
            {
                ["analysisRunId"] = runId,
                ["repositoryId"] = repository.Id,
                ["repositoryName"] = repository.Name,
                ["workspaceId"] = workspace?.Id,
                ["workspaceName"] = workspace?.Name,
                ["providerType"] = MapProviderType(connection.Type)
            },
            ct);
        ReportProgress(progress, currentStep, "Analysis started.", 2);

        var vulnerabilities = new List<Finding>();
        var outdated = new List<Finding>();
        var projects = new List<DetectedProject>();
        var components = new List<Component>();

        try
        {
            Directory.CreateDirectory(analysisRoot);

            currentStep = "FetchRepoFileList";
            var fetchListTimer = System.Diagnostics.Stopwatch.StartNew();
            var provider = _providerFactory.Resolve(connection.Type);
            var files = await provider.GetRepositoryFilesAsync(connection, repository, ct);
            fetchListTimer.Stop();

            await _analysisLog.InfoAsync(
                currentStep,
                "Repository file list fetched.",
                context,
                new Dictionary<string, object?>
                {
                    ["fileCount"] = files.Count,
                    ["elapsedMs"] = fetchListTimer.ElapsedMilliseconds
                },
                ct);
            ReportProgress(progress, currentStep, "Repository file list fetched.", 12);

            var fileMap = files.ToDictionary(f => NormalizePath(f.Path), f => f.Content, StringComparer.OrdinalIgnoreCase);

            currentStep = "FetchManifests";
            var manifestTimer = System.Diagnostics.Stopwatch.StartNew();
            var manifestPaths = fileMap.Keys
                .Where(IsManifestPath)
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .ToList();
            manifestTimer.Stop();

            await _analysisLog.InfoAsync(
                currentStep,
                "Manifest paths collected.",
                context,
                new Dictionary<string, object?>
                {
                    ["manifestCount"] = manifestPaths.Count,
                    ["manifestPaths"] = manifestPaths.Take(500).ToList(),
                    ["elapsedMs"] = manifestTimer.ElapsedMilliseconds
                },
                ct);
            ReportProgress(progress, currentStep, "Manifest paths collected.", 20);

            currentStep = "DetectProjects";
            var detectTimer = System.Diagnostics.Stopwatch.StartNew();
            projects = await DetectProjectsAsync(repositoryId, fileMap, context, ct);
            detectTimer.Stop();

            await _analysisLog.InfoAsync(
                currentStep,
                "Project detection completed.",
                context,
                new Dictionary<string, object?>
                {
                    ["projectCount"] = projects.Count,
                    ["projectTypes"] = projects.Select(p => p.ProjectType).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x).ToList(),
                    ["projectPaths"] = projects.Select(p => p.Path).ToList(),
                    ["elapsedMs"] = detectTimer.ElapsedMilliseconds
                },
                ct);
            ReportProgress(progress, currentStep, "Project detection completed.", 35);

            currentStep = "ExtractComponents";
            var extractTimer = System.Diagnostics.Stopwatch.StartNew();
            components = await ExtractComponentsAsync(repositoryId, projects, fileMap, context, ct);
            extractTimer.Stop();

            await _analysisLog.InfoAsync(
                currentStep,
                "Component extraction completed.",
                context,
                new Dictionary<string, object?>
                {
                    ["componentCount"] = components.Count,
                    ["extractors"] = new List<string> { "CsprojPackageExtractor", "PackagesConfigExtractor", "DirectoryPackagesPropsExtractor", "PackageJsonExtractor", "RequirementsTxtExtractor", "PomPackageExtractor" },
                    ["processedPaths"] = projects.Select(p => p.Path).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x).ToList(),
                    ["elapsedMs"] = extractTimer.ElapsedMilliseconds
                },
                ct);
            ReportProgress(progress, currentStep, "Component extraction completed.", 50);

            currentStep = "RunVulnerabilityScans";
            await _analysisLog.InfoAsync(
                currentStep,
                "Starting vulnerability and outdated scans.",
                context,
                new Dictionary<string, object?>
                {
                    ["projectCount"] = projects.Count
                },
                ct);
            ReportProgress(progress, currentStep, "Running vulnerability and outdated scans.", 55);

            var scanTimer = System.Diagnostics.Stopwatch.StartNew();
            var dotNetProjects = projects.Where(IsDotNetProject).ToList();
            if (dotNetProjects.Count > 0)
            {
                var selfTest = await _dotnetCliInspector.EnsureSelfTestAsync(Path.Combine(_dataPath, "tmp"), ct);
                var selfTestLevel = selfTest.Success ? (Func<Task>)(() => _analysisLog.InfoAsync(
                    "DotNetScannerSelfTest",
                    ".NET scanner self-test completed.",
                    context,
                    new Dictionary<string, object?>
                    {
                        ["success"] = true,
                        ["restoreExitCode"] = selfTest.RestoreExitCode,
                        ["vulnerableExitCode"] = selfTest.VulnerableExitCode,
                        ["outdatedExitCode"] = selfTest.OutdatedExitCode,
                        ["vulnerabilityCount"] = selfTest.VulnerabilityCount,
                        ["outdatedCount"] = selfTest.OutdatedCount,
                        ["restoreCommand"] = selfTest.RestoreCommand,
                        ["vulnerableCommand"] = selfTest.VulnerableCommand,
                        ["outdatedCommand"] = selfTest.OutdatedCommand,
                        ["restoreStdErrPreview"] = selfTest.RestoreStdErrPreview,
                        ["vulnerableStdErrPreview"] = selfTest.VulnerableStdErrPreview,
                        ["outdatedStdErrPreview"] = selfTest.OutdatedStdErrPreview
                    },
                    ct)) : (() => _analysisLog.WarningAsync(
                    "DotNetScannerSelfTest",
                    ".NET scanner self-test reported a failure.",
                    context,
                    new Dictionary<string, object?>
                    {
                        ["success"] = false,
                        ["error"] = selfTest.Error,
                        ["restoreExitCode"] = selfTest.RestoreExitCode,
                        ["vulnerableExitCode"] = selfTest.VulnerableExitCode,
                        ["outdatedExitCode"] = selfTest.OutdatedExitCode,
                        ["restoreCommand"] = selfTest.RestoreCommand,
                        ["vulnerableCommand"] = selfTest.VulnerableCommand,
                        ["outdatedCommand"] = selfTest.OutdatedCommand,
                        ["restoreStdErrPreview"] = selfTest.RestoreStdErrPreview,
                        ["restoreStdOutPreview"] = selfTest.RestoreStdOutPreview,
                        ["vulnerableStdErrPreview"] = selfTest.VulnerableStdErrPreview,
                        ["outdatedStdErrPreview"] = selfTest.OutdatedStdErrPreview,
                        ["vulnerableStdOutPreview"] = selfTest.VulnerableStdOutPreview,
                        ["outdatedStdOutPreview"] = selfTest.OutdatedStdOutPreview
                    },
                    ct));
                await selfTestLevel();
            }

            var scannedIndex = 0;
            foreach (var project in projects)
            {
                scannedIndex++;
                if (IsDotNetProject(project))
                {
                    var scannerStep = "RunDotnetVulnScan";
                    var scannerTimer = System.Diagnostics.Stopwatch.StartNew();
                    var projectPath = NormalizePath(project.Path);
                    var projectContent = fileMap.GetValueOrDefault(projectPath);
                    if (string.IsNullOrWhiteSpace(projectContent))
                    {
                        await _analysisLog.WarningAsync(
                            scannerStep,
                            "Skipping .NET scan due to missing project content.",
                            context,
                            new Dictionary<string, object?>
                            {
                                ["projectId"] = project.Id,
                                ["projectPath"] = projectPath
                            },
                            ct);
                    }
                    else
                    {
                        var extra = await CollectDotNetExtraFiles(projectPath, fileMap, context, ct);
                        var dotnetResult = await _dotnetCliInspector.AnalyzeAsync(repositoryId, project.Id, analysisRoot, projectPath, projectContent, extra, ct);
                        vulnerabilities.AddRange(dotnetResult.Vulnerabilities);
                        outdated.AddRange(dotnetResult.Outdated);

                        scannerTimer.Stop();
                        await _analysisLog.InfoAsync(
                            scannerStep,
                            "Completed .NET scan.",
                            context,
                            new Dictionary<string, object?>
                            {
                                ["projectId"] = project.Id,
                                ["projectPath"] = projectPath,
                                ["scanner"] = nameof(DotNetCliInspector),
                                ["vulnerabilities"] = dotnetResult.Vulnerabilities.Count,
                                ["outdated"] = dotnetResult.Outdated.Count,
                                ["extraFiles"] = extra.Keys.OrderBy(x => x).ToList(),
                                ["restoreCommand"] = dotnetResult.RestoreCommand,
                                ["restoreExitCode"] = dotnetResult.RestoreExitCode,
                                ["restoreStdErrPreview"] = dotnetResult.RestoreStdErrPreview,
                                ["restoreStdOutPreview"] = dotnetResult.RestoreStdOutPreview,
                                ["vulnerableCommand"] = dotnetResult.VulnerableCommand,
                                ["vulnerableExitCode"] = dotnetResult.VulnerableExitCode,
                                ["vulnerableStdErrPreview"] = dotnetResult.VulnerableStdErrPreview,
                                ["vulnerableStdOutPreview"] = dotnetResult.VulnerableStdOutPreview,
                                ["outdatedCommand"] = dotnetResult.OutdatedCommand,
                                ["outdatedExitCode"] = dotnetResult.OutdatedExitCode,
                                ["outdatedStdErrPreview"] = dotnetResult.OutdatedStdErrPreview,
                                ["outdatedStdOutPreview"] = dotnetResult.OutdatedStdOutPreview,
                                ["scannerError"] = dotnetResult.Error,
                                ["elapsedMs"] = scannerTimer.ElapsedMilliseconds
                            },
                            ct);

                        var scanPercent = 55 + (int)Math.Round((scannedIndex / (double)Math.Max(1, projects.Count)) * 30);
                        ReportProgress(progress, scannerStep, $"Scanned .NET project: {project.Name}", Math.Clamp(scanPercent, 55, 85));
                    }
                }

                if (project.Language.Contains("JavaScript", StringComparison.OrdinalIgnoreCase))
                {
                    var scannerStep = "RunNpmVulnScan";
                    var scannerTimer = System.Diagnostics.Stopwatch.StartNew();
                    var packageJsonPath = NormalizePath(project.Path);
                    var packageJsonContent = fileMap.GetValueOrDefault(packageJsonPath);
                    if (string.IsNullOrWhiteSpace(packageJsonContent))
                    {
                        await _analysisLog.WarningAsync(
                            scannerStep,
                            "Skipping npm scan due to missing package.json content.",
                            context,
                            new Dictionary<string, object?>
                            {
                                ["projectId"] = project.Id,
                                ["projectPath"] = packageJsonPath
                            },
                            ct);
                    }
                    else
                    {
                        var lockPath = CombineRepoPath(Path.GetDirectoryName(packageJsonPath) ?? string.Empty, "package-lock.json");
                        var lockContent = fileMap.GetValueOrDefault(lockPath);

                        var result = await _nodeCliInspector.AnalyzeAsync(repositoryId, project.Id, analysisRoot, packageJsonPath, packageJsonContent, lockContent, ct);
                        vulnerabilities.AddRange(result.Vulnerabilities);
                        outdated.AddRange(result.Outdated);

                        scannerTimer.Stop();
                        await _analysisLog.InfoAsync(
                            scannerStep,
                            "Completed npm scan.",
                            context,
                            new Dictionary<string, object?>
                            {
                                ["projectId"] = project.Id,
                                ["projectPath"] = packageJsonPath,
                                ["lockPath"] = lockContent is null ? null : lockPath,
                                ["scanner"] = nameof(NodeCliInspector),
                                ["vulnerabilities"] = result.Vulnerabilities.Count,
                                ["outdated"] = result.Outdated.Count,
                                ["elapsedMs"] = scannerTimer.ElapsedMilliseconds
                            },
                            ct);
                    }
                }

                if (project.Language.Equals("Python", StringComparison.OrdinalIgnoreCase))
                {
                    var scannerStep = "RunPipAudit";
                    var scannerTimer = System.Diagnostics.Stopwatch.StartNew();
                    var requirementsPath = NormalizePath(project.Path);
                    var requirementsContent = fileMap.GetValueOrDefault(requirementsPath);
                    if (string.IsNullOrWhiteSpace(requirementsContent))
                    {
                        await _analysisLog.WarningAsync(
                            scannerStep,
                            "Skipping Python scan due to missing requirements content.",
                            context,
                            new Dictionary<string, object?>
                            {
                                ["projectId"] = project.Id,
                                ["projectPath"] = requirementsPath
                            },
                            ct);
                    }
                    else
                    {
                        var pyFindings = await _pythonCliInspector.AnalyzeOutdatedAsync(repositoryId, project.Id, analysisRoot, requirementsPath, requirementsContent, ct);
                        outdated.AddRange(pyFindings);

                        scannerTimer.Stop();
                        await _analysisLog.InfoAsync(
                            scannerStep,
                            "Completed Python scan.",
                            context,
                            new Dictionary<string, object?>
                            {
                                ["projectId"] = project.Id,
                                ["projectPath"] = requirementsPath,
                                ["scanner"] = nameof(PythonCliInspector),
                                ["outdated"] = pyFindings.Count,
                                ["elapsedMs"] = scannerTimer.ElapsedMilliseconds
                            },
                            ct);
                    }
                }

                if (IsJavaProject(project))
                {
                    var scannerStep = "RunMavenVulnScan";
                    var scannerTimer = System.Diagnostics.Stopwatch.StartNew();
                    var pomPath = NormalizePath(project.Path);
                    var pomContent = fileMap.GetValueOrDefault(pomPath);
                    if (string.IsNullOrWhiteSpace(pomContent))
                    {
                        await _analysisLog.WarningAsync(
                            scannerStep,
                            "Skipping Maven scan due to missing pom.xml content.",
                            context,
                            new Dictionary<string, object?>
                            {
                                ["projectId"] = project.Id,
                                ["projectPath"] = pomPath
                            },
                            ct);
                    }
                    else
                    {
                        var result = await _javaCliInspector.AnalyzeAsync(repositoryId, project.Id, analysisRoot, pomPath, pomContent, ct);
                        vulnerabilities.AddRange(result.Vulnerabilities);
                        outdated.AddRange(result.Outdated);

                        scannerTimer.Stop();
                        await _analysisLog.InfoAsync(
                            scannerStep,
                            "Completed Maven scan.",
                            context,
                            new Dictionary<string, object?>
                            {
                                ["projectId"] = project.Id,
                                ["projectPath"] = pomPath,
                                ["scanner"] = nameof(JavaCliInspector),
                                ["vulnerabilities"] = result.Vulnerabilities.Count,
                                ["outdated"] = result.Outdated.Count,
                                ["vulnerableCommand"] = result.VulnerableCommand,
                                ["vulnerableExitCode"] = result.VulnerableExitCode,
                                ["vulnerableStdErrPreview"] = result.VulnerableStdErrPreview,
                                ["vulnerableStdOutPreview"] = result.VulnerableStdOutPreview,
                                ["outdatedCommand"] = result.OutdatedCommand,
                                ["outdatedExitCode"] = result.OutdatedExitCode,
                                ["outdatedStdErrPreview"] = result.OutdatedStdErrPreview,
                                ["outdatedStdOutPreview"] = result.OutdatedStdOutPreview,
                                ["scannerError"] = result.Error,
                                ["elapsedMs"] = scannerTimer.ElapsedMilliseconds
                            },
                            ct);
                    }
                }
            }
            scanTimer.Stop();

            await _analysisLog.InfoAsync(
                currentStep,
                "All vulnerability and outdated scans completed.",
                context,
                new Dictionary<string, object?>
                {
                    ["vulnerabilityCount"] = vulnerabilities.Count,
                    ["outdatedCount"] = outdated.Count,
                    ["elapsedMs"] = scanTimer.ElapsedMilliseconds
                },
                ct);
            ReportProgress(progress, currentStep, "All scans completed.", 88);

            currentStep = "BuildSnapshot";
            var snapshot = new RepoAnalysisSnapshot
            {
                RepositoryId = repositoryId,
                AnalyzedAtUtc = DateTimeOffset.UtcNow,
                DetectedProjects = projects,
                Components = components,
                Vulnerabilities = vulnerabilities,
                Outdated = outdated
            };

            await _analysisLog.InfoAsync(
                currentStep,
                "Snapshot assembled.",
                context,
                new Dictionary<string, object?>
                {
                    ["projectCount"] = snapshot.DetectedProjects.Count,
                    ["componentCount"] = snapshot.Components.Count,
                    ["vulnerabilities"] = snapshot.Vulnerabilities.Count,
                    ["outdated"] = snapshot.Outdated.Count
                },
                ct);
            ReportProgress(progress, currentStep, "Snapshot assembled.", 93);

            await ReplaceSnapshotAndGlobalFindingsAsync(snapshot, context, ct);
            ReportProgress(progress, "PersistSnapshot", "Snapshot and findings saved.", 97);

            totalTimer.Stop();
            await _analysisLog.InfoAsync(
                "Completed",
                "Analysis completed successfully.",
                context,
                new Dictionary<string, object?>
                {
                    ["analyzedAtUtc"] = snapshot.AnalyzedAtUtc,
                    ["startedAtUtc"] = analysisStarted,
                    ["totalElapsedMs"] = totalTimer.ElapsedMilliseconds
                },
                ct);
            ReportProgress(progress, "Completed", "Analysis completed successfully.", 100);

            return snapshot;
        }
        catch (Exception ex)
        {
            await _analysisLog.ErrorAsync(
                "Failed",
                "Analysis failed.",
                context,
                ex,
                new Dictionary<string, object?>
                {
                    ["failedStep"] = currentStep,
                    ["startedAtUtc"] = analysisStarted,
                    ["elapsedMs"] = totalTimer.ElapsedMilliseconds
                },
                ct);
            ReportProgress(progress, "Failed", "Analysis failed.", 100);

            throw new InvalidOperationException("Analysis failed. See analysis logs for details.", ex);
        }
        finally
        {
            try
            {
                if (Directory.Exists(analysisRoot))
                {
                    Directory.Delete(analysisRoot, recursive: true);
                }
            }
            catch
            {
                // Best-effort cleanup.
            }
        }
    }

    public async Task ClearRepositoryAnalysisAsync(string repositoryId, CancellationToken ct = default)
    {
        var snapshots = await _data.GetSnapshotsAsync(ct);
        snapshots.RemoveAll(s => s.RepositoryId == repositoryId);
        await _data.SaveSnapshotsAsync(snapshots, ct);

        var global = await _data.GetGlobalFindingsAsync(ct);
        foreach (var entry in global)
        {
            entry.AffectedLocations.RemoveAll(x => x.RepositoryId == repositoryId);
        }

        global.RemoveAll(x => x.AffectedLocations.Count == 0);
        await _data.SaveGlobalFindingsAsync(global, ct);
    }

    private async Task ReplaceSnapshotAndGlobalFindingsAsync(RepoAnalysisSnapshot snapshot, AnalysisLogContext context, CancellationToken ct)
    {
        var persistTimer = System.Diagnostics.Stopwatch.StartNew();
        var snapshots = await _data.GetSnapshotsAsync(ct);
        snapshots.RemoveAll(s => s.RepositoryId == snapshot.RepositoryId);
        snapshots.Add(snapshot);
        await _data.SaveSnapshotsAsync(snapshots, ct);
        persistTimer.Stop();

        await _analysisLog.InfoAsync(
            "PersistSnapshot",
            "Snapshot persisted to storage.",
            context,
            new Dictionary<string, object?>
            {
                ["repositoryId"] = snapshot.RepositoryId,
                ["elapsedMs"] = persistTimer.ElapsedMilliseconds
            },
            ct);

        var globalTimer = System.Diagnostics.Stopwatch.StartNew();
        var global = await _data.GetGlobalFindingsAsync(ct);

        foreach (var entry in global)
        {
            entry.AffectedLocations.RemoveAll(x => x.RepositoryId == snapshot.RepositoryId);
        }

        global.RemoveAll(x => x.AffectedLocations.Count == 0);

        var findings = snapshot.Vulnerabilities.Concat(snapshot.Outdated).ToList();
        var projectPathMap = snapshot.DetectedProjects.ToDictionary(p => p.Id, p => p.Path, StringComparer.Ordinal);

        foreach (var finding in findings)
        {
            var key = BuildGlobalFindingKey(finding);
            var existing = global.FirstOrDefault(x => x.Key == key);
            if (existing is null)
            {
                existing = new GlobalFinding
                {
                    Key = key,
                    Type = finding.Type,
                    Ecosystem = finding.Ecosystem,
                    PackageName = finding.PackageName,
                    InstalledVersion = finding.InstalledVersion ?? string.Empty,
                    FixedVersion = finding.FixedVersion,
                    Severity = NormalizeSeverity(finding.Severity),
                    Advisory = finding.Advisory,
                    SourceTool = finding.SourceTool,
                    AnalyzedAtUtc = snapshot.AnalyzedAtUtc
                };
                global.Add(existing);
            }
            else
            {
                existing.FixedVersion = finding.FixedVersion ?? existing.FixedVersion;
                existing.Severity = MaxSeverity(existing.Severity, NormalizeSeverity(finding.Severity));
                existing.Advisory = finding.Advisory ?? existing.Advisory;
                existing.AnalyzedAtUtc = snapshot.AnalyzedAtUtc;
            }

            var location = new AffectedLocation
            {
                RepositoryId = finding.RepositoryId,
                ProjectId = finding.ProjectId,
                Path = projectPathMap.GetValueOrDefault(finding.ProjectId) ?? string.Empty
            };

            if (!existing.AffectedLocations.Any(x =>
                    x.RepositoryId == location.RepositoryId &&
                    x.ProjectId == location.ProjectId &&
                    string.Equals(x.Path, location.Path, StringComparison.OrdinalIgnoreCase)))
            {
                existing.AffectedLocations.Add(location);
            }
        }

        global.RemoveAll(x => x.AffectedLocations.Count == 0);
        await _data.SaveGlobalFindingsAsync(global, ct);
        globalTimer.Stop();

        await _analysisLog.InfoAsync(
            "UpdateGlobalFindings",
            "Global findings index updated.",
            context,
            new Dictionary<string, object?>
            {
                ["globalFindingCount"] = global.Count,
                ["repositoryId"] = snapshot.RepositoryId,
                ["elapsedMs"] = globalTimer.ElapsedMilliseconds
            },
            ct);
    }

    private async Task<List<DetectedProject>> DetectProjectsAsync(
        string repositoryId,
        Dictionary<string, string> fileMap,
        AnalysisLogContext context,
        CancellationToken ct)
    {
        var projects = new List<DetectedProject>();

        foreach (var csproj in fileMap.Keys.Where(k => k.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)))
        {
            var content = fileMap[csproj];
            try
            {
                projects.Add(ParseDotNetProject(repositoryId, csproj, content));
            }
            catch (Exception ex)
            {
                await _analysisLog.WarningAsync(
                    "ParseCsproj",
                    "Skipping invalid .csproj file during project detection.",
                    context,
                    new Dictionary<string, object?>
                    {
                        ["path"] = csproj,
                        ["error"] = ex.Message,
                        ["contentPreview"] = BuildContentPreview(content)
                    },
                    ct);
            }
        }

        foreach (var packageJson in fileMap.Keys.Where(k => k.EndsWith("package.json", StringComparison.OrdinalIgnoreCase)))
        {
            var content = fileMap[packageJson];
            try
            {
                projects.Add(ParseNodeProject(repositoryId, packageJson, content));
            }
            catch (Exception ex)
            {
                await _analysisLog.WarningAsync(
                    "ParsePackageJsonProject",
                    "Skipping invalid package.json during project detection.",
                    context,
                    new Dictionary<string, object?>
                    {
                        ["path"] = packageJson,
                        ["error"] = ex.Message
                    },
                    ct);
            }
        }

        foreach (var requirements in fileMap.Keys.Where(k => k.EndsWith("requirements.txt", StringComparison.OrdinalIgnoreCase)))
        {
            projects.Add(new DetectedProject
            {
                RepositoryId = repositoryId,
                Name = Path.GetFileName(Path.GetDirectoryName(requirements) ?? requirements),
                Path = requirements,
                ProjectType = "Python",
                Framework = "Python",
                Version = "UNKNOWN",
                Language = "Python"
            });
        }

        foreach (var pom in fileMap.Keys.Where(k => k.EndsWith("pom.xml", StringComparison.OrdinalIgnoreCase)))
        {
            var content = fileMap[pom];
            try
            {
                projects.Add(ParsePomProject(repositoryId, pom, content));
            }
            catch (Exception ex)
            {
                await _analysisLog.WarningAsync(
                    "ParsePomProject",
                    "Skipping invalid pom.xml during project detection.",
                    context,
                    new Dictionary<string, object?>
                    {
                        ["path"] = pom,
                        ["error"] = ex.Message
                    },
                    ct);
            }
        }

        return projects;
    }

    private async Task<List<Component>> ExtractComponentsAsync(
        string repositoryId,
        List<DetectedProject> projects,
        Dictionary<string, string> fileMap,
        AnalysisLogContext context,
        CancellationToken ct)
    {
        var components = new List<Component>();
        var centralVersions = ParseCentralPackageVersions(fileMap);

        foreach (var project in projects)
        {
            var path = NormalizePath(project.Path);

            if (path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
            {
                var content = fileMap[path];
                try
                {
                    components.AddRange(ParseCsprojPackages(repositoryId, project.Id, path, content, centralVersions));
                }
                catch (Exception ex)
                {
                    await _analysisLog.WarningAsync(
                        "ParseCsprojPackages",
                        "Skipping package extraction for invalid .csproj.",
                        context,
                        new Dictionary<string, object?>
                        {
                            ["path"] = path,
                            ["projectId"] = project.Id,
                            ["error"] = ex.Message
                        },
                        ct);
                }

                var packagesConfigPath = CombineRepoPath(Path.GetDirectoryName(path) ?? string.Empty, "packages.config");
                if (fileMap.TryGetValue(packagesConfigPath, out var packagesConfigContent))
                {
                    components.AddRange(ParsePackagesConfig(repositoryId, project.Id, packagesConfigPath, packagesConfigContent));
                }
            }

            if (path.EndsWith("package.json", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    components.AddRange(ParsePackageJsonComponents(repositoryId, project.Id, path, fileMap[path]));
                }
                catch (Exception ex)
                {
                    await _analysisLog.WarningAsync(
                        "ParsePackageJsonComponents",
                        "Skipping component extraction for invalid package.json.",
                        context,
                        new Dictionary<string, object?>
                        {
                            ["path"] = path,
                            ["projectId"] = project.Id,
                            ["error"] = ex.Message
                        },
                        ct);
                }
            }

            if (path.EndsWith("requirements.txt", StringComparison.OrdinalIgnoreCase))
            {
                components.AddRange(ParseRequirementsComponents(repositoryId, project.Id, path, fileMap[path]));
            }

            if (path.EndsWith("pom.xml", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    components.AddRange(ParsePomComponents(repositoryId, project.Id, path, fileMap[path]));
                }
                catch (Exception ex)
                {
                    await _analysisLog.WarningAsync(
                        "ParsePomComponents",
                        "Skipping component extraction for invalid pom.xml.",
                        context,
                        new Dictionary<string, object?>
                        {
                            ["path"] = path,
                            ["projectId"] = project.Id,
                            ["error"] = ex.Message
                        },
                        ct);
                }
            }
        }

        return components;
    }

    private static Dictionary<string, string> ParseCentralPackageVersions(Dictionary<string, string> fileMap)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var kvp in fileMap.Where(kvp => kvp.Key.EndsWith("Directory.Packages.props", StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                var doc = XDocument.Parse(kvp.Value);
                foreach (var item in doc.Descendants("PackageVersion"))
                {
                    var include = item.Attribute("Include")?.Value;
                    var version = item.Attribute("Version")?.Value;
                    if (!string.IsNullOrWhiteSpace(include) && !string.IsNullOrWhiteSpace(version))
                    {
                        result[include] = version;
                    }
                }
            }
            catch
            {
                // Best-effort parse.
            }
        }

        return result;
    }

    private async Task<Dictionary<string, string>> CollectDotNetExtraFiles(
        string csprojPath,
        Dictionary<string, string> fileMap,
        AnalysisLogContext context,
        CancellationToken ct)
    {
        var extra = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var projectPath = NormalizePath(csprojPath);
        if (!fileMap.TryGetValue(projectPath, out var projectContent))
        {
            return extra;
        }

        var projectDir = Path.GetDirectoryName(csprojPath) ?? string.Empty;

        var packagesConfigPath = CombineRepoPath(projectDir, "packages.config");
        if (fileMap.TryGetValue(packagesConfigPath, out var packagesConfig))
        {
            extra[packagesConfigPath] = packagesConfig;
        }

        foreach (var inheritedPath in EnumerateAncestorConfigPaths(projectDir))
        {
            if (fileMap.TryGetValue(inheritedPath, out var inheritedContent))
            {
                extra[inheritedPath] = inheritedContent;
            }
        }

        foreach (var referencePath in ParseProjectReferencePaths(projectPath, projectContent))
        {
            if (!fileMap.TryGetValue(referencePath, out var referenceContent))
            {
                continue;
            }

            extra[referencePath] = referenceContent;
            var referenceDir = Path.GetDirectoryName(referencePath) ?? string.Empty;
            foreach (var inheritedPath in EnumerateAncestorConfigPaths(referenceDir))
            {
                if (fileMap.TryGetValue(inheritedPath, out var inheritedContent))
                {
                    extra[inheritedPath] = inheritedContent;
                }
            }
        }

        var dir = projectDir;
        while (!string.IsNullOrWhiteSpace(dir))
        {
            var centralPath = CombineRepoPath(dir, "Directory.Packages.props");
            if (fileMap.TryGetValue(centralPath, out var centralContent))
            {
                extra[centralPath] = centralContent;
                break;
            }

            dir = Path.GetDirectoryName(dir) ?? string.Empty;
        }

        await _analysisLog.DebugAsync(
            "CollectDotNetExtras",
            "Collected additional .NET scan files.",
            context,
            new Dictionary<string, object?>
            {
                ["projectPath"] = projectPath,
                ["extraFileCount"] = extra.Count,
                ["extraFiles"] = extra.Keys.OrderBy(x => x).ToList()
            },
            ct);

        return extra;
    }

    private static IEnumerable<string> ParseProjectReferencePaths(string projectPath, string projectContent)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var doc = XDocument.Parse(projectContent);
            var projectDir = Path.GetDirectoryName(projectPath) ?? string.Empty;

            foreach (var reference in doc.Descendants("ProjectReference"))
            {
                var include = reference.Attribute("Include")?.Value;
                if (string.IsNullOrWhiteSpace(include))
                {
                    continue;
                }

                var normalized = ResolveRelativeRepoPath(projectDir, include);
                result.Add(normalized);
            }
        }
        catch
        {
            // Best effort: project reference parsing is optional.
        }

        return result;
    }

    private static string ResolveRelativeRepoPath(string baseDirectory, string relativePath)
    {
        var baseSegments = NormalizePath(baseDirectory)
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .ToList();
        var relativeSegments = NormalizePath(relativePath)
            .Split('/', StringSplitOptions.RemoveEmptyEntries);

        foreach (var segment in relativeSegments)
        {
            if (segment == ".")
            {
                continue;
            }

            if (segment == "..")
            {
                if (baseSegments.Count > 0)
                {
                    baseSegments.RemoveAt(baseSegments.Count - 1);
                }

                continue;
            }

            baseSegments.Add(segment);
        }

        return string.Join('/', baseSegments);
    }

    private static IEnumerable<string> EnumerateAncestorConfigPaths(string startDirectory)
    {
        const string directoryPackagesProps = "Directory.Packages.props";
        const string directoryBuildProps = "Directory.Build.props";
        const string directoryBuildTargets = "Directory.Build.targets";
        const string nuGetConfig = "NuGet.Config";
        const string globalJson = "global.json";

        var current = NormalizePath(startDirectory);
        while (true)
        {
            yield return CombineRepoPath(current, directoryPackagesProps);
            yield return CombineRepoPath(current, directoryBuildProps);
            yield return CombineRepoPath(current, directoryBuildTargets);
            yield return CombineRepoPath(current, nuGetConfig);
            yield return CombineRepoPath(current, globalJson);

            if (string.IsNullOrWhiteSpace(current))
            {
                break;
            }

            current = NormalizePath(Path.GetDirectoryName(current) ?? string.Empty);
        }
    }

    private static string BuildContentPreview(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return string.Empty;
        }

        var preview = content.Replace("\r", string.Empty, StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal);
        if (preview.Length <= 180)
        {
            return preview;
        }

        return $"{preview[..180]}... [TRUNCATED]";
    }

    private static DetectedProject ParseDotNetProject(string repositoryId, string path, string content)
    {
        var doc = XDocument.Parse(content);
        var tfm = doc.Descendants("TargetFramework").FirstOrDefault()?.Value
            ?? doc.Descendants("TargetFrameworks").FirstOrDefault()?.Value.Split(';', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()
            ?? doc.Descendants("TargetFrameworkVersion").FirstOrDefault()?.Value;

        var frameworkType = DetectDotNetProjectType(tfm);

        return new DetectedProject
        {
            RepositoryId = repositoryId,
            Name = Path.GetFileNameWithoutExtension(path),
            Path = path,
            ProjectType = frameworkType,
            Framework = frameworkType,
            Version = tfm,
            Language = "C#"
        };
    }

    private static string DetectDotNetProjectType(string? tfm)
    {
        if (string.IsNullOrWhiteSpace(tfm))
        {
            return ".NET";
        }

        var value = tfm.Trim().ToLowerInvariant();
        if (value.StartsWith("v4.", StringComparison.Ordinal) ||
            (value.StartsWith("net", StringComparison.Ordinal) && value.Length >= 5 && char.IsDigit(value[3]) && char.IsDigit(value[4])))
        {
            return ".NET Framework";
        }

        return "Modern .NET";
    }

    private static IEnumerable<Component> ParseCsprojPackages(string repositoryId, string projectId, string path, string content, Dictionary<string, string> centralVersions)
    {
        var doc = XDocument.Parse(content);

        var packageRefs = doc.Descendants("PackageReference")
            .Select(x => new
            {
                Name = x.Attribute("Include")?.Value ?? x.Attribute("Update")?.Value,
                Version = x.Attribute("Version")?.Value ?? x.Element("Version")?.Value
            })
            .Where(x => !string.IsNullOrWhiteSpace(x.Name));

        foreach (var p in packageRefs)
        {
            var version = p.Version;
            if (string.IsNullOrWhiteSpace(version) && centralVersions.TryGetValue(p.Name!, out var central))
            {
                version = central;
            }

            yield return new Component
            {
                Ecosystem = "NuGet",
                Name = p.Name!,
                Version = version ?? string.Empty,
                ProjectId = projectId,
                RepositoryId = repositoryId,
                Path = path
            };
        }
    }

    private static IEnumerable<Component> ParsePackagesConfig(string repositoryId, string projectId, string path, string content)
    {
        XDocument doc;
        try
        {
            doc = XDocument.Parse(content);
        }
        catch
        {
            yield break;
        }

        foreach (var package in doc.Descendants("package"))
        {
            var name = package.Attribute("id")?.Value;
            var version = package.Attribute("version")?.Value;

            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            yield return new Component
            {
                Ecosystem = "NuGet",
                Name = name,
                Version = version ?? string.Empty,
                ProjectId = projectId,
                RepositoryId = repositoryId,
                Path = path
            };
        }
    }

    private static DetectedProject ParseNodeProject(string repositoryId, string path, string content)
    {
        using var doc = JsonDocument.Parse(content);

        string projectType = "Node.js";
        string? framework = null;
        string? version = null;

        if (doc.RootElement.TryGetProperty("dependencies", out var dependencies))
        {
            if (dependencies.TryGetProperty("@angular/core", out _))
            {
                projectType = "Angular";
                framework = "Angular";
            }
            else if (dependencies.TryGetProperty("react", out _))
            {
                projectType = "React";
                framework = "React";
            }
        }

        if (doc.RootElement.TryGetProperty("engines", out var engines) && engines.TryGetProperty("node", out var nodeVer))
        {
            version = nodeVer.GetString();
        }

        return new DetectedProject
        {
            RepositoryId = repositoryId,
            Name = Path.GetFileName(Path.GetDirectoryName(path) ?? path),
            Path = path,
            ProjectType = projectType,
            Framework = framework ?? "Node.js",
            Version = version ?? "UNKNOWN",
            Language = "JavaScript/TypeScript"
        };
    }

    private static IEnumerable<Component> ParsePackageJsonComponents(string repositoryId, string projectId, string path, string content)
    {
        using var doc = JsonDocument.Parse(content);

        foreach (var propertyName in new[] { "dependencies", "devDependencies" })
        {
            if (!doc.RootElement.TryGetProperty(propertyName, out var deps) || deps.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            foreach (var dep in deps.EnumerateObject())
            {
                yield return new Component
                {
                    Ecosystem = "npm",
                    Name = dep.Name,
                    Version = dep.Value.GetString() ?? string.Empty,
                    ProjectId = projectId,
                    RepositoryId = repositoryId,
                    Path = path
                };
            }
        }
    }

    private static IEnumerable<Component> ParseRequirementsComponents(string repositoryId, string projectId, string path, string content)
    {
        foreach (var line in content.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = line.Trim();
            if (string.IsNullOrWhiteSpace(trimmed) || trimmed.StartsWith("#", StringComparison.Ordinal))
            {
                continue;
            }

            var separators = new[] { "==", ">=", "<=", "~=" };
            var name = trimmed;
            var version = string.Empty;

            foreach (var separator in separators)
            {
                var parts = trimmed.Split(separator, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2)
                {
                    name = parts[0].Trim();
                    version = parts[1].Trim();
                    break;
                }
            }

            yield return new Component
            {
                Ecosystem = "python",
                Name = name,
                Version = version,
                ProjectId = projectId,
                RepositoryId = repositoryId,
                Path = path
            };
        }
    }

    private static DetectedProject ParsePomProject(string repositoryId, string path, string content)
    {
        var doc = XDocument.Parse(content);
        var ns = doc.Root?.Name.Namespace ?? XNamespace.None;
        var artifactId = GetXmlValue(doc.Root, ns, "artifactId")
            ?? Path.GetFileName(Path.GetDirectoryName(path) ?? path);
        var version = GetXmlValue(doc.Root, ns, "version")
            ?? GetXmlValue(doc.Root?.Element(ns + "parent"), ns, "version")
            ?? "UNKNOWN";

        return new DetectedProject
        {
            RepositoryId = repositoryId,
            Name = artifactId,
            Path = path,
            ProjectType = "Java (Maven)",
            Framework = "Maven",
            Version = version,
            Language = "Java"
        };
    }

    private static IEnumerable<Component> ParsePomComponents(string repositoryId, string projectId, string path, string content)
    {
        var doc = XDocument.Parse(content);
        var ns = doc.Root?.Name.Namespace ?? XNamespace.None;

        foreach (var dep in doc.Descendants(ns + "dependency"))
        {
            var groupId = dep.Element(ns + "groupId")?.Value?.Trim();
            var artifactId = dep.Element(ns + "artifactId")?.Value?.Trim();
            var version = dep.Element(ns + "version")?.Value?.Trim() ?? string.Empty;

            if (string.IsNullOrWhiteSpace(groupId) || string.IsNullOrWhiteSpace(artifactId))
            {
                continue;
            }

            yield return new Component
            {
                Ecosystem = "Maven",
                Name = $"{groupId}:{artifactId}",
                Version = version,
                ProjectId = projectId,
                RepositoryId = repositoryId,
                Path = path
            };
        }
    }

    private static string? GetXmlValue(XElement? parent, XNamespace ns, string name)
        => parent?.Element(ns + name)?.Value?.Trim();

    private static string BuildGlobalFindingKey(Finding finding)
        => $"{finding.Type}|{finding.Ecosystem}|{finding.PackageName}|{finding.InstalledVersion}";

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
            "medium" => "Medium",
            "moderate" => "Medium",
            "low" => "Low",
            _ => "Unknown"
        };
    }

    private static string MaxSeverity(string a, string b)
    {
        var rank = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["Unknown"] = 0,
            ["Low"] = 1,
            ["Medium"] = 2,
            ["High"] = 3,
            ["Critical"] = 4
        };

        return rank.GetValueOrDefault(a, 0) >= rank.GetValueOrDefault(b, 0) ? a : b;
    }

    private static string NormalizePath(string path) => path.Replace('\\', '/').TrimStart('/');

    private static string CombineRepoPath(string dir, string name)
    {
        var combined = Path.Combine(dir, name);
        return NormalizePath(combined);
    }

    private static bool IsManifestPath(string path)
    {
        return path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) ||
               path.EndsWith("packages.config", StringComparison.OrdinalIgnoreCase) ||
               path.EndsWith("Directory.Packages.props", StringComparison.OrdinalIgnoreCase) ||
               path.EndsWith("package.json", StringComparison.OrdinalIgnoreCase) ||
               path.EndsWith("package-lock.json", StringComparison.OrdinalIgnoreCase) ||
               path.EndsWith("requirements.txt", StringComparison.OrdinalIgnoreCase) ||
               path.EndsWith("pom.xml", StringComparison.OrdinalIgnoreCase) ||
               path.EndsWith("build.gradle", StringComparison.OrdinalIgnoreCase) ||
               path.EndsWith("build.gradle.kts", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsDotNetProject(DetectedProject project)
    {
        return project.Path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) ||
               project.ProjectType.Contains(".NET", StringComparison.OrdinalIgnoreCase) ||
               project.Framework.Contains(".NET", StringComparison.OrdinalIgnoreCase) ||
               project.Language.Equals("C#", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsJavaProject(DetectedProject project)
    {
        return project.Path.EndsWith("pom.xml", StringComparison.OrdinalIgnoreCase) ||
               project.ProjectType.Contains("Java", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(project.Framework, "Maven", StringComparison.OrdinalIgnoreCase) ||
               project.Language.Equals("Java", StringComparison.OrdinalIgnoreCase);
    }

    private static string MapProviderType(ConnectionType type)
        => type == ConnectionType.AzureDevOpsServer ? "ADS" : "GitHub";

    private static void ReportProgress(IProgress<AnalyzeProgress>? progress, string step, string message, int? percent)
    {
        progress?.Report(new AnalyzeProgress
        {
            Step = step,
            Message = message,
            Percent = percent
        });
    }
}
