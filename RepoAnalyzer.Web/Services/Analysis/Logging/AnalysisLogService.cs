using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace RepoAnalyzer.Web.Services.Analysis.Logging;

public sealed class AnalysisLogService : IAnalysisLog
{
    private const long MaxLogSizeBytes = 10L * 1024L * 1024L;
    private const int MaxRotatedFiles = 10;
    private const int MaxExceptionStackLength = 16000;
    private const string CurrentLogName = "analysis.log";

    private static readonly Regex BearerRegex = new("(?i)bearer\\s+[a-z0-9\\-_.=]+", RegexOptions.Compiled);
    private static readonly Regex KeyValueSecretRegex = new("(?i)(token|pat|password|secret|authorization|cookie)\\s*[:=]\\s*[^\\s,;]+", RegexOptions.Compiled);

    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly string _logsPath;
    private readonly string _currentLogPath;
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public AnalysisLogService(IConfiguration configuration)
    {
        var dataPath = configuration["DataPath"] ?? "/app/data";
        _logsPath = Path.Combine(dataPath, "logs");
        _currentLogPath = Path.Combine(_logsPath, CurrentLogName);
        Directory.CreateDirectory(_logsPath);

        if (!File.Exists(_currentLogPath))
        {
            File.WriteAllText(_currentLogPath, string.Empty);
        }
    }

    public Task TraceAsync(string step, string message, AnalysisLogContext context, Dictionary<string, object?>? data = null, CancellationToken ct = default)
        => WriteAsync("Trace", step, message, context, data, null, ct);

    public Task DebugAsync(string step, string message, AnalysisLogContext context, Dictionary<string, object?>? data = null, CancellationToken ct = default)
        => WriteAsync("Debug", step, message, context, data, null, ct);

    public Task InfoAsync(string step, string message, AnalysisLogContext context, Dictionary<string, object?>? data = null, CancellationToken ct = default)
        => WriteAsync("Info", step, message, context, data, null, ct);

    public Task WarningAsync(string step, string message, AnalysisLogContext context, Dictionary<string, object?>? data = null, CancellationToken ct = default)
        => WriteAsync("Warning", step, message, context, data, null, ct);

    public Task ErrorAsync(string step, string message, AnalysisLogContext context, Exception exception, Dictionary<string, object?>? data = null, CancellationToken ct = default)
        => WriteAsync("Error", step, message, context, data, exception, ct);

    public string GetCurrentLogFilePath() => _currentLogPath;

