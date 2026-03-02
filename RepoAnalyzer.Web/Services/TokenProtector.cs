using Microsoft.AspNetCore.DataProtection;

namespace RepoAnalyzer.Web.Services;

public sealed class TokenProtector
{
    private readonly IDataProtector _protector;

    public TokenProtector(IDataProtectionProvider dataProtectionProvider)
    {
        _protector = dataProtectionProvider.CreateProtector("RepoAnalyzer.ConnectionToken.v1");
    }

    public string Protect(string rawToken) => _protector.Protect(rawToken);

    public string Unprotect(string encryptedToken)
    {
        if (string.IsNullOrWhiteSpace(encryptedToken))
        {
            return string.Empty;
        }

        return _protector.Unprotect(encryptedToken);
    }
}
