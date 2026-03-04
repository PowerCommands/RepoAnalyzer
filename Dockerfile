FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY RepoAnalyzer.sln ./
COPY RepoAnalyzer.Web/RepoAnalyzer.Web.csproj RepoAnalyzer.Web/
RUN dotnet restore RepoAnalyzer.Web/RepoAnalyzer.Web.csproj

COPY . .
RUN dotnet publish RepoAnalyzer.Web/RepoAnalyzer.Web.csproj -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS final
WORKDIR /app

RUN apt-get update \
    && apt-get install -y --no-install-recommends nodejs npm ca-certificates maven openjdk-17-jre-headless python3 python3-pip python3-venv python-is-python3 \
    && rm -rf /var/lib/apt/lists/*

RUN useradd -m -u 10001 appuser \
    && mkdir -p /app/data /app/data/keys \
    && chown -R appuser:appuser /app

COPY --from=build /app/publish .

ENV ASPNETCORE_URLS=http://+:8080
ENV ASPNETCORE_ENVIRONMENT=Production
ENV DataPath=/app/data

USER appuser
EXPOSE 8080
ENTRYPOINT ["dotnet", "RepoAnalyzer.Web.dll"]
