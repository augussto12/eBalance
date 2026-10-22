# syntax=docker/dockerfile:1.7
# API ASP.NET Core 10 (LTS). Build multi-stage; la imagen final no contiene SDK, código fuente ni secretos.
ARG DOTNET_SDK_IMAGE=mcr.microsoft.com/dotnet/sdk:10.0.402-alpine3.24
ARG DOTNET_RUNTIME_IMAGE=mcr.microsoft.com/dotnet/aspnet:10.0.13-alpine3.24-extra

FROM ${DOTNET_SDK_IMAGE} AS build
ENV DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
WORKDIR /src
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY src/api/AlvarezFinanzas.Api/AlvarezFinanzas.Api.csproj src/api/AlvarezFinanzas.Api/packages.lock.json src/api/AlvarezFinanzas.Api/
# CA adicional OPCIONAL (solo para builds detrás de un proxy TLS corporativo); queda en la etapa de build, no en la final.
RUN --mount=type=secret,id=extra_ca,required=false \
    if [ -s /run/secrets/extra_ca ]; then cp /run/secrets/extra_ca /usr/local/share/ca-certificates/extra-ca.crt && update-ca-certificates >/dev/null; fi; \
    dotnet restore src/api/AlvarezFinanzas.Api/AlvarezFinanzas.Api.csproj --locked-mode
COPY src/api/ src/api/
RUN dotnet publish src/api/AlvarezFinanzas.Api/AlvarezFinanzas.Api.csproj -c Release --no-restore -o /out /p:UseAppHost=false \
    && rm -f /out/appsettings.Development.json

# Imagen -extra: incluye ICU y tzdata (es-AR y America/Argentina/Buenos_Aires).
FROM ${DOTNET_RUNTIME_IMAGE}
WORKDIR /app
COPY --from=build /out .
# Directorio del key ring (volumen persistente) propiedad del usuario no-root de la imagen (APP_UID=1654).
RUN mkdir -p /keys && chown "$APP_UID:$APP_UID" /keys && chmod 700 /keys
ENV ASPNETCORE_HTTP_PORTS=8080 \
    ASPNETCORE_ENVIRONMENT=Production \
    DOTNET_EnableDiagnostics=0 \
    DOTNET_gcServer=0 \
    TZ=UTC
USER $APP_UID
EXPOSE 8080
HEALTHCHECK --interval=30s --timeout=3s --start-period=20s --retries=3 \
    CMD wget -q -O /dev/null http://127.0.0.1:8080/api/health || exit 1
ENTRYPOINT ["dotnet", "AlvarezFinanzas.Api.dll"]
