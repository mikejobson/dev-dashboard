# DevDash hub: ASP.NET Core 10 + Blazor. Published framework-dependent onto the aspnet runtime image.

# The build stage always runs on the builder's own architecture and cross-compiles for the target,
# so a multi-arch build (linux/amd64 + linux/arm64) never runs the .NET SDK under emulation.
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG TARGETARCH
# Set by the release workflow from the git tag; empty means the version in Directory.Build.props.
ARG VERSION=
WORKDIR /src

# Restore first so the package layer is cached until a project file changes.
COPY Directory.Build.props ./
COPY src/DevDash.Contracts/DevDash.Contracts.csproj src/DevDash.Contracts/
COPY src/DevDash.Core/DevDash.Core.csproj src/DevDash.Core/
COPY src/DevDash.Hub/DevDash.Hub.csproj src/DevDash.Hub/
RUN dotnet restore src/DevDash.Hub/DevDash.Hub.csproj -a "$TARGETARCH"

COPY src/DevDash.Contracts/ src/DevDash.Contracts/
COPY src/DevDash.Core/ src/DevDash.Core/
COPY src/DevDash.Hub/ src/DevDash.Hub/
# No --no-restore: the SDK only adds the package that ships _framework/blazor.web.js once it can see
# .razor files, which the project-files-only restore above can't. Without it every page is static.
RUN dotnet publish src/DevDash.Hub/DevDash.Hub.csproj -c Release -a "$TARGETARCH" --self-contained false -o /app ${VERSION:+-p:Version=$VERSION}

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

# SQLite database and other hub state live here; mount a volume over it.
RUN mkdir -p /data && chown "$APP_UID" /data

COPY --from=build /app ./

ENV ASPNETCORE_ENVIRONMENT=Production \
    DOTNET_RUNNING_IN_CONTAINER=true \
    Hub__DataDirectory=/data \
    Urls=http://127.0.0.2:8080 \
    ASPNETCORE_HTTP_PORTS=

USER $APP_UID
VOLUME ["/data"]
ENTRYPOINT ["dotnet", "DevDash.Hub.dll"]
