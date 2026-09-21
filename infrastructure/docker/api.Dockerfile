# Hatid Suki API. Build from the repository root:
#   docker build -f infrastructure/docker/api.Dockerfile -t hatidsuki-backend .

FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

# Restore first so this layer is cached until a project file changes.
COPY Directory.Build.props ./
COPY src/HatidSuki.Domain/HatidSuki.Domain.csproj src/HatidSuki.Domain/
COPY src/HatidSuki.Application/HatidSuki.Application.csproj src/HatidSuki.Application/
COPY src/HatidSuki.Infrastructure/HatidSuki.Infrastructure.csproj src/HatidSuki.Infrastructure/
COPY src/HatidSuki.Api/HatidSuki.Api.csproj src/HatidSuki.Api/
RUN dotnet restore src/HatidSuki.Api/HatidSuki.Api.csproj

COPY src/HatidSuki.Domain src/HatidSuki.Domain
COPY src/HatidSuki.Application src/HatidSuki.Application
COPY src/HatidSuki.Infrastructure src/HatidSuki.Infrastructure
COPY src/HatidSuki.Api src/HatidSuki.Api
RUN dotnet publish src/HatidSuki.Api/HatidSuki.Api.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS runtime
WORKDIR /app
COPY --from=build /app .

# Run as the unprivileged user that ships in the base image, and listen on a non-privileged port.
USER $APP_UID
ENV ASPNETCORE_HTTP_PORTS=8080 \
    ASPNETCORE_ENVIRONMENT=Production
EXPOSE 8080
ENTRYPOINT ["dotnet", "HatidSuki.Api.dll"]
