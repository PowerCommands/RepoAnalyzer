using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using RepoAnalyzer.Web.Models;

namespace RepoAnalyzer.Web.Services.Providers;

public sealed class GitHubProvider : IGitProvider
{
    private readonly HttpClient _httpClient;
    private readonly ConnectionService _connectionService;
    private readonly ILogger<GitHubProvider> _logger;

    private static readonly string[] InterestingFiles =
    {
        ".csproj",
        "package.json",
        "package-lock.json",
        "packages.config",
        "requirements.txt",
        "pom.xml",
        "build.gradle",
        "build.gradle.kts",
        "gradle.lockfile",
        "DESCRIPTION",
        "Directory.Packages.props",
        "Directory.Build.props",
        "Directory.Build.targets",
        "NuGet.Config",
        "global.json"
    };

    public GitHubProvider(IHttpClientFactory httpClientFactory, ConnectionService connectionService, ILogger<GitHubProvider> logger)
    {
        _httpClient = httpClientFactory.CreateClient(nameof(GitHubProvider));
        _connectionService = connectionService;
        _logger = logger;
    }

    public Task<IReadOnlyList<Workspace>> GetWorkspacesAsync(Connection connection, CancellationToken ct = default)
    {
        IReadOnlyList<Workspace> workspaces = new[]
        {
            new Workspace { ConnectionId = connection.Id, Name = "(GitHub)" }
        };
        return Task.FromResult(workspaces);
    }

