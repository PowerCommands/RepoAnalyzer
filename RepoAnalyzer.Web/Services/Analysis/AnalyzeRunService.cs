using System.Collections.Concurrent;
using RepoAnalyzer.Web.Models;
using RepoAnalyzer.Web.Services.Analysis.Logging;

namespace RepoAnalyzer.Web.Services.Analysis;

public sealed class AnalyzeRunService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ConcurrentDictionary<string, AnalyzeRun> _runs = new(StringComparer.Ordinal);

    public AnalyzeRunService(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory;
    }

    public AnalyzeRun Start(string repositoryId)
    {
        var run = new AnalyzeRun
        {
            RepositoryId = repositoryId,
            Status = "Queued",
            StartedAtUtc = DateTimeOffset.UtcNow,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
            CurrentStep = "Queued",
            CurrentMessage = "Analysis run queued for execution.",
            ProgressPercent = 0
        };

        _runs[run.Id] = run;

        _ = Task.Run(async () =>
        {
            run.Status = "Running";
            run.UpdatedAtUtc = DateTimeOffset.UtcNow;
            run.CurrentStep = "StartAnalysis";
            run.CurrentMessage = "Analysis started.";
            run.ProgressPercent = 1;

            try
            {
                using var scope = _scopeFactory.CreateScope();
                var analyzer = scope.ServiceProvider.GetRequiredService<RepositoryAnalyzerService>();
                var logger = scope.ServiceProvider.GetRequiredService<IAnalysisLog>();
                var context = new AnalysisLogContext
                {
                    AnalysisRunId = run.Id,
                    RepositoryId = repositoryId
                };
                await logger.InfoAsync("StartAnalysis", "Analysis run queued for execution.", context);

                var progress = new Progress<AnalyzeProgress>(p =>
                {
                    run.CurrentStep = p.Step;
                    run.CurrentMessage = p.Message;
                    run.ProgressPercent = p.Percent;
                    run.UpdatedAtUtc = DateTimeOffset.UtcNow;
                });

                run.Snapshot = await analyzer.AnalyzeRepositoryAsync(repositoryId, run.Id, progress);
                run.Status = "Completed";
                run.CompletedAtUtc = DateTimeOffset.UtcNow;
                run.CurrentStep = "Completed";
                run.CurrentMessage = "Analysis completed successfully.";
                run.ProgressPercent = 100;
                run.UpdatedAtUtc = DateTimeOffset.UtcNow;
            }
            catch (Exception ex)
            {
                run.Status = "Failed";
                run.CompletedAtUtc = DateTimeOffset.UtcNow;
                run.Error = "Analysis failed. Check Tools > Analysis Logs for details.";
                run.CurrentStep = "Failed";
                run.CurrentMessage = "Analysis failed.";
                run.ProgressPercent = 100;
                run.UpdatedAtUtc = DateTimeOffset.UtcNow;

                try
                {
                    using var scope = _scopeFactory.CreateScope();
                    var logger = scope.ServiceProvider.GetRequiredService<IAnalysisLog>();
                    await logger.ErrorAsync(
                        "Failed",
                        "Unhandled exception in analysis run worker.",
                        new AnalysisLogContext
                        {
                            AnalysisRunId = run.Id,
                            RepositoryId = repositoryId
                        },
                        ex);
                }
                catch
                {
                    // Logging must not throw from background worker.
                }
            }
        });

        return run;
    }

    public AnalyzeRun? Get(string runId)
    {
        _runs.TryGetValue(runId, out var run);
        return run;
    }

    public List<AnalyzeRun> GetRecent(int take = 20)
        => _runs.Values
            .OrderByDescending(x => x.UpdatedAtUtc)
            .Take(Math.Clamp(take, 1, 200))
            .ToList();
}
