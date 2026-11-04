FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY src/Worklane.Domain/Worklane.Domain.csproj src/Worklane.Domain/
COPY src/Worklane.Application/Worklane.Application.csproj src/Worklane.Application/
COPY src/Worklane.Infrastructure/Worklane.Infrastructure.csproj src/Worklane.Infrastructure/
COPY src/Worklane.WebApi/Worklane.WebApi.csproj src/Worklane.WebApi/
COPY tests/Worklane.IntegrationTests/Worklane.IntegrationTests.csproj tests/Worklane.IntegrationTests/
RUN dotnet restore src/Worklane.WebApi/Worklane.WebApi.csproj

COPY src/ src/

RUN dotnet publish src/Worklane.WebApi/Worklane.WebApi.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app .

# uid 1654 cannot create these on a read-only root. An empty named volume is
# root-owned, so the directories are created here and Docker copies that
# ownership into a new volume.
USER root
RUN mkdir -p /var/lib/worklane/dataprotection-keys /var/worklane/attachments \
    && chown -R 1654:1654 /var/lib/worklane /var/worklane \
    && chmod 700 /var/lib/worklane/dataprotection-keys /var/worklane/attachments
USER app

ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

ENTRYPOINT ["dotnet", "Worklane.WebApi.dll"]
