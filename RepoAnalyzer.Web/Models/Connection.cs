using RepoAnalyzer.Web.Models.Enums;

namespace RepoAnalyzer.Web.Models;

public sealed class Connection
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = string.Empty;
    public ConnectionType Type { get; set; }
    public string BaseUrlOrOrg { get; set; } = string.Empty;
    public string EncryptedToken { get; set; } = string.Empty;
    public DateTimeOffset? TokenExpiresAt { get; set; }
}
