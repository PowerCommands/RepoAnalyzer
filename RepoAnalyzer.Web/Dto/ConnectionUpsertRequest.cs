using RepoAnalyzer.Web.Models.Enums;

namespace RepoAnalyzer.Web.Dto;

public sealed class ConnectionUpsertRequest
{
    public string? Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public ConnectionType Type { get; set; }
    public string BaseUrlOrOrg { get; set; } = string.Empty;
    public string? RawToken { get; set; }
    public DateTimeOffset? TokenExpiresAt { get; set; }
}
