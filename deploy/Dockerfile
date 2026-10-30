FROM mcr.microsoft.com/dotnet/sdk:10.0.401 AS build
WORKDIR /src
COPY Directory.Build.props global.json ./
COPY src/ ./src/
RUN dotnet restore src/WhalePod.Web/WhalePod.Web.csproj --locked-mode \
    && dotnet restore src/WhalePod.Api/WhalePod.Api.csproj --locked-mode \
    && dotnet restore src/WhalePod.Infrastructure/WhalePod.Infrastructure.csproj --locked-mode

FROM build AS publish-web
RUN dotnet publish src/WhalePod.Web/WhalePod.Web.csproj -c Release --no-restore -o /out

FROM build AS publish-api
RUN dotnet publish src/WhalePod.Api/WhalePod.Api.csproj -c Release --no-restore -o /out

FROM build AS migrate
RUN dotnet tool install --tool-path /tools dotnet-ef --version 10.0.4
ENV PATH="/tools:${PATH}"
ENTRYPOINT ["dotnet-ef", "database", "update", "--project", "src/WhalePod.Infrastructure", "--startup-project", "src/WhalePod.Infrastructure"]

FROM mcr.microsoft.com/dotnet/aspnet:10.0.4 AS runtime
WORKDIR /app
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

FROM runtime AS web
COPY --from=publish-web /out ./
ENTRYPOINT ["dotnet", "WhalePod.Web.dll"]

FROM runtime AS api
COPY --from=publish-api /out ./
ENTRYPOINT ["dotnet", "WhalePod.Api.dll"]
