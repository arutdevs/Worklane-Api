# syntax=docker/dockerfile:1
# EF migrations bundle. Built when the image is built, so the running container
# does not install dotnet-ef and does not need NuGet. Framework-dependent IL
# plus a RID-specific apphost: the SDK stage runs on the builder, and -r selects
# the VM architecture.

FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0@sha256:0d99ebf1ccad15037b5c98c9fe8979d6b8dbac23812feae2277a0b277b5c5ffa AS build
ARG TARGETARCH
WORKDIR /src

COPY src/Worklane.Domain/Worklane.Domain.csproj src/Worklane.Domain/
COPY src/Worklane.Application/Worklane.Application.csproj src/Worklane.Application/
COPY src/Worklane.Infrastructure/Worklane.Infrastructure.csproj src/Worklane.Infrastructure/
COPY src/Worklane.WebApi/Worklane.WebApi.csproj src/Worklane.WebApi/
RUN dotnet restore src/Worklane.WebApi/Worklane.WebApi.csproj \
    && dotnet tool install --tool-path /tools dotnet-ef --version 10.0.13

COPY src/ src/

ENV PATH="/tools:${PATH}" \
    DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    DOTNET_NOLOGO=1 \
    NUGET_CERT_REVOCATION_MODE=offline

RUN case "$TARGETARCH" in \
      amd64) rid=linux-x64 ;; \
      arm64) rid=linux-arm64 ;; \
      *) echo "unsupported TARGETARCH=$TARGETARCH" >&2; exit 1 ;; \
    esac \
    && dotnet restore src/Worklane.WebApi/Worklane.WebApi.csproj -r "$rid" \
    && dotnet ef migrations bundle \
      --project src/Worklane.Infrastructure/Worklane.Infrastructure.csproj \
      --startup-project src/Worklane.WebApi/Worklane.WebApi.csproj \
      --configuration Release \
      --runtime "$rid" \
      --output /out/efbundle \
      --force

FROM mcr.microsoft.com/dotnet/aspnet:10.0@sha256:d5633778c754e89a386882eddf8dbee6942447a8a4b8267c19cf1e1158d0b0ac
WORKDIR /opt/worklane
# efbundle, its runtimeconfig/dlls, and the entry script.
COPY --from=build /out/ /opt/worklane/
COPY deploy/beta/migrate.sh /opt/worklane/migrate.sh
RUN chmod 0555 /opt/worklane/efbundle /opt/worklane/migrate.sh \
    && chown -R "$APP_UID:$APP_UID" /opt/worklane
USER $APP_UID
ENTRYPOINT ["/opt/worklane/migrate.sh"]