    public async Task<(bool Success, string Message)> TestConnectionAsync(Connection connection, CancellationToken ct = default)
    {
        var token = _connectionService.GetRawToken(connection);

        try
        {
            var target = ParseTarget(connection.BaseUrlOrOrg);
            var probeUrl = !string.IsNullOrWhiteSpace(token)
                ? "https://api.github.com/user"
                : BuildPublicProbeUrl(target);
            using var request = new HttpRequestMessage(HttpMethod.Get, probeUrl);
            if (!string.IsNullOrWhiteSpace(token))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            }
            request.Headers.UserAgent.ParseAdd("RepoAnalyzerMvp/1.0");

            using var response = await _httpClient.SendAsync(request, ct);
            if (response.IsSuccessStatusCode)
            {
                return string.IsNullOrWhiteSpace(token)
                    ? (true, "Public connection successful (no token).")
                    : (true, "Connection successful.");
            }

            return (false, $"Connection failed: HTTP {(int)response.StatusCode}.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GitHub test connection failed for connection {ConnectionId}", connection.Id);
            return (false, "Connection failed: request error.");
        }
    }

    public async Task<IReadOnlyList<RepositoryEntity>> GetRepositoriesAsync(Connection connection, Workspace? workspace, CancellationToken ct = default)
    {
        var token = _connectionService.GetRawToken(connection);
        var hasToken = !string.IsNullOrWhiteSpace(token);
        if (!hasToken)
        {
            _logger.LogInformation(
                "GitHub repository listing for connection {ConnectionId} is running in public mode (no token).",
                connection.Id);
        }

        var target = ParseTarget(connection.BaseUrlOrOrg);
        var ownerCandidates = target.OwnerCandidates.ToList();
        var authenticatedLogin = hasToken ? await TryGetAuthenticatedLoginAsync(token!, ct) : null;

        try
        {
            foreach (var owner in ownerCandidates)
            {
                var orgRepos = await TryGetRepositoriesByOwnerAsync(connection, workspace, token ?? string.Empty, owner, isOrg: true, ct);
                if (orgRepos.Count > 0)
                {
                    return orgRepos;
                }

                var userRepos = await TryGetRepositoriesByOwnerAsync(connection, workspace, token ?? string.Empty, owner, isOrg: false, ct);
                if (userRepos.Count > 0)
                {
                    return userRepos;
                }
            }

            if (!string.IsNullOrWhiteSpace(authenticatedLogin) &&
                ownerCandidates.Any(x => string.Equals(x, authenticatedLogin, StringComparison.OrdinalIgnoreCase)))
            {
                var myRepos = await TryGetAuthenticatedUserRepositoriesAsync(connection, workspace, token!, ct);
                if (myRepos.Count > 0)
                {
                    return myRepos;
                }
            }

            if (!string.IsNullOrWhiteSpace(target.OwnerFromUrl) && !string.IsNullOrWhiteSpace(target.RepositoryFromUrl))
            {
                var singleRepo = await TryGetSingleRepositoryAsync(connection, workspace, token ?? string.Empty, target.OwnerFromUrl!, target.RepositoryFromUrl!, ct);
                if (singleRepo is not null)
                {
                    return new List<RepositoryEntity> { singleRepo };
                }
            }

            if (!string.IsNullOrWhiteSpace(authenticatedLogin) &&
                ownerCandidates.Count == 1 &&
                !connection.BaseUrlOrOrg.Contains('/', StringComparison.Ordinal))
            {
                var singleRepo = await TryGetSingleRepositoryAsync(connection, workspace, token ?? string.Empty, authenticatedLogin, ownerCandidates[0], ct);
                if (singleRepo is not null)
                {
                    return new List<RepositoryEntity> { singleRepo };
                }
            }

            _logger.LogWarning("GitHub repository list returned no repositories for connection {ConnectionId}. Target={Target}", connection.Id, connection.BaseUrlOrOrg);
            return new List<RepositoryEntity>();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GitHub repository listing failed for connection {ConnectionId}", connection.Id);
            return new List<RepositoryEntity>();
        }
    }

    private async Task<string?> TryGetAuthenticatedLoginAsync(string token, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/user");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Headers.UserAgent.ParseAdd("RepoAnalyzerMvp/1.0");

        using var response = await _httpClient.SendAsync(req, ct);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        return doc.RootElement.TryGetProperty("login", out var login) ? login.GetString() : null;
    }

    private async Task<List<RepositoryEntity>> TryGetAuthenticatedUserRepositoriesAsync(
        Connection connection,
        Workspace? workspace,
        string token,
        CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/user/repos?per_page=100");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Headers.UserAgent.ParseAdd("RepoAnalyzerMvp/1.0");

        using var response = await _httpClient.SendAsync(req, ct);
        if (!response.IsSuccessStatusCode)
        {
            return new List<RepositoryEntity>();
        }

        return (await ParseRepositoriesAsync(connection, workspace, response, ct)).ToList();
    }

    public async Task<IReadOnlyList<RepoFile>> GetRepositoryFilesAsync(Connection connection, RepositoryEntity repository, CancellationToken ct = default)
    {
        var owner = ExtractOwnerForRepository(connection.BaseUrlOrOrg, repository.Url);
        var repo = repository.Name.Trim();
        var token = _connectionService.GetRawToken(connection);

        var files = new List<RepoFile>();

        try
        {
            using var treeReq = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{owner}/{repo}/git/trees/HEAD?recursive=1");
            if (!string.IsNullOrWhiteSpace(token))
            {
                treeReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            }
            treeReq.Headers.UserAgent.ParseAdd("RepoAnalyzerMvp/1.0");
            using var treeResp = await _httpClient.SendAsync(treeReq, ct);

            if (!treeResp.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "GitHub file fetch failed for repository {RepositoryId}: tree API returned {StatusCode}.",
                    repository.Id,
                    (int)treeResp.StatusCode);
                return new List<RepoFile>();
            }

            await using var treeStream = await treeResp.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(treeStream, cancellationToken: ct);

            var targetPaths = doc.RootElement.GetProperty("tree")
                .EnumerateArray()
                .Where(x => x.GetProperty("type").GetString() == "blob")
                .Select(x => x.GetProperty("path").GetString() ?? string.Empty)
                .Where(path => InterestingFiles.Any(ext => path.EndsWith(ext, StringComparison.OrdinalIgnoreCase)))
                .Take(200)
                .ToList();

            foreach (var path in targetPaths)
            {
                using var fileReq = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{owner}/{repo}/contents/{Uri.EscapeDataString(path)}");
                if (!string.IsNullOrWhiteSpace(token))
                {
                    fileReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                }
                fileReq.Headers.UserAgent.ParseAdd("RepoAnalyzerMvp/1.0");

                using var fileResp = await _httpClient.SendAsync(fileReq, ct);
                if (!fileResp.IsSuccessStatusCode)
                {
                    continue;
                }

                await using var fileStream = await fileResp.Content.ReadAsStreamAsync(ct);
                using var fileDoc = await JsonDocument.ParseAsync(fileStream, cancellationToken: ct);

                if (!fileDoc.RootElement.TryGetProperty("content", out var contentElement))
                {
                    continue;
                }

                var encoded = contentElement.GetString() ?? string.Empty;
                var bytes = Convert.FromBase64String(encoded.Replace("\n", string.Empty));
                var decoded = DecodeText(bytes);
                files.Add(new RepoFile { Path = path, Content = decoded });
            }

            if (files.Count == 0)
            {
                _logger.LogWarning("GitHub file fetch completed for repository {RepositoryId}: no interesting manifests found.", repository.Id);
                return new List<RepoFile>();
            }

            return files;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GitHub repository file fetch failed for repository {RepositoryId}", repository.Id);
            return new List<RepoFile>();
        }
    }

    private static string ExtractOwnerForRepository(string connectionTarget, string repositoryUrl)
    {
        if (TryParseOwnerFromRepositoryUrl(repositoryUrl, out var ownerFromUrl))
        {
            return ownerFromUrl;
        }

        var parsed = ParseTarget(connectionTarget);
        if (!string.IsNullOrWhiteSpace(parsed.OwnerFromUrl))
        {
            return parsed.OwnerFromUrl!;
        }

        return parsed.OwnerCandidates.FirstOrDefault() ?? connectionTarget.Trim();
    }

    private async Task<List<RepositoryEntity>> TryGetRepositoriesByOwnerAsync(
        Connection connection,
        Workspace? workspace,
        string token,
        string owner,
        bool isOrg,
        CancellationToken ct)
    {
        var url = isOrg
            ? $"https://api.github.com/orgs/{owner}/repos?per_page=100"
            : $"https://api.github.com/users/{owner}/repos?per_page=100";

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        if (!string.IsNullOrWhiteSpace(token))
        {
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }
        req.Headers.UserAgent.ParseAdd("RepoAnalyzerMvp/1.0");

        using var response = await _httpClient.SendAsync(req, ct);
        if (!response.IsSuccessStatusCode)
        {
            return new List<RepositoryEntity>();
        }

        return (await ParseRepositoriesAsync(connection, workspace, response, ct)).ToList();
    }

    private async Task<RepositoryEntity?> TryGetSingleRepositoryAsync(
        Connection connection,
        Workspace? workspace,
        string token,
        string owner,
        string repositoryName,
        CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{owner}/{repositoryName}");
        if (!string.IsNullOrWhiteSpace(token))
        {
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }
        req.Headers.UserAgent.ParseAdd("RepoAnalyzerMvp/1.0");

        using var response = await _httpClient.SendAsync(req, ct);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

        return new RepositoryEntity
        {
            ConnectionId = connection.Id,
            WorkspaceId = workspace?.Id,
            Name = doc.RootElement.GetProperty("name").GetString() ?? repositoryName,
            Url = doc.RootElement.GetProperty("html_url").GetString() ?? string.Empty
        };
    }

    private static string BuildPublicProbeUrl((List<string> OwnerCandidates, string? OwnerFromUrl, string? RepositoryFromUrl) target)
    {
        if (!string.IsNullOrWhiteSpace(target.OwnerFromUrl) && !string.IsNullOrWhiteSpace(target.RepositoryFromUrl))
        {
            return $"https://api.github.com/repos/{target.OwnerFromUrl}/{target.RepositoryFromUrl}";
        }

        var owner = target.OwnerCandidates.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(owner))
        {
            return "https://api.github.com/rate_limit";
        }

        return $"https://api.github.com/users/{owner}";
    }

    private static (List<string> OwnerCandidates, string? OwnerFromUrl, string? RepositoryFromUrl) ParseTarget(string input)
    {
        var value = input.Trim();
        var owners = new List<string>();

        string? ownerFromUrl = null;
        string? repoFromUrl = null;

        if (Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
            string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase))
        {
            var parts = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 1)
            {
                ownerFromUrl = parts[0];
                owners.Add(parts[0]);
            }

            if (parts.Length >= 2 && !parts[1].Equals(".git", StringComparison.OrdinalIgnoreCase))
            {
                repoFromUrl = parts[1].Replace(".git", string.Empty, StringComparison.OrdinalIgnoreCase);
            }
        }

        if (value.Contains('/', StringComparison.Ordinal))
        {
            var parts = value.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 1)
            {
                owners.Add(parts[0]);
            }

            if (parts.Length >= 2 && string.IsNullOrWhiteSpace(repoFromUrl))
            {
                repoFromUrl = parts[1];
            }
        }
        else if (!string.IsNullOrWhiteSpace(value))
        {
            owners.Add(value);
        }

        owners = owners
            .Select(x => Regex.Replace(x.Trim(), @"\.git$", string.Empty, RegexOptions.IgnoreCase))
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return (owners, ownerFromUrl, repoFromUrl);
    }

    private static bool TryParseOwnerFromRepositoryUrl(string repositoryUrl, out string owner)
    {
        owner = string.Empty;
        if (!Uri.TryCreate(repositoryUrl, UriKind.Absolute, out var uri))
        {
            return false;
        }

        if (!string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var parts = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 1)
        {
            return false;
        }

        owner = parts[0];
        return !string.IsNullOrWhiteSpace(owner);
    }

    private static async Task<IReadOnlyList<RepositoryEntity>> ParseRepositoriesAsync(Connection connection, Workspace? workspace, HttpResponseMessage response, CancellationToken ct)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

        var result = doc.RootElement
            .EnumerateArray()
            .Select(x => new RepositoryEntity
            {
                ConnectionId = connection.Id,
                WorkspaceId = workspace?.Id,
                Name = x.GetProperty("name").GetString() ?? "unknown",
                Url = x.GetProperty("html_url").GetString() ?? string.Empty
            })
            .ToList();

        return result;
    }

    private static string DecodeText(byte[] bytes)
    {
        if (bytes.Length >= 3 &&
            bytes[0] == 0xEF &&
            bytes[1] == 0xBB &&
            bytes[2] == 0xBF)
        {
            return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        }

        if (bytes.Length >= 2 &&
            bytes[0] == 0xFF &&
            bytes[1] == 0xFE)
        {
            return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        }

        if (bytes.Length >= 2 &&
            bytes[0] == 0xFE &&
            bytes[1] == 0xFF)
        {
            return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
        }

        try
        {
            var utf8Strict = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
            var utf8 = utf8Strict.GetString(bytes);
            if (LooksLikeUtf16WithoutBom(bytes, utf8))
            {
                return DecodeUtf16WithoutBom(bytes);
            }

            return utf8;
        }
        catch
        {
            // Fallback for legacy files.
            return Encoding.Latin1.GetString(bytes);
        }
    }

    private static bool LooksLikeUtf16WithoutBom(byte[] bytes, string decodedUtf8)
    {
        if (bytes.Length < 4)
        {
            return false;
        }

        var zeroCount = bytes.Count(b => b == 0);
        if (zeroCount < bytes.Length / 5)
        {
            return false;
        }

        var replacementCharCount = decodedUtf8.Count(c => c == '\uFFFD');
        return replacementCharCount > 0 || decodedUtf8.Contains('\0');
    }

    private static string DecodeUtf16WithoutBom(byte[] bytes)
    {
        var evenZero = 0;
        var oddZero = 0;
        for (var i = 0; i < bytes.Length; i++)
        {
            if (bytes[i] != 0)
            {
                continue;
            }

            if (i % 2 == 0)
            {
                evenZero++;
            }
            else
            {
                oddZero++;
            }
        }

        return oddZero >= evenZero
            ? Encoding.Unicode.GetString(bytes)
            : Encoding.BigEndianUnicode.GetString(bytes);
    }
}
