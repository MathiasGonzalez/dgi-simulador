FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/DgiSimulador/DgiSimulador.csproj src/DgiSimulador/
RUN dotnet restore src/DgiSimulador/DgiSimulador.csproj
COPY src/ src/
RUN dotnet publish src/DgiSimulador/DgiSimulador.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
# El estado (SQLite y las claves de la CA de prueba) vive en un volumen.
RUN mkdir -p /datos && chown $APP_UID /datos
USER $APP_UID
ENV Simulador__Datos=/datos \
    Urls=http://0.0.0.0:8080
VOLUME /datos
EXPOSE 8080
ENTRYPOINT ["dotnet", "DgiSimulador.dll"]
