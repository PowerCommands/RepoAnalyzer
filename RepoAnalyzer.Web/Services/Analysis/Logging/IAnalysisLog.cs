namespace RepoAnalyzer.Web.Services.Analysis.Logging;

public interface IAnalysisLog
{
    Task TraceAsync(string step, string message, AnalysisLogContext context, Dictionary<string, object?>? data = null, CancellationToken ct = default);
    Task DebugAsync(string step, string message, AnalysisLogContext context, Dictionary<string, object?>? data = null, CancellationToken ct = default);
    Task InfoAsync(string step, string message, AnalysisLogContext context, Dictionary<string, object?>? data = null, CancellationToken ct = default);
    Task WarningAsync(string step, string message, AnalysisLogContext context, Dictionary<string, object?>? data = null, CancellationToken ct = default);
    Task ErrorAsync(string step, string message, AnalysisLogContext context, Exception exception, Dictionary<string, object?>? data = null, CancellationToken ct = default);

    Task<List<string>> ReadLatestLinesAsync(int lines, CancellationToken ct = default);
    string GetCurrentLogFilePath();
    Task ClearAsync(CancellationToken ct = default);
}
