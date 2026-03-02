using System.Diagnostics;
using System.Text;

namespace RepoAnalyzer.Web.Services.Analysis;

public sealed class SafeCliRunner
{
    private static readonly HashSet<string> AllowedCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "dotnet",
        "npm",
        "python3",
        "mvn"
    };

    public async Task<(int ExitCode, string StdOut, string StdErr)> RunAsync(
        string fileName,
        IEnumerable<string> arguments,
        string workingDirectory,
        TimeSpan timeout,
        int outputLimit,
        CancellationToken ct = default)
    {
        if (!AllowedCommands.Contains(fileName))
        {
            throw new InvalidOperationException($"Command not allowlisted: {fileName}");
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory
        };

        foreach (var arg in arguments)
        {
            startInfo.ArgumentList.Add(arg);
        }

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException($"Could not start process: {fileName}");

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linkedCts.CancelAfter(timeout);

        var stdoutTask = ReadLimitedAsync(process.StandardOutput, outputLimit, linkedCts.Token);
        var stderrTask = ReadLimitedAsync(process.StandardError, outputLimit, linkedCts.Token);

        try
        {
            await process.WaitForExitAsync(linkedCts.Token);
        }
        catch (OperationCanceledException)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch
            {
                // Best-effort kill.
            }

            return (-1, string.Empty, "Command timed out.");
        }

        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        return (process.ExitCode, stdout, stderr);
    }

    private static async Task<string> ReadLimitedAsync(StreamReader reader, int limit, CancellationToken ct)
    {
        var buffer = new StringBuilder();
        var chunk = new char[1024];

        while (!reader.EndOfStream)
        {
            var read = await reader.ReadAsync(chunk.AsMemory(0, chunk.Length), ct);
            if (read == 0)
            {
                break;
            }

            if (buffer.Length < limit)
            {
                var take = Math.Min(read, limit - buffer.Length);
                buffer.Append(chunk, 0, take);
            }
        }

        return buffer.ToString();
    }
}
