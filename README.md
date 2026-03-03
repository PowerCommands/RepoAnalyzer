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

## Connection setup

### GitHub

- `Name`: free text label shown in the UI.
- `Type`: `GitHub`.
- `Base URL or Org/User`: enter GitHub owner/org (for example `weidylan`), or full API/base URL when needed.
- `PAT`: optional.
- If `PAT` is empty, Repo Analyzer assumes public repositories and runs unauthenticated requests.

### Azure DevOps Server (ADS)

- `Name`: free text label shown in the UI.
- `Type`: `Azure DevOps Server`.
- `Base URL or Org/User`: Azure DevOps Server base URL, for example `https://ado.company.local/tfs` (or collection URL used in your environment).
- `PAT`: normally required for ADS.
- Workspace selection is available when fetching repositories.

## Supported package distributions

Repo Analyzer currently supports these ecosystems for component extraction and vulnerability/outdated analysis:

- NuGet (`.csproj`, `packages.config`, `Directory.Packages.props`)
- npm (`package.json`, `package-lock.json`)
- PyPI (`requirements.txt`)
- Maven (`pom.xml`)

Current tool versions in the Docker runtime image:

- .NET SDK: `8.0.418`
- NuGet CLI (via `dotnet nuget`): `6.11.1.2`
- Node.js: `v18.20.4`
- npm: `9.2.0`
- Python: `3.11.2`
- pip: `23.0.1`
- Maven: `3.8.7`

## Notes

- PATs are encrypted at rest using ASP.NET Core Data Protection.
- JSON persistence uses file-level locking and atomic writes.
- Analyzer parses `.csproj`, `packages.config`, `Directory.Packages.props`, `package.json`, `requirements.txt`, and `pom.xml`.
- .NET vulnerability/outdated checks use `dotnet list package` in a controlled temporary directory.
- Python checks create a temporary virtual environment per scan, install `requirements.txt`, then run `pip list --outdated` and `pip-audit`, so Python runtime + `pip` must be available in the runtime environment.
- TODOs are in code for richer Azure DevOps file fetch, more ecosystems, and deeper findings support.
