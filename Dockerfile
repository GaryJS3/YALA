FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/YALA/YALA.csproj src/YALA/
RUN dotnet restore src/YALA/YALA.csproj
COPY src/YALA/ src/YALA/
# Dockhand's direct-host staging can represent binary assets as base64-prefixed
# text. Normalize only those files while leaving ordinary Git checkouts intact.
RUN find src/YALA/wwwroot -type f \( -name '*.png' -o -name '*.jpg' -o -name '*.jpeg' -o -name '*.webp' \) -exec sh -c \
    'for file do if [ "$(head -c 7 "$file")" = "base64:" ]; then tail -c +8 "$file" | base64 -d > "$file.decoded" && mv "$file.decoded" "$file"; fi; done' sh {} +
RUN dotnet publish src/YALA/YALA.csproj --configuration Release --output /app/publish --no-restore /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
RUN apt-get update \
    && apt-get install --yes --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/* \
    && mkdir -p /data \
    && chown -R $APP_UID:$APP_UID /data
COPY --from=build /app/publish .
ENV ASPNETCORE_URLS=http://+:8080 \
    ASPNETCORE_HTTP_PORTS=8080 \
    YALA_DATA_DIR=/data \
    DOTNET_EnableDiagnostics=0
EXPOSE 8080
USER $APP_UID
HEALTHCHECK --interval=30s --timeout=3s --start-period=15s --retries=3 CMD curl --fail --silent http://127.0.0.1:8080/health || exit 1
ENTRYPOINT ["dotnet", "YALA.dll"]