    public async Task<List<string>> ReadLatestLinesAsync(int lines, CancellationToken ct = default)
    {
        lines = Math.Clamp(lines <= 0 ? 2000 : lines, 1, 5000);
        await _writeLock.WaitAsync(ct);

        try
        {
            EnsureCurrentLogExists();

            var queue = new Queue<string>(lines);
            foreach (var line in File.ReadLines(_currentLogPath))
            {
                if (queue.Count == lines)
                {
                    queue.Dequeue();
                }

                queue.Enqueue(line);
            }

            return queue.ToList();
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task ClearAsync(CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct);

        try
        {
            EnsureCurrentLogExists();
            var info = new FileInfo(_currentLogPath);
            if (info.Exists && info.Length > 0)
            {
                RotateCurrentLog();
            }

            await File.WriteAllTextAsync(_currentLogPath, string.Empty, ct);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task WriteAsync(
        string level,
        string step,
        string message,
        AnalysisLogContext context,
        Dictionary<string, object?>? data,
        Exception? exception,
        CancellationToken ct)
    {
        await _writeLock.WaitAsync(ct);

        try
        {
            EnsureCurrentLogExists();
            RotateIfNeeded();

            var entry = new AnalysisLogEntry
            {
                TimestampUtc = DateTimeOffset.UtcNow,
                Level = level,
                AnalysisRunId = context.AnalysisRunId,
                ConnectionId = context.ConnectionId,
                ProviderType = context.ProviderType,
                WorkspaceId = context.WorkspaceId,
                WorkspaceName = context.WorkspaceName,
                RepositoryId = context.RepositoryId,
                RepositoryName = SanitizeString(context.RepositoryName),
                Step = step,
                Message = SanitizeString(message) ?? string.Empty,
                Data = SanitizeData(data),
                Exception = exception is null ? null : BuildExceptionData(exception)
            };

            var line = JsonSerializer.Serialize(entry, _jsonOptions) + Environment.NewLine;
            await using var fs = new FileStream(_currentLogPath, FileMode.Append, FileAccess.Write, FileShare.Read);
            await using var writer = new StreamWriter(fs);
            await writer.WriteAsync(line.AsMemory(), ct);
            await writer.FlushAsync();
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private void RotateIfNeeded()
    {
        var info = new FileInfo(_currentLogPath);
        if (!info.Exists || info.Length <= MaxLogSizeBytes)
        {
            return;
        }

        RotateCurrentLog();
    }

    private void RotateCurrentLog()
    {
        var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
        var rotatedPath = Path.Combine(_logsPath, $"analysis-{stamp}.log");
        var sequence = 1;

        while (File.Exists(rotatedPath))
        {
            rotatedPath = Path.Combine(_logsPath, $"analysis-{stamp}-{sequence}.log");
            sequence++;
        }

        File.Move(_currentLogPath, rotatedPath);
        File.WriteAllText(_currentLogPath, string.Empty);
        TrimRotatedFiles();
    }

    private void TrimRotatedFiles()
    {
        var files = Directory.GetFiles(_logsPath, "analysis-*.log")
            .OrderByDescending(Path.GetFileName)
            .ToList();

        foreach (var file in files.Skip(MaxRotatedFiles))
        {
            File.Delete(file);
        }
    }

    private void EnsureCurrentLogExists()
    {
        Directory.CreateDirectory(_logsPath);
        if (!File.Exists(_currentLogPath))
        {
            File.WriteAllText(_currentLogPath, string.Empty);
        }
    }

    private static Dictionary<string, object?>? SanitizeData(Dictionary<string, object?>? data)
    {
        if (data is null)
        {
            return null;
        }

        var sanitized = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in data)
        {
            if (IsSensitiveKey(pair.Key))
            {
                sanitized[pair.Key] = "[REDACTED]";
                continue;
            }

            sanitized[pair.Key] = SanitizeObject(pair.Value);
        }

        return sanitized;
    }

    private static object? SanitizeObject(object? value)
    {
        if (value is null)
        {
            return null;
        }

        if (value is string text)
        {
            return SanitizeString(text);
        }

        if (value is IEnumerable<string> stringList)
        {
            return stringList.Select(SanitizeString).ToList();
        }

        if (value is Dictionary<string, object?> nested)
        {
            return SanitizeData(nested);
        }

        return value;
    }

    private static bool IsSensitiveKey(string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return false;
        }

        var normalized = key.Trim().ToLowerInvariant();
        return normalized is "token" or
               "rawtoken" or
               "encryptedtoken" or
               "password" or
               "secret" or
               "authorization" or
               "cookie" or
               "pat" or
               "accesstoken" or
               "refreshtoken" or
               "apikey";
    }

    private static string? SanitizeString(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return input;
        }

        var value = input;
        value = BearerRegex.Replace(value, "Bearer [REDACTED]");
        value = KeyValueSecretRegex.Replace(value, "$1=[REDACTED]");

        if (Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            try
            {
                var builder = new UriBuilder(uri)
                {
                    UserName = string.Empty,
                    Password = string.Empty,
                    Query = string.Empty,
                    Fragment = string.Empty
                };
                value = builder.Uri.ToString();
            }
            catch
            {
                // Keep best effort sanitized value.
            }
        }

        return value;
    }

    private static AnalysisExceptionData BuildExceptionData(Exception exception)
    {
        var stack = exception.StackTrace ?? string.Empty;
        if (stack.Length > MaxExceptionStackLength)
        {
            stack = stack[..MaxExceptionStackLength];
        }

        return new AnalysisExceptionData
        {
            Type = exception.GetType().FullName ?? exception.GetType().Name,
            Message = SanitizeString(exception.Message) ?? string.Empty,
            StackTrace = SanitizeString(stack)
        };
    }

    private sealed class AnalysisLogEntry
    {
        public DateTimeOffset TimestampUtc { get; set; }
        public string Level { get; set; } = "Info";
        public string AnalysisRunId { get; set; } = string.Empty;
        public string? ConnectionId { get; set; }
        public string? ProviderType { get; set; }
        public string? WorkspaceId { get; set; }
        public string? WorkspaceName { get; set; }
        public string? RepositoryId { get; set; }
        public string? RepositoryName { get; set; }
        public string Step { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;
        public Dictionary<string, object?>? Data { get; set; }
        public AnalysisExceptionData? Exception { get; set; }
    }

    private sealed class AnalysisExceptionData
    {
        public string Type { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;
        public string? StackTrace { get; set; }
    }
}
