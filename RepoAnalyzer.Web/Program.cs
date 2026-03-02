using Microsoft.AspNetCore.DataProtection;
using MudBlazor.Services;
using RepoAnalyzer.Web.Components;
using RepoAnalyzer.Web.Services;
using RepoAnalyzer.Web.Services.Analysis;
using RepoAnalyzer.Web.Services.Analysis.Logging;
using RepoAnalyzer.Web.Services.Providers;
using RepoAnalyzer.Web.Services.Storage;

var builder = WebApplication.CreateBuilder(args);

var dataPath = builder.Configuration["DataPath"] ?? "/app/data";
Directory.CreateDirectory(dataPath);
Directory.CreateDirectory(Path.Combine(dataPath, "keys"));
Directory.CreateDirectory(Path.Combine(dataPath, "logs"));

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
builder.Services.AddMudServices();

builder.Services.AddHttpContextAccessor();
builder.Services.AddHttpClient(nameof(InternalApiClient));
builder.Services.AddHttpClient(nameof(GitHubProvider));
builder.Services.AddHttpClient(nameof(AzureDevOpsServerProvider));

builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(dataPath, "keys")));

builder.Services.AddSingleton<JsonFileStore>();
builder.Services.AddScoped<AppDataService>();
builder.Services.AddScoped<TokenProtector>();
builder.Services.AddScoped<ConnectionService>();
builder.Services.AddScoped<ConnectionValidationService>();
builder.Services.AddScoped<RepositorySyncService>();
builder.Services.AddScoped<QueryService>();
builder.Services.AddScoped<RepositoryAnalyzerService>();
builder.Services.AddSingleton<AnalyzeRunService>();
builder.Services.AddSingleton<IAnalysisLog, AnalysisLogService>();
builder.Services.AddScoped<DotNetCliInspector>();
builder.Services.AddScoped<NodeCliInspector>();
builder.Services.AddScoped<PythonCliInspector>();
builder.Services.AddScoped<JavaCliInspector>();
builder.Services.AddSingleton<SafeCliRunner>();
builder.Services.AddScoped<GitProviderFactory>();
builder.Services.AddScoped<GitHubProvider>();
builder.Services.AddScoped<AzureDevOpsServerProvider>();
builder.Services.AddScoped<InternalApiClient>();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseStaticFiles();
app.UseAntiforgery();

app.MapInternalApi();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
