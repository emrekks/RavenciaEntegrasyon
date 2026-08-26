# syntax=docker/dockerfile:1.7
FROM mcr.microsoft.com/dotnet/sdk:10.0.302@sha256:ed034a8bf0b24ded0cbbac07e17825d8e9ebfe21e308191d0f7421eaf5ad4664 AS build
WORKDIR /src
COPY global.json Directory.Build.props Directory.Packages.props MarketplaceHub.sln ./
COPY src/MarketplaceHub.Api/MarketplaceHub.Api.csproj src/MarketplaceHub.Api/
COPY src/MarketplaceHub.Application/MarketplaceHub.Application.csproj src/MarketplaceHub.Application/
COPY src/MarketplaceHub.Domain/MarketplaceHub.Domain.csproj src/MarketplaceHub.Domain/
COPY src/MarketplaceHub.Infrastructure/MarketplaceHub.Infrastructure.csproj src/MarketplaceHub.Infrastructure/
COPY src/MarketplaceHub.Worker/MarketplaceHub.Worker.csproj src/MarketplaceHub.Worker/
COPY tests/MarketplaceHub.Application.Tests/MarketplaceHub.Application.Tests.csproj tests/MarketplaceHub.Application.Tests/
COPY tests/MarketplaceHub.Application.Tests/packages.lock.json tests/MarketplaceHub.Application.Tests/
RUN dotnet restore MarketplaceHub.sln --locked-mode
COPY src/ src/
RUN dotnet publish src/MarketplaceHub.Api/MarketplaceHub.Api.csproj -c Release --no-restore -o /out/api \
 && dotnet publish src/MarketplaceHub.Worker/MarketplaceHub.Worker.csproj -c Release --no-restore -o /out/worker

FROM mcr.microsoft.com/dotnet/aspnet:10.0.10@sha256:1fa23fc4872d95fd71c2833ebe65d7e84a43b2d51a31d119516852f13d9505a7
WORKDIR /app
RUN mkdir -p /var/lib/marketplacehub/files /var/lib/marketplacehub/dp-keys \
 && chown -R ${APP_UID}:${APP_UID} /var/lib/marketplacehub
COPY --from=build --chown=${APP_UID}:${APP_UID} /out/api ./api
COPY --from=build --chown=${APP_UID}:${APP_UID} /out/worker ./worker
# API and worker both use /app as the content root in the production compose.
# Keep the shared marketplace persistence settings at that common root.
COPY --from=build --chown=${APP_UID}:${APP_UID} /out/api/appsettings*.json ./
USER ${APP_UID}
ENV ASPNETCORE_URLS=http://+:8080 DOTNET_EnableDiagnostics=0
EXPOSE 8080
ENTRYPOINT ["dotnet"]
CMD ["api/MarketplaceHub.Api.dll"]
