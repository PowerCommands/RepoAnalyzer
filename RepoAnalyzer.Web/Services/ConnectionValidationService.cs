using RepoAnalyzer.Web.Dto;
using RepoAnalyzer.Web.Services.Providers;

namespace RepoAnalyzer.Web.Services;

public sealed class ConnectionValidationService
{
    private readonly ConnectionService _connectionService;
    private readonly GitProviderFactory _providerFactory;

    public ConnectionValidationService(ConnectionService connectionService, GitProviderFactory providerFactory)
    {
        _connectionService = connectionService;
        _providerFactory = providerFactory;
    }

    public async Task<ConnectionTestResult> TestConnectionAsync(string id, CancellationToken ct = default)
    {
        var connection = await _connectionService.GetRawByIdAsync(id, ct);
        if (connection is null)
        {
            return new ConnectionTestResult { Success = false, Message = "Connection not found." };
        }

        var provider = _providerFactory.Resolve(connection.Type);
        var result = await provider.TestConnectionAsync(connection, ct);
        return new ConnectionTestResult { Success = result.Success, Message = result.Message };
    }
}
