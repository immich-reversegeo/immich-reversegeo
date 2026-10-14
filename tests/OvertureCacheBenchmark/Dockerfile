FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Directory.Build.props global.json ./
COPY src/ImmichReverseGeo.Core/ src/ImmichReverseGeo.Core/
COPY src/ImmichReverseGeo.Overture/ src/ImmichReverseGeo.Overture/
COPY tests/OvertureCacheBenchmark/ tests/OvertureCacheBenchmark/
RUN dotnet publish tests/OvertureCacheBenchmark/OvertureCacheBenchmark.csproj -c Release -o /app/publish
RUN mkdir /extensions && dotnet /app/publish/OvertureCacheBenchmark.dll --install-extensions

FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled-extra
WORKDIR /app
COPY --from=build /app/publish/ ./
COPY --from=build /extensions/ /extensions/
USER $APP_UID
ENTRYPOINT ["dotnet", "OvertureCacheBenchmark.dll"]
