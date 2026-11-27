# SmartHotel API image.
#
# The build context is the repository root, filtered by the root .dockerignore allowlist, so the shared build
# files (global.json, Directory.Build.props, Directory.Packages.props, .editorconfig) are available:
#   docker build -f src/SmartHotel.Api/Dockerfile .

FROM mcr.microsoft.com/dotnet/sdk:8.0.428-alpine3.24 AS build
WORKDIR /src

# Restore from the project files alone, so the restore layer is reused until a project or a pin changes.
# The project is restored and published, never SmartHotel.sln: the solution also lists the test projects,
# which are not in the build context.
COPY global.json Directory.Build.props Directory.Packages.props .editorconfig ./
COPY src/SmartHotel.Data/SmartHotel.Data.csproj src/SmartHotel.Data/
COPY src/SmartHotel.Api/SmartHotel.Api.csproj src/SmartHotel.Api/
RUN dotnet restore src/SmartHotel.Api/SmartHotel.Api.csproj

COPY src/SmartHotel.Data/ src/SmartHotel.Data/
COPY src/SmartHotel.Api/ src/SmartHotel.Api/

# TreatWarningsAsErrors from Directory.Build.props applies, so any warning fails the image build.
RUN dotnet publish src/SmartHotel.Api/SmartHotel.Api.csproj -c Release --no-restore -o /app/publish /p:UseAppHost=false

# A Windows checkout with CRLF line endings would break the script's shebang line.
RUN sed -i 's/\r$//' src/SmartHotel.Api/docker-entrypoint.sh && chmod +x src/SmartHotel.Api/docker-entrypoint.sh

# The alpine runtime runs .NET in invariant globalization mode. The API uses only ordinal and invariant-culture
# operations, so no ICU package is installed. BusyBox wget, which the Compose healthcheck calls, is already present.
FROM mcr.microsoft.com/dotnet/aspnet:8.0.34-alpine3.24 AS runtime
WORKDIR /app

COPY --from=build /app/publish ./
COPY --from=build /src/src/SmartHotel.Api/docker-entrypoint.sh /app/docker-entrypoint.sh

# ASPNETCORE_HTTP_PORTS defaults to 8080 in this base image. ASPNETCORE_ENVIRONMENT is deliberately not set: Compose
# sets Development locally, and an unset value means Production everywhere else. Secrets arrive at run time only,
# from the environment or through docker-entrypoint.sh, never as ENV or ARG here.
EXPOSE 8080

# The non-root user that the .NET 8 images define (UID 1654).
USER app

ENTRYPOINT ["/app/docker-entrypoint.sh"]
