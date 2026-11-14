# Arayüz: React + Vite derlemesi
FROM node:20-alpine AS web
WORKDIR /src/web
COPY web/package.json web/package-lock.json ./
RUN npm ci
COPY web/ ./
RUN npm run build

# API: .NET yayını
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS api
WORKDIR /src
COPY src/DuseyGuzergah.Core/DuseyGuzergah.Core.csproj src/DuseyGuzergah.Core/
COPY src/DuseyGuzergah.Api/DuseyGuzergah.Api.csproj src/DuseyGuzergah.Api/
RUN dotnet restore src/DuseyGuzergah.Api/DuseyGuzergah.Api.csproj
COPY src/ src/
RUN dotnet publish src/DuseyGuzergah.Api/DuseyGuzergah.Api.csproj -c Release -o /out --no-restore

# Çalışma imajı
FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=api /out ./
COPY --from=web /src/web/dist ./spa
RUN mkdir -p /data/sessions && chown -R $APP_UID /data

ENV ASPNETCORE_HTTP_PORTS=8080 \
    ASPNETCORE_ENVIRONMENT=Production \
    HttpsRedirection=false \
    SpaPath=/app/spa \
    Sessions__RootPath=/data/sessions

USER $APP_UID
EXPOSE 8080
VOLUME ["/data"]
ENTRYPOINT ["dotnet", "DuseyGuzergah.Api.dll"]
