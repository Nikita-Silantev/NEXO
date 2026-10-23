# Сборка из корня репозитория:  docker build -f src/Nexo.Server/Dockerfile .
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Directory.Build.props ./
COPY proto/ proto/
COPY src/Nexo.Core/ src/Nexo.Core/
COPY src/Nexo.Contracts/ src/Nexo.Contracts/
COPY src/Nexo.Server/ src/Nexo.Server/
RUN dotnet publish src/Nexo.Server/Nexo.Server.csproj -c Release -o /app /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS run
WORKDIR /app
RUN apt-get update && apt-get install -y --no-install-recommends curl && rm -rf /var/lib/apt/lists/* \
 && useradd -r -u 10001 nexo && mkdir /data && chown nexo /data
COPY --from=build /app .
USER nexo
ENV ASPNETCORE_ENVIRONMENT=Production Nexo__DataDirectory=/data
EXPOSE 5080 5081
ENTRYPOINT ["dotnet", "Nexo.Server.dll"]
