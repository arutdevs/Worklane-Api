# syntax=docker/dockerfile:1
# Framework-dependent IL, published on the builder's native architecture.
# The runtime image's dotnet host runs it on amd64 and arm64.
# `dotnet ef migrations bundle --runtime <foreign-rid>` is not used: that
# command loads the startup assembly for the bundle RID, and an amd64 SDK
# cannot load the arm64 build (FileNotFoundException after a successful
# compile). Running the SDK stage under qemu also fails (the arm64 SDK aborts
# during restore). Worklane.Migrator calls Database.Migrate() instead.

FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0@sha256:0d99ebf1ccad15037b5c98c9fe8979d6b8dbac23812feae2277a0b277b5c5ffa AS build
WORKDIR /src

COPY src/Worklane.Domain/Worklane.Domain.csproj src/Worklane.Domain/
COPY src/Worklane.Application/Worklane.Application.csproj src/Worklane.Application/
COPY src/Worklane.Infrastructure/Worklane.Infrastructure.csproj src/Worklane.Infrastructure/
COPY src/Worklane.Migrator/Worklane.Migrator.csproj src/Worklane.Migrator/
RUN dotnet restore src/Worklane.Migrator/Worklane.Migrator.csproj

COPY src/ src/

ENV DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    DOTNET_NOLOGO=1 \
    NUGET_CERT_REVOCATION_MODE=offline

RUN dotnet publish src/Worklane.Migrator/Worklane.Migrator.csproj \
    -c Release \
    -o /out \
    --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0@sha256:d5633778c754e89a386882eddf8dbee6942447a8a4b8267c19cf1e1158d0b0ac
WORKDIR /opt/worklane
COPY --from=build /out/ /opt/worklane/
COPY deploy/beta/migrate.sh /opt/worklane/migrate.sh
RUN chmod 0555 /opt/worklane/migrate.sh \
    && chown -R "$APP_UID:$APP_UID" /opt/worklane
USER $APP_UID
ENTRYPOINT ["/opt/worklane/migrate.sh"]
