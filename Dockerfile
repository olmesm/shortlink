FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY Shortlink.sln .
COPY src/Shortlink.Core/Shortlink.Core.fsproj src/Shortlink.Core/
COPY src/Shortlink.Data/Shortlink.Data.fsproj src/Shortlink.Data/
COPY src/Shortlink.Web/Shortlink.Web.fsproj src/Shortlink.Web/
RUN dotnet restore src/Shortlink.Web/Shortlink.Web.fsproj

COPY src/ src/
RUN dotnet publish src/Shortlink.Web/Shortlink.Web.fsproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /app .

ENV SHORTLINK_PORT=8080 \
    SHORTLINK_DATA_DIR=/data
VOLUME /data
EXPOSE 8080

HEALTHCHECK --interval=30s --timeout=5s --start-period=10s \
  CMD curl -fs http://localhost:8080/rest/health || exit 1

ENTRYPOINT ["dotnet", "Shortlink.Web.dll"]
