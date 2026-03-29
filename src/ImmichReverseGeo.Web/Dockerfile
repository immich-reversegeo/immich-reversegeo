FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY Directory.Build.props ./
COPY global.json ./
COPY src/ImmichReverseGeo.Core/ImmichReverseGeo.Core.csproj src/ImmichReverseGeo.Core/
COPY src/ImmichReverseGeo.Overture/ImmichReverseGeo.Overture.csproj src/ImmichReverseGeo.Overture/
COPY src/ImmichReverseGeo.Web/ImmichReverseGeo.Web.csproj src/ImmichReverseGeo.Web/
RUN dotnet restore src/ImmichReverseGeo.Web/ImmichReverseGeo.Web.csproj

COPY src/ src/
RUN dotnet publish src/ImmichReverseGeo.Web/ImmichReverseGeo.Web.csproj \
    -c Release -o /app/publish --no-restore /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled-extra AS runtime
WORKDIR /app

COPY --from=build /app/publish ./

ENV ASPNETCORE_URLS=http://+:8080
ENV DATA_DIR=/data
ENV CONFIG_DIR=/config

EXPOSE 8080

ENTRYPOINT ["dotnet", "ImmichReverseGeo.Web.dll"]
