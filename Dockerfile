# La compilación corre en la arquitectura del runner y publica para la de la imagen (amd64 o arm64).
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG TARGETARCH
# Commit de la imagen, para /salud (imagen.yml lo pasa; sin él, /salud no informa revisión).
ARG REVISION
WORKDIR /src
COPY src/DgiSimulador/DgiSimulador.csproj src/DgiSimulador/
RUN dotnet restore src/DgiSimulador/DgiSimulador.csproj -a $TARGETARCH
COPY src/ src/
RUN dotnet publish src/DgiSimulador/DgiSimulador.csproj -c Release -a $TARGETARCH -o /app --no-restore \
    ${REVISION:+-p:SourceRevisionId=$REVISION}

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
# El estado (SQLite y las claves de la CA de prueba) vive en un volumen.
RUN mkdir -p /datos && chown $APP_UID /datos
USER $APP_UID
# Dentro del contenedor escucha en todas las interfaces; quién llega lo decide el `-p` de quien lo corre (SECURITY.md).
ENV Simulador__Datos=/datos \
    Urls=http://0.0.0.0:8080
VOLUME /datos
EXPOSE 8080
# Los servicios de GitHub Actions y `docker compose up --wait` esperan a que esté sano: no hace falta sondear el puerto.
HEALTHCHECK --interval=30s --timeout=5s --start-period=60s --start-interval=1s --retries=3 \
    CMD ["dotnet", "DgiSimulador.dll", "--salud"]
ENTRYPOINT ["dotnet", "DgiSimulador.dll"]
