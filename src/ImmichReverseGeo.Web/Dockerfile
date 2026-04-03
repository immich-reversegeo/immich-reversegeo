FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
ARG TARGETARCH

COPY Directory.Build.props ./
COPY global.json ./
COPY src/ImmichReverseGeo.Core/ImmichReverseGeo.Core.csproj src/ImmichReverseGeo.Core/
COPY src/ImmichReverseGeo.Overture/ImmichReverseGeo.Overture.csproj src/ImmichReverseGeo.Overture/
COPY src/ImmichReverseGeo.Web/ImmichReverseGeo.Web.csproj src/ImmichReverseGeo.Web/
RUN case "$TARGETARCH" in \
        "amd64") export RID="linux-x64" ;; \
        "arm64") export RID="linux-arm64" ;; \
        *) echo "Unsupported TARGETARCH: $TARGETARCH" && exit 1 ;; \
    esac \
    && dotnet restore src/ImmichReverseGeo.Web/ImmichReverseGeo.Web.csproj -r "$RID"

COPY src/ src/
RUN case "$TARGETARCH" in \
        "amd64") export RID="linux-x64" ;; \
        "arm64") export RID="linux-arm64" ;; \
        *) echo "Unsupported TARGETARCH: $TARGETARCH" && exit 1 ;; \
    esac \
    && dotnet publish src/ImmichReverseGeo.Web/ImmichReverseGeo.Web.csproj \
        -c Release -o /app/publish --no-restore -r "$RID" --self-contained false /p:UseAppHost=false
RUN mkdir -p /volume-seed/config/dataprotection-keys /volume-seed/data \
    && touch /volume-seed/config/.volume-init /volume-seed/data/.volume-init \
    && chown -R "$APP_UID":"$APP_UID" /volume-seed

FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled-extra AS runtime
WORKDIR /app

COPY --from=build --chown=$APP_UID:$APP_UID /app/publish ./
COPY --from=build --chown=$APP_UID:$APP_UID /src/src/ImmichReverseGeo.Web/bundled-data/ /app/bundled-data/
COPY --from=build --chown=$APP_UID:$APP_UID /volume-seed/config/ /config/
COPY --from=build --chown=$APP_UID:$APP_UID /volume-seed/data/ /data/

ENV ASPNETCORE_URLS=http://+:8080
ENV DATA_DIR=/data
ENV CONFIG_DIR=/config

USER $APP_UID

EXPOSE 8080

ENTRYPOINT ["dotnet", "ImmichReverseGeo.Web.dll"]
