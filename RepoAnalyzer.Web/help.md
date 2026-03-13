# Repo Analyzer Help

Repo Analyzer helps you discover package dependencies, review findings, manage hosted feed packages, and keep your data backed up.

## 1. Start with Connections

Before you can analyze anything, you need at least one connection.

Open the **Connections** tab and create a connection with:

- **Name**: A friendly name shown in the UI.
- **Type**: `GitHub` or `Azure DevOps Server`.
- **Base URL or Org/User**:
  - For GitHub, enter the organization or user name.
  - For Azure DevOps Server, enter the base server URL.
- **PAT**: Optional for public GitHub repositories, usually required for Azure DevOps Server.
- **Token expiry**: Optional but recommended if you want expiration alerts.

After saving, use **Test connection** to verify that the connection works.

## 2. Add Repositories

Once a connection exists, open the **Repos** tab.

This tab lets you fetch repositories from the selected connection and workspace.

Typical flow:

1. Select a connection.
2. If you use Azure DevOps Server, select one or more workspaces/projects.
3. Click **Fetch repositories**.

Fetched repositories are stored locally so you can analyze them later.

## 3. Analyze Repositories

Repo Analyzer supports multiple ways to start analysis.

### From Dashboard

The **Dashboard** shows:

- total repositories
- analyzed vs not analyzed
- total vulnerabilities
- total outdated packages
- total stored data size
- hosted feed package count

You can also:

- analyze the next repository that has not been analyzed yet
- analyze all repositories that are still not analyzed

### From Repos

In **Repos**, each repository has actions such as:

- **Analyze** / **Re-Analyze**
- **View components**
- **SBOM**
- **Delete**

Use this when you want to work repository by repository.

### From Analyze

The **Analyze** tab is the focused execution view.

Here you can:

- choose a connection
- choose a repository
- start analysis
- follow progress
- inspect the latest analysis result for the selected repository

This is the best place when you want to monitor a single run in detail.

## 4. Understand Components

The **Components** tab shows the latest discovered packages from analyzed repositories.

You can:

- search by package name, ecosystem, or version
- filter by severity
- filter by repository
- inspect where a component was found
- review inferred transitive dependencies
- add a package to the local feed

This view is useful when you want to understand what has been installed across your repositories.

## 5. Understand Findings

The **Findings** tab is the global findings index.

It combines vulnerability and outdated information across analyzed repositories.

Use it to:

- filter by ecosystem
- filter by severity
- filter by repository
- inspect affected locations
- review transitive impact

This is the best view when you want to answer questions such as:

- Which packages are vulnerable?
- Which repositories are affected?
- Which issues are direct and which are inherited?

## 6. Work with Feeds

The **Feeds** tab is the administration area for locally hosted packages.

Today it supports **NuGet**, but the design is prepared for more feed types later.

### Feed source URL

At the top of the page you will see the feed URL that can be used in Visual Studio or another NuGet client.

You can use the copy button to copy the URL directly to the clipboard.

### Import packages

You can import packages in two ways:

- directly from the **Feeds** tab with **Import package**
- from the **Components** tab with **Add to local Feed**

When importing from **Components**, the dialog can help you start from the currently selected package and version.

### Scan hosted packages

Use **Scan components** to evaluate all packages in the selected feed.

This updates:

- vulnerability status
- outdated status
- latest known version

### Update and delete hosted packages

Each hosted package row includes actions to:

- expand details
- update the package
- download the stored package
- delete the package from the feed

Details show the full package description, stored file path, full timestamps, and extracted package metadata.

## 7. Create and Manage SBOM Files

In the **Tools** tab you can generate CycloneDX SBOM files from the latest analyzed dependency data.

You can:

- choose connection and repository
- choose CycloneDX version
- choose output type
- choose JSON or XML
- create, download, and delete SBOM files

## 8. Backup and Restore Data

The **Tools** tab also includes backup and restore support.

Use it to:

- download a backup of stored JSON data
- restore the application state from a previous backup

This is useful before upgrades, experiments, or when moving data between environments.

## 9. Review Logs

The **Tools** tab contains the application log view.

You can:

- read recent log entries
- filter and sort log data
- download the current log
- clear the log

Feed operations, repository operations, and analysis operations can all appear here depending on the feature being used.

## 10. Review Runtime and Tool Versions

The **Tools** tab also shows the currently supported distributions and runtime/tooling information, such as:

- NuGet / .NET SDK
- npm / Node.js
- Maven / Java
- Python / pip

Use this section when you want to confirm what versions are available in the current runtime environment.

## Suggested First-Time Workflow

If you are new to the application, this is a good starting sequence:

1. Create and test a connection in **Connections**.
2. Fetch repositories in **Repos**.
3. Analyze one repository in **Analyze** or **Repos**.
4. Review results in **Components** and **Findings**.
5. Open **Feeds** if you want to host or update local packages.
6. Use **Tools** for SBOM, logs, backup, and runtime information.
