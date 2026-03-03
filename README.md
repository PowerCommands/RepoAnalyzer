# Repo Analyzer

MVP web app for repository analysis with a server-rendered UI, internal API, encrypted PAT storage, JSON persistence, and safe manifest-based dependency checks.

## Run locally

```bash
export DOTNET_CLI_HOME=$PWD/.dotnet-home
dotnet restore
dotnet run --project RepoAnalyzer.Web
```

App URL: `http://localhost:5010` (or value from launch profile).

## Docker

```bash
docker build -t repo-analyzer:local .
docker run --rm -p 8080:8080 -v repo-analyzer-data:/app/data repo-analyzer:local
```

## Notes

- PATs are encrypted at rest using ASP.NET Core Data Protection.
- JSON persistence uses file-level locking and atomic writes.
- Analyzer parses `.csproj` and `package.json` for MVP.
- .NET vulnerability/outdated checks use `dotnet list package` in a controlled temporary directory.
- Python checks create a temporary virtual environment per scan, install `requirements.txt`, then run `pip list --outdated` and `pip-audit`, so Python runtime + `pip` must be available in the runtime environment.
- TODOs are in code for richer Azure DevOps file fetch, more ecosystems, and deeper findings support.
