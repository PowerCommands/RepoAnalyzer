using System.Collections.Concurrent;
using System.Text.Json;

namespace RepoAnalyzer.Web.Services.Storage;

public sealed class JsonFileStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _rootPath;

    public JsonFileStore(IConfiguration configuration)
    {
        _rootPath = configuration["DataPath"] ?? "/app/data";
        Directory.CreateDirectory(_rootPath);
    }

    public async Task<List<T>> ReadListAsync<T>(string fileName, CancellationToken cancellationToken = default)
    {
        var filePath = GetPath(fileName);
        var lck = _locks.GetOrAdd(filePath, _ => new SemaphoreSlim(1, 1));

        await lck.WaitAsync(cancellationToken);
        try
        {
            if (!File.Exists(filePath))
            {
                return new List<T>();
            }

            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.None);
            var result = await JsonSerializer.DeserializeAsync<List<T>>(stream, SerializerOptions, cancellationToken);
            return result ?? new List<T>();
        }
        finally
        {
            lck.Release();
        }
    }

    public async Task WriteListAsync<T>(string fileName, List<T> items, CancellationToken cancellationToken = default)
    {
        var filePath = GetPath(fileName);
        var tempPath = filePath + ".tmp";
        var lck = _locks.GetOrAdd(filePath, _ => new SemaphoreSlim(1, 1));

        await lck.WaitAsync(cancellationToken);
        try
        {
            await using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                await JsonSerializer.SerializeAsync(stream, items, SerializerOptions, cancellationToken);
            }

            File.Move(tempPath, filePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
            lck.Release();
        }
    }

    private string GetPath(string fileName) => Path.Combine(_rootPath, fileName);
}
