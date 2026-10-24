# syntax=docker/dockerfile:1

# PC Diagnostic web host (UI + API) and the cross-platform build/test pipeline.
#
# The recorder Agent is NOT containerized: it must observe the Windows host itself (performance counters,
# GPU drivers, display configuration, event logs, sensors), which a container cannot do.
#
#   docker build --target test .        run all cross-platform tests (.NET + front end)
#   docker compose up -d --build        run the web host on http://localhost:5180

ARG DOTNET_VERSION=10.0
ARG NODE_VERSION=24

# ---- UI: build and unit-test the React app -------------------------------------------------------------
FROM node:${NODE_VERSION}-bookworm-slim AS ui
WORKDIR /src/web
COPY web/package.json web/package-lock.json ./
RUN npm ci --no-audit --no-fund
COPY web/ ./
RUN npm run build

FROM ui AS ui-test
RUN npm test

# ---- .NET: restore (cached on project files), then build -----------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:${DOTNET_VERSION} AS build
WORKDIR /src
COPY global.json Directory.Build.props Directory.Packages.props .editorconfig ./
COPY src/PcDiag.Core/PcDiag.Core.csproj src/PcDiag.Core/
COPY src/PcDiag.Storage.Sqlite/PcDiag.Storage.Sqlite.csproj src/PcDiag.Storage.Sqlite/
COPY src/PcDiag.Hosting/PcDiag.Hosting.csproj src/PcDiag.Hosting/
COPY src/PcDiag.Web/PcDiag.Web.csproj src/PcDiag.Web/
COPY src/PcDiag.Engine/PcDiag.Engine.csproj src/PcDiag.Engine/
COPY tests/PcDiag.Core.Tests/PcDiag.Core.Tests.csproj tests/PcDiag.Core.Tests/
COPY tests/PcDiag.Storage.Tests/PcDiag.Storage.Tests.csproj tests/PcDiag.Storage.Tests/
COPY tests/PcDiag.Engine.Tests/PcDiag.Engine.Tests.csproj tests/PcDiag.Engine.Tests/
COPY tests/PcDiag.Web.Tests/PcDiag.Web.Tests.csproj tests/PcDiag.Web.Tests/
# Cross-platform test projects only. Windows-only projects (Agent, collectors, collector integration tests)
# need a Windows host and run in the Windows pipeline.
ENV CROSS_PLATFORM_TESTS="PcDiag.Core.Tests PcDiag.Storage.Tests PcDiag.Engine.Tests PcDiag.Web.Tests"
RUN for t in $CROSS_PLATFORM_TESTS; do dotnet restore "tests/$t/$t.csproj" -p:SkipWebBuild=true || exit 1; done
COPY src/ src/
COPY tests/ tests/
# The editor's test character: the Core tests read it too (the editor and the recorder read the same file).
COPY web/src/features/overlay/characters/test.char web/src/features/overlay/characters/
# The characters' reactions to readings: one table, read by the editor and embedded in PcDiag.Core for the recorder.
COPY web/src/features/overlay/characters/reactions.json web/src/features/overlay/characters/

# ---- Tests: Windows-only projects (Agent, collectors) are excluded; they run in the Windows pipeline ------
FROM build AS test
COPY --from=ui-test /src/web/dist /src/web/dist
RUN for t in $CROSS_PLATFORM_TESTS; do \
      dotnet test "tests/$t/$t.csproj" -c Release --no-restore -p:SkipWebBuild=true --logger "console;verbosity=minimal" || exit 1; \
    done

# ---- Publish the web host ------------------------------------------------------------------------------
FROM build AS publish
RUN dotnet publish src/PcDiag.Web/PcDiag.Web.csproj -c Release -o /app --no-restore -p:SkipWebBuild=true

# ---- Runtime: non-root, data on a volume ---------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/aspnet:${DOTNET_VERSION} AS runtime
WORKDIR /app
ENV ASPNETCORE_ENVIRONMENT=Production \
    Storage__DataDirectory=/data \
    Web__Port=8080 \
    ASPNETCORE_HTTP_PORTS= \
    DOTNET_gcServer=0
RUN mkdir -p /data && chown "$APP_UID" /data
COPY --from=publish /app ./
COPY --from=ui /src/web/dist ./wwwroot
USER $APP_UID
VOLUME ["/data"]
EXPOSE 8080
HEALTHCHECK --interval=30s --timeout=5s --start-period=15s --retries=3 \
  CMD ["dotnet", "PcDiag.Web.dll", "--healthcheck"]
ENTRYPOINT ["dotnet", "PcDiag.Web.dll"]
