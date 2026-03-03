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

## Publish Docker container with Dockube 

```bash
build https://github.com/PowerCommands/RepoAnalyzer.git "repo-analyzer" --publish
```

## How to use

1. Open the app in browser (`http://localhost:8080` for Docker, or local launch URL when running with `dotnet run`).
2. Go to `Connections` and create a connection (GitHub or Azure DevOps Server).
3. In `Repositories`, fetch repositories from the selected connection.
4. Open a connection/workspace and use `Analyze all` or analyze individual repositories.
5. Follow progress in `Analyze`:
   - single-run status per repository
   - batch progress (`Analyzing repo X of Y`)
   - cancel support (stops after current repository completes)
6. Review results:
   - `Dashboard`: totals, risk ranking, data size, latest runs
   - `Components`: component list with filters, status, transitive details
   - `Findings`: global findings index with severity/repository filters
7. Use `Tools`:
   - `Backup`: download zip with all JSON data files
   - `Restore`: upload backup zip and restore JSON data
   - `Logs`: view/filter/download/clear analysis logs

## Notes

- PATs are encrypted at rest using ASP.NET Core Data Protection.
- JSON persistence uses file-level locking and atomic writes.
- Analyzer parses `.csproj` and `package.json` for MVP.
- .NET vulnerability/outdated checks use `dotnet list package` in a controlled temporary directory.
- Python checks create a temporary virtual environment per scan, install `requirements.txt`, then run `pip list --outdated` and `pip-audit`, so Python runtime + `pip` must be available in the runtime environment.
- TODOs are in code for richer Azure DevOps file fetch, more ecosystems, and deeper findings support.
