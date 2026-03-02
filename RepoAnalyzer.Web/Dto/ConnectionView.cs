using RepoAnalyzer.Web.Models.Enums;

namespace RepoAnalyzer.Web.Dto;

public sealed class ConnectionView
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public ConnectionType Type { get; set; }
    public string BaseUrlOrOrg { get; set; } = string.Empty;
    public DateTimeOffset? TokenExpiresAt { get; set; }
    public bool HasToken { get; set; }
}
