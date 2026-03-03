using System.IO.Compression;
using System.Text.Json;
using RepoAnalyzer.Web.Dto;

namespace RepoAnalyzer.Web.Services;

public sealed class BackupService
{
    private readonly string _dataPath;

    public BackupService(IConfiguration configuration)
    {
        _dataPath = configuration["DataPath"] ?? "/app/data";
        Directory.CreateDirectory(_dataPath);
    }

    public async Task<(MemoryStream Stream, string FileName, int FileCount)> CreateBackupZipAsync(CancellationToken ct = default)
    {
        var jsonFiles = Directory.GetFiles(_dataPath, "*.json", SearchOption.TopDirectoryOnly)
            .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var memory = new MemoryStream();
        using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var filePath in jsonFiles)
            {
                var entry = zip.CreateEntry(Path.GetFileName(filePath), CompressionLevel.Optimal);
                await using var entryStream = entry.Open();
                await using var fileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                await fileStream.CopyToAsync(entryStream, ct);
            }
        }

        memory.Position = 0;
        var fileName = $"repo-analyzer-backup-{DateTime.UtcNow:yyyyMMdd-HHmmss}.zip";
        return (memory, fileName, jsonFiles.Count);
    }

    public async Task<BackupRestoreResult> RestoreZipAsync(Stream zipStream, CancellationToken ct = default)
    {
        var extracted = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        using var zip = new ZipArchive(zipStream, ZipArchiveMode.Read, leaveOpen: true);
        foreach (var entry in zip.Entries)
        {
            if (string.IsNullOrWhiteSpace(entry.Name))
            {
                continue;
            }

            var fileName = Path.GetFileName(entry.Name);
            if (!fileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            using var reader = new StreamReader(entry.Open());
            var json = await reader.ReadToEndAsync(ct);
            if (string.IsNullOrWhiteSpace(json))
            {
                json = "[]";
            }

            using var _ = JsonDocument.Parse(json);
            extracted[fileName] = json;
        }

        if (extracted.Count == 0)
        {
            return new BackupRestoreResult();
        }

        foreach (var item in extracted)
        {
            var filePath = Path.Combine(_dataPath, item.Key);
            var tempPath = $"{filePath}.restore.tmp";
            await File.WriteAllTextAsync(tempPath, item.Value, ct);
            File.Move(tempPath, filePath, overwrite: true);
        }

        return new BackupRestoreResult
        {
            RestoredFileCount = extracted.Count,
            RestoredFiles = extracted.Keys.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList()
        };
    }
}
