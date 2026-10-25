# syntax=docker/dockerfile:1
# Publish on the builder's native architecture. The app is framework-dependent
# IL, so the same output runs on the target runtime image (linux/arm64 on the
# Oracle A1 VM, linux/amd64 elsewhere). Native dependencies would break this;
# this project has none.

FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0@sha256:0d99ebf1ccad15037b5c98c9fe8979d6b8dbac23812feae2277a0b277b5c5ffa AS build
WORKDIR /src

COPY src/Worklane.Domain/Worklane.Domain.csproj src/Worklane.Domain/
COPY src/Worklane.Application/Worklane.Application.csproj src/Worklane.Application/
COPY src/Worklane.Infrastructure/Worklane.Infrastructure.csproj src/Worklane.Infrastructure/
COPY src/Worklane.WebApi/Worklane.WebApi.csproj src/Worklane.WebApi/
COPY tests/Worklane.IntegrationTests/Worklane.IntegrationTests.csproj tests/Worklane.IntegrationTests/
RUN dotnet restore src/Worklane.WebApi/Worklane.WebApi.csproj

COPY src/ src/

RUN dotnet publish src/Worklane.WebApi/Worklane.WebApi.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0@sha256:d5633778c754e89a386882eddf8dbee6942447a8a4b8267c19cf1e1158d0b0ac AS runtime
WORKDIR /app
COPY --from=build /app .

# Named volumes at these paths are seeded from the image, including ownership.
# APP_UID in this aspnet image is 1654. A fresh volume is writable by that user.
# The key ring is mode 700. The API process reads DataProtection__KeysPath
# only after WL-63a; the directory is ready before that.
RUN mkdir -p /var/worklane/attachments /var/lib/worklane/dataprotection-keys \
    && chown -R "$APP_UID:$APP_UID" /var/worklane/attachments /var/lib/worklane/dataprotection-keys \
    && chmod 700 /var/lib/worklane/dataprotection-keys

ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

USER $APP_UID
ENTRYPOINT ["dotnet", "Worklane.WebApi.dll"]
