# syntax=docker/dockerfile:1.7
FROM mcr.microsoft.com/dotnet/sdk:10.0.402@sha256:0d99ebf1ccad15037b5c98c9fe8979d6b8dbac23812feae2277a0b277b5c5ffa AS build
WORKDIR /src
COPY global.json Directory.Build.props Directory.Packages.props MarketplaceHub.sln ./
COPY src/MarketplaceHub.Api/MarketplaceHub.Api.csproj src/MarketplaceHub.Api/
COPY src/MarketplaceHub.Api/packages.lock.json src/MarketplaceHub.Api/
COPY src/MarketplaceHub.Application/MarketplaceHub.Application.csproj src/MarketplaceHub.Application/
COPY src/MarketplaceHub.Application/packages.lock.json src/MarketplaceHub.Application/
COPY src/MarketplaceHub.Domain/MarketplaceHub.Domain.csproj src/MarketplaceHub.Domain/
COPY src/MarketplaceHub.Domain/packages.lock.json src/MarketplaceHub.Domain/
COPY src/MarketplaceHub.Infrastructure/MarketplaceHub.Infrastructure.csproj src/MarketplaceHub.Infrastructure/
COPY src/MarketplaceHub.Infrastructure/packages.lock.json src/MarketplaceHub.Infrastructure/
COPY src/MarketplaceHub.Worker/MarketplaceHub.Worker.csproj src/MarketplaceHub.Worker/
COPY src/MarketplaceHub.Worker/packages.lock.json src/MarketplaceHub.Worker/
COPY tests/MarketplaceHub.Application.Tests/MarketplaceHub.Application.Tests.csproj tests/MarketplaceHub.Application.Tests/
COPY tests/MarketplaceHub.Application.Tests/packages.lock.json tests/MarketplaceHub.Application.Tests/
RUN dotnet restore MarketplaceHub.sln --locked-mode
COPY src/MarketplaceHub.Api/ src/MarketplaceHub.Api/
COPY src/MarketplaceHub.Application/ src/MarketplaceHub.Application/
COPY src/MarketplaceHub.Domain/ src/MarketplaceHub.Domain/
COPY src/MarketplaceHub.Infrastructure/ src/MarketplaceHub.Infrastructure/
COPY src/MarketplaceHub.Worker/ src/MarketplaceHub.Worker/
RUN dotnet publish src/MarketplaceHub.Api/MarketplaceHub.Api.csproj -c Release --no-restore -o /out/api \
 && dotnet publish src/MarketplaceHub.Worker/MarketplaceHub.Worker.csproj -c Release --no-restore -o /out/worker

FROM build AS validation
COPY tests/ tests/
ENTRYPOINT ["dotnet", "test", "MarketplaceHub.sln", "--no-restore", "--configuration", "Release"]

FROM mcr.microsoft.com/dotnet/aspnet:10.0.13@sha256:d5633778c754e89a386882eddf8dbee6942447a8a4b8267c19cf1e1158d0b0ac
WORKDIR /app
RUN mkdir -p /var/lib/marketplacehub/files /var/lib/marketplacehub/dp-keys \
 && chown -R ${APP_UID}:${APP_UID} /var/lib/marketplacehub
COPY --from=build --chown=${APP_UID}:${APP_UID} /out/api ./api
COPY --from=build --chown=${APP_UID}:${APP_UID} /out/worker ./worker
# API and worker both use /app as the content root in the production compose.
# Keep the shared marketplace persistence settings at that common root.
COPY --from=build --chown=${APP_UID}:${APP_UID} /out/api/appsettings*.json ./
USER ${APP_UID}
# Debugger and profiler attach stay disabled; the diagnostics IPC socket stays on so
# `dotnet-counters`/`dotnet-trace` can read the MarketplaceHub meters (docs/CI-CD-DEPLOY.md).
ENV ASPNETCORE_URLS=http://+:8080 DOTNET_EnableDiagnostics_Debugger=0 DOTNET_EnableDiagnostics_Profiler=0
EXPOSE 8080
ENTRYPOINT ["dotnet"]
CMD ["api/MarketplaceHub.Api.dll"]
